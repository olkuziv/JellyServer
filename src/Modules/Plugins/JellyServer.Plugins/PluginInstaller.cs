using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JellyServer.Plugins;

/// <summary>
/// Downloads a plugin package (a zip), checks it and unpacks it into the plugins directory. Nothing is loaded here: plugins load at
/// startup, so a fresh install waits for a restart. The zip is untrusted input: the MD5 from the repository is checked, entries cannot
/// leave the plugin's folder and the size is capped.
/// </summary>
internal sealed partial class PluginInstaller(IHttpClientFactory http, IConfiguration configuration, ILogger<PluginInstaller> log)
{
    public const string StagingFolder = ".staging";
    private const int MaxEntries = 5000;

    private readonly SemaphoreSlim _one = new(1, 1);

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex Unsafe();

    private long MaxPackageBytes => Math.Max(1, configuration.GetValue("Plugins:MaxPackageMB", 100)) * 1024L * 1024;

    /// <summary>Installs <paramref name="package"/> at <paramref name="version"/> (from the repository it was listed in) and returns the plugin's folder.</summary>
    public async Task<string> InstallAsync(string directory, PackageInfoDto package, PackageVersionDto version, CancellationToken ct)
    {
        if (version.SourceUrl is not { Length: > 0 } source) throw new InvalidOperationException("the package version has no sourceUrl");
        var url = PluginCatalog.ValidateUrl(source, configuration.GetValue<bool>("Plugins:RequireHttps"));

        await _one.WaitAsync(ct);
        var staging = Path.Combine(directory, StagingFolder, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, "package.zip");
            var md5 = await DownloadAsync(url, zip, ct);
            if (version.Checksum is { Length: > 0 } expected && !string.Equals(expected.Trim(), md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"checksum mismatch: the repository says {expected}, the download is {md5}");

            var unpacked = Path.Combine(staging, "files");
            var layoutFolder = Extract(zip, unpacked);
            var folderName = layoutFolder ?? Unsafe().Replace(package.Name, "");
            if (folderName.Length == 0 || folderName.StartsWith('.')) throw new InvalidOperationException($"'{package.Name}' cannot be used as a folder name");
            var root = layoutFolder is null ? unpacked : Path.Combine(unpacked, layoutFolder);

            var fromZip = PluginMetaFile.Read(root);
            var main = fromZip?.MainAssembly is { Length: > 0 } m ? m : folderName + ".dll";
            if (!SafeRelative(main) || !File.Exists(Path.Combine(root, main)))
                throw new InvalidOperationException($"the package has no {main}: the plugin's main assembly must be <folder>.dll (or named by \"mainAssembly\" in meta.json)");

            var meta = new PluginMeta
            {
                Guid = Guid.TryParse(package.Guid, out var g) ? g : PluginMetaFile.GuidFor(package.Name),
                Name = package.Name,
                Version = PluginMetaFile.NormalizeVersion(version.Version),
                Description = package.Description,
                Overview = package.Overview,
                Owner = package.Owner,
                Category = package.Category,
                MainAssembly = main == folderName + ".dll" ? null : main,
                Status = PluginStatus.Active,
            };
            PluginMetaFile.Write(root, meta);

            var target = Path.Combine(directory, folderName);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(root, target);
            log.LogInformation("Plugin {Name} {Version} installed from {Url} into {Folder}; it loads at the next start", package.Name, meta.Version, url, folderName);
            return target;
        }
        finally
        {
            PluginLoader.TryDelete(staging);
            _one.Release();
        }
    }

    private async Task<string> DownloadAsync(Uri url, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await http.CreateClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new InvalidOperationException("the package is larger than Plugins:MaxPackageMB");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            total += read;
            if (total > MaxPackageBytes) throw new InvalidOperationException("the package is larger than Plugins:MaxPackageMB");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Unpacks the zip into <paramref name="into"/>. Returns the name of the single top-level folder the zip is wrapped in
    /// (the plugin's folder name), or null when the files sit at the root.
    /// </summary>
    private string? Extract(string zipPath, string into)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidOperationException("the package has too many files");
        if (archive.Entries.Sum(e => e.Length) > MaxPackageBytes * 5) throw new InvalidOperationException("the package unpacks to too much data");

        var full = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(into);
        var tops = new HashSet<string>(StringComparer.Ordinal);
        var rootFiles = false;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Contains("..") || name.Contains(':'))
                throw new InvalidOperationException($"unsafe path in the package: {entry.FullName}");
            var slash = name.IndexOf('/');
            if (slash < 0) rootFiles = true; else tops.Add(name[..slash]);

            var destination = Path.GetFullPath(Path.Combine(into, name));
            if (!destination.StartsWith(full, StringComparison.Ordinal)) throw new InvalidOperationException($"unsafe path in the package: {entry.FullName}");
            if (name.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
        return !rootFiles && tops.Count == 1 ? tops.First() : null;
    }

    private static bool SafeRelative(string path) =>
        !Path.IsPathRooted(path) && !path.Replace('\\', '/').Split('/').Contains("..");
}
