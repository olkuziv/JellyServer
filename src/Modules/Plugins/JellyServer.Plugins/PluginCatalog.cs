using System.Text.Json;

namespace JellyServer.Plugins;

/// <summary>
/// The plugin repositories an administrator added in the dashboard and the packages they offer. A repository is a URL of a JSON file
/// in Jellyfin's repository format (an array of packages, each with versions that point to a zip), so any static web server can host one.
/// </summary>
internal sealed class PluginCatalog(PluginStore store, IHttpClientFactory http, IConfiguration configuration, ILogger<PluginCatalog> log)
{
    // "_host" is not a valid plugin id, so no plugin can read or overwrite these documents.
    private const string HostId = "_host";
    private const string Collection = "repositories";
    private const string Key = "list";
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, IReadOnlyList<PackageInfoDto> Packages)? _cache;

    public bool RequireHttps => configuration.GetValue<bool>("Plugins:RequireHttps");

    public async Task<IReadOnlyList<RepositoryInfoDto>> RepositoriesAsync(CancellationToken ct) =>
        await store.GetAsync<List<RepositoryInfoDto>>(HostId, Collection, Key, ct) ?? [];

    public async Task SetRepositoriesAsync(IEnumerable<RepositoryInfoDto> repositories, CancellationToken ct)
    {
        var list = new List<RepositoryInfoDto>();
        foreach (var r in repositories)
        {
            var url = ValidateUrl(r.Url, RequireHttps);
            list.Add(new RepositoryInfoDto { Name = string.IsNullOrWhiteSpace(r.Name) ? url.Host : r.Name.Trim(), Url = url.AbsoluteUri, Enabled = r.Enabled });
        }
        await store.SetAsync(HostId, Collection, Key, list, ct);
        _cache = null;
    }

    /// <summary>Every package of the enabled repositories; a repository that cannot be read is logged and skipped. Cached for a few minutes.</summary>
    public async Task<IReadOnlyList<PackageInfoDto>> PackagesAsync(CancellationToken ct)
    {
        if (_cache is { } c && DateTime.UtcNow - c.At < CacheFor) return c.Packages;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.At < CacheFor) return again.Packages;

            var repositories = (await RepositoriesAsync(ct)).Where(r => r.Enabled && r.Url is { Length: > 0 }).ToList();
            var fetched = await Task.WhenAll(repositories.Select(r => FetchAsync(r, ct)));

            var byGuid = new Dictionary<string, PackageInfoDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in fetched.SelectMany(p => p))
            {
                var key = package.Guid.Length > 0 ? package.Guid : package.Name;
                if (byGuid.TryGetValue(key, out var existing)) existing.Versions.AddRange(package.Versions);
                else byGuid[key] = package;
            }
            foreach (var package in byGuid.Values)
                package.Versions = package.Versions.OrderByDescending(v => System.Version.TryParse(v.Version, out var n) ? n : new Version()).ToList();

            var result = byGuid.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _cache = (DateTime.UtcNow, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PackageInfoDto>> FetchAsync(RepositoryInfoDto repository, CancellationToken ct)
    {
        try
        {
            var url = ValidateUrl(repository.Url, RequireHttps);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.CreateClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MaxManifestBytes, timeout.Token);
            var packages = ParseManifest(await response.Content.ReadAsByteArrayAsync(timeout.Token));

            foreach (var package in packages)
            {
                package.Guid = Guid.TryParse(package.Guid, out var g) ? g.ToString("N") : PluginMetaFile.GuidFor(package.Name).ToString("N");
                foreach (var version in package.Versions)
                {
                    version.Version = PluginMetaFile.NormalizeVersion(version.Version);
                    version.RepositoryName = repository.Name ?? url.Host;
                    version.RepositoryUrl = url.AbsoluteUri;
                    if (version.SourceUrl is { Length: > 0 } source && Uri.TryCreate(url, source, out var absolute)) version.SourceUrl = absolute.AbsoluteUri;
                }
            }
            return packages.Where(p => p.Name.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Plugin repository {Url} could not be read: {Error}", repository.Url, ex.Message);
            return [];
        }
    }

    /// <summary>The packages of a manifest. A UTF-8 byte order mark, which Windows editors and PowerShell like to add, is skipped.</summary>
    internal static List<PackageInfoDto> ParseManifest(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith("﻿"u8)) json = json[3..];
        return JsonSerializer.Deserialize<List<PackageInfoDto>>(json, Json) ?? [];
    }

    internal static Uri ValidateUrl(string? text, bool requireHttps)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"'{text}' is not an http(s) address");
        if (requireHttps && url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"'{text}' is not https (Plugins:RequireHttps is on)");
        return url;
    }
}
