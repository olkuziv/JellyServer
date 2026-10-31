using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using JellyServer.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyServer.Tests;

public sealed class PluginManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyserver-plugin-mgmt-" + Guid.NewGuid().ToString("N"));
    private string Plugins => Path.Combine(_root, "plugins");

    public PluginManagementTests() => Directory.CreateDirectory(Plugins);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Status_is_written_as_a_name_because_the_dashboard_treats_the_number_0_as_not_installed()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PluginInfoDto { Name = "X", Status = PluginStatus.Active });
        Assert.Contains("\"Status\":\"Active\"", json);
    }

    private sealed class Factory(byte[] body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body));
    }

    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(body) });
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }
        return ms.ToArray();
    }

    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    private static PackageInfoDto Package(string name, Guid guid) => new() { Name = name, Guid = guid.ToString("N"), Owner = "me", Description = "d" };

    private static PackageVersionDto Version(byte[] zip, string? checksum = null) =>
        new() { Version = "1.2.3", SourceUrl = "http://repo.test/p.zip", Checksum = checksum ?? Md5(zip) };

    private static PluginInstaller Installer(byte[] zip) =>
        new(new Factory(zip), new ConfigurationBuilder().Build(), NullLogger<PluginInstaller>.Instance);

    private PluginManager Manager() =>
        new(new PluginLoadReport([], [], Plugins), null!, Installer([]), null!);

    [Fact]
    public async Task Install_UnpacksWrappedFolder_AndWritesMeta()
    {
        var zip = Zip(("Sample/Sample.dll", "x"), ("Sample/lib/dep.txt", "y"));
        var guid = Guid.NewGuid();

        var folder = await Installer(zip).InstallAsync(Plugins, Package("Sample", guid), Version(zip), CancellationToken.None);

        Assert.Equal(Path.Combine(Plugins, "Sample"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "Sample.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "lib", "dep.txt")));
        var meta = PluginMetaFile.Read(folder)!;
        Assert.Equal(guid, meta.Guid);
        Assert.Equal("1.2.3.0", PluginMetaFile.NormalizeVersion(meta.Version));
        Assert.Equal(PluginStatus.Active, meta.Status);
        Assert.False(Directory.Exists(Path.Combine(Plugins, PluginInstaller.StagingFolder, "x")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Plugins, PluginInstaller.StagingFolder)));
    }

    [Fact]
    public async Task Install_RootLayout_UsesPackageNameAsFolder()
    {
        var zip = Zip(("My Plugin.dll", "x"));
        // "My Plugin" becomes "MyPlugin"; the main assembly must then be named like the folder.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(zip).InstallAsync(Plugins, Package("My Plugin", Guid.NewGuid()), Version(zip), CancellationToken.None));

        var good = Zip(("MyPlugin.dll", "x"));
        var folder = await Installer(good).InstallAsync(Plugins, Package("My Plugin", Guid.NewGuid()), Version(good), CancellationToken.None);
        Assert.Equal(Path.Combine(Plugins, "MyPlugin"), folder);
    }

    [Fact]
    public async Task Install_RejectsWrongChecksum_AndLeavesNothing()
    {
        var zip = Zip(("Sample/Sample.dll", "x"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(zip).InstallAsync(Plugins, Package("Sample", Guid.NewGuid()), Version(zip, new string('0', 32)), CancellationToken.None));

        Assert.Contains("checksum", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(Plugins, "Sample")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Sample/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/evil.txt")]
    public async Task Install_RejectsPathsOutsideTheFolder(string entry)
    {
        var zip = Zip(("Sample/Sample.dll", "x"), (entry, "boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(zip).InstallAsync(Plugins, Package("Sample", Guid.NewGuid()), Version(zip), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(Plugins, "Sample")));
    }

    [Fact]
    public async Task Install_ReplacesAnOlderVersion()
    {
        var guid = Guid.NewGuid();
        var v1 = Zip(("Sample/Sample.dll", "one"), ("Sample/old.txt", "x"));
        var v2 = Zip(("Sample/Sample.dll", "two"));
        await Installer(v1).InstallAsync(Plugins, Package("Sample", guid), Version(v1), CancellationToken.None);

        var folder = await Installer(v2).InstallAsync(Plugins, Package("Sample", guid), Version(v2), CancellationToken.None);

        Assert.Equal("two", File.ReadAllText(Path.Combine(folder, "Sample.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "old.txt")));
    }

    [Fact]
    public async Task Manager_InstalledPluginWaitsForRestart_AndCanBeDisabledEnabledAndRemoved()
    {
        var guid = Guid.NewGuid();
        var zip = Zip(("Sample/Sample.dll", "x"));
        await Installer(zip).InstallAsync(Plugins, Package("Sample", guid), Version(zip), CancellationToken.None);
        var manager = Manager();

        var info = Assert.Single(manager.Installed());
        Assert.Equal(guid.ToString("N"), info.Id);
        Assert.Equal(PluginStatus.Restart, info.Status);
        Assert.True(info.CanUninstall);
        Assert.True(manager.HasPendingRestart);

        Assert.True(manager.Disable(guid));
        Assert.Equal(PluginStatus.Disabled, Assert.Single(manager.Installed()).Status);
        Assert.False(manager.HasPendingRestart);

        Assert.True(manager.Enable(guid));
        Assert.Equal(PluginStatus.Restart, Assert.Single(manager.Installed()).Status);

        Assert.True(manager.Uninstall(guid));
        Assert.Empty(manager.Installed());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "Sample")));
        Assert.False(manager.Uninstall(guid));
    }

    [Fact]
    public void Loader_RemovesDeletedFolders_AndSkipsDisabledOnes()
    {
        var gone = Path.Combine(Plugins, "Gone");
        Directory.CreateDirectory(gone);
        File.WriteAllText(Path.Combine(gone, "Gone.dll"), "x");
        PluginMetaFile.Write(gone, new PluginMeta { Guid = Guid.NewGuid(), Name = "Gone", Version = "1.0.0.0", Status = PluginStatus.Deleted });

        var off = Path.Combine(Plugins, "Off");
        Directory.CreateDirectory(off);
        File.WriteAllText(Path.Combine(off, "Off.dll"), "not an assembly");
        var offGuid = Guid.NewGuid();
        PluginMetaFile.Write(off, new PluginMeta { Guid = offGuid, Name = "Off", Version = "1.0.0.0", Status = PluginStatus.Disabled });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:Directory"] = Plugins, ["Plugins:DataDirectory"] = Path.Combine(_root, "data"),
        }).Build();

        var report = PluginLoader.Load(config);

        Assert.False(Directory.Exists(gone));
        Assert.Empty(report.Loaded);
        Assert.Empty(report.Failures);
        var skipped = Assert.Single(report.NotLoaded);
        Assert.Equal(PluginStatus.Disabled, skipped.Status);
        Assert.Equal(offGuid, skipped.Meta.Guid);

        var info = Assert.Single(new PluginManager(report, null!, Installer([]), null!).Installed());
        Assert.Equal(PluginStatus.Disabled, info.Status);
    }

    [Fact]
    public void Manifest_WithByteOrderMark_IsParsed()
    {
        var json = "[{\"guid\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"MyPlugin\",\"versions\":[{\"version\":\"1.0.0.0\",\"sourceUrl\":\"MyPlugin_1.0.0.0.zip\",\"checksum\":\"abc\"}]}]";
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var package = Assert.Single(PluginCatalog.ParseManifest(withBom));

        Assert.Equal("MyPlugin", package.Name);
        Assert.Equal("MyPlugin_1.0.0.0.zip", Assert.Single(package.Versions).SourceUrl);
        Assert.Equal("MyPlugin", Assert.Single(PluginCatalog.ParseManifest(Encoding.UTF8.GetBytes(json))).Name);
    }

    [Theory]
    [InlineData("1", "1.0.0.0")]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("nonsense", "0.0.0.0")]
    public void NormalizeVersion_AlwaysHasFourParts(string input, string expected) =>
        Assert.Equal(expected, PluginMetaFile.NormalizeVersion(input));

    [Theory]
    [InlineData("http://repo.test/m.json", false, true)]
    [InlineData("https://repo.test/m.json", true, true)]
    [InlineData("http://repo.test/m.json", true, false)]
    [InlineData("ftp://repo.test/m.json", false, false)]
    [InlineData("not a url", false, false)]
    public void RepositoryUrls_MustBeHttp_AndHttpsWhenRequired(string url, bool requireHttps, bool ok)
    {
        if (ok) Assert.NotNull(PluginCatalog.ValidateUrl(url, requireHttps));
        else Assert.Throws<InvalidOperationException>(() => PluginCatalog.ValidateUrl(url, requireHttps));
    }
}
