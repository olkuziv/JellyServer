namespace JellyServer.Plugins;

/// <summary>
/// What the dashboard's plugin pages do: list installed plugins, install from a repository, enable, disable and remove them.
/// Plugins load once at startup, so every change is written to the plugin's <c>meta.json</c> and takes effect at the next start;
/// until then the plugin reports <see cref="PluginStatus.Restart"/>.
/// </summary>
public sealed class PluginManager
{
    private readonly PluginLoadReport _report;
    private readonly PluginCatalog _catalog;
    private readonly PluginInstaller _installer;
    private readonly PluginSettingsService _settings;
    private readonly string _directory;

    internal PluginManager(PluginLoadReport report, PluginCatalog catalog, PluginInstaller installer, PluginSettingsService settings)
    {
        _report = report;
        _catalog = catalog;
        _installer = installer;
        _settings = settings;
        _directory = report.Directory;
    }

    // ---- settings pages ----

    /// <summary>The plugin pages <c>GET /web/ConfigurationPages</c> lists: one per plugin that declared settings.</summary>
    public IReadOnlyList<ConfigurationPageInfoDto> ConfigurationPages(bool? enableInMainMenu) => _settings.Pages(enableInMainMenu);

    /// <summary>The HTML of a plugin's settings page; null when no plugin of that name has settings.</summary>
    public string? SettingsPage(string name) =>
        _settings.FindPageByName(name) is { } page ? PluginSettingsPage.Render(page.Name, page.Id) : null;

    public PluginSettingsDto? Settings(Guid id) => _settings.Get(id);

    /// <summary>Null when the plugin has no settings; otherwise the problems found (none = saved).</summary>
    public Task<IReadOnlyList<string>?> SaveSettingsAsync(Guid id, IReadOnlyDictionary<string, string?> values, CancellationToken ct) =>
        _settings.SaveAsync(id, values, ct);

    private sealed record Row(Guid Id, PluginMeta Meta, string? Folder, PluginStatus Status, bool Loaded);

    public bool HasPendingRestart => Rows().Any(r => r.Status == PluginStatus.Restart);

    public IReadOnlyList<PluginInfoDto> Installed() =>
        Rows().OrderBy(r => r.Meta.Name, StringComparer.OrdinalIgnoreCase).Select(r => new PluginInfoDto
        {
            Name = r.Meta.Name,
            Version = PluginMetaFile.NormalizeVersion(r.Meta.Version),
            Description = r.Meta.Description,
            Id = r.Id.ToString("N"),
            CanUninstall = r.Folder is not null,
            Status = r.Status,
        }).ToList();

    /// <summary>The plugins directory as it is now, set against what was loaded at startup.</summary>
    private List<Row> Rows()
    {
        var rows = new List<Row>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in _report.Loaded)
        {
            var onDisk = p.Folder is null ? null : PluginMetaFile.Read(p.Folder);
            if (p.Folder is not null) seenFolders.Add(Path.GetFullPath(p.Folder));
            var meta = onDisk ?? p.Meta;
            var runningVersion = PluginMetaFile.NormalizeVersion(p.Plugin.Version);
            var changed = p.Folder is not null
                && (!Directory.Exists(p.Folder) || onDisk is { Status: not PluginStatus.Active }
                    || (onDisk is not null && PluginMetaFile.NormalizeVersion(onDisk.Version) != runningVersion));
            rows.Add(new Row(meta.Guid, new PluginMeta
            {
                Guid = meta.Guid, Name = p.Plugin.Name, Version = changed ? meta.Version : runningVersion,
                Description = meta.Description, Overview = meta.Overview, Owner = meta.Owner, Category = meta.Category,
            }, p.Folder, changed ? PluginStatus.Restart : PluginStatus.Active, true));
        }
        foreach (var p in _report.NotLoaded) seenFolders.Add(Path.GetFullPath(p.Folder));

        if (!Directory.Exists(_directory)) return rows;
        foreach (var folder in Directory.EnumerateDirectories(_directory).Where(f => !Path.GetFileName(f).StartsWith('.')))
        {
            var full = Path.GetFullPath(folder);
            var meta = PluginMetaFile.Read(folder);
            var wasSkipped = _report.NotLoaded.FirstOrDefault(n => Path.GetFullPath(n.Folder) == full);

            if (seenFolders.Contains(full) && wasSkipped is null) continue; // loaded; handled above
            if (wasSkipped is not null)
            {
                // Disabled or broken at startup: what the file says now decides (enabled since → waits for a restart).
                var status = meta is null || meta.Status == PluginStatus.Deleted ? (PluginStatus?)null
                    : wasSkipped.Status == PluginStatus.Malfunctioned ? PluginStatus.Malfunctioned
                    : meta.Status == PluginStatus.Active ? PluginStatus.Restart : PluginStatus.Disabled;
                if (status is { } s) rows.Add(new Row(meta!.Guid, meta, folder, s, false));
                continue;
            }
            // Not seen at startup: installed since.
            if (meta is null || meta.Status == PluginStatus.Deleted) continue;
            rows.Add(new Row(meta.Guid, meta, folder, meta.Status == PluginStatus.Active ? PluginStatus.Restart : PluginStatus.Disabled, false));
        }
        return rows;
    }

    private Row? Find(Guid id) => Rows().FirstOrDefault(r => r.Id == id);

    public bool Enable(Guid id) => SetStatus(id, PluginStatus.Active);

    public bool Disable(Guid id) => SetStatus(id, PluginStatus.Disabled);

    private bool SetStatus(Guid id, PluginStatus status)
    {
        if (Find(id) is not { Folder: { } folder } row) return false;
        var meta = PluginMetaFile.Read(folder) ?? new PluginMeta { Guid = row.Id, Name = row.Meta.Name, Version = row.Meta.Version };
        meta.Status = status;
        PluginMetaFile.Write(folder, meta);
        return true;
    }

    /// <summary>Removes the plugin: at once when it never loaded, otherwise at the next start (its files are in use until then).</summary>
    public bool Uninstall(Guid id)
    {
        if (Find(id) is not { Folder: { } folder } row) return false;
        if (!row.Loaded)
        {
            PluginLoader.TryDelete(folder);
            if (!Directory.Exists(folder)) return true;
        }
        var meta = PluginMetaFile.Read(folder) ?? new PluginMeta { Guid = row.Id, Name = row.Meta.Name, Version = row.Meta.Version };
        meta.Status = PluginStatus.Deleted;
        PluginMetaFile.Write(folder, meta);
        return true;
    }

    // ---- repositories ----

    public Task<IReadOnlyList<RepositoryInfoDto>> RepositoriesAsync(CancellationToken ct) => _catalog.RepositoriesAsync(ct);

    public Task SetRepositoriesAsync(IEnumerable<RepositoryInfoDto> repositories, CancellationToken ct) => _catalog.SetRepositoriesAsync(repositories, ct);

    public Task<IReadOnlyList<PackageInfoDto>> PackagesAsync(CancellationToken ct) => _catalog.PackagesAsync(ct);

    public async Task<PackageInfoDto?> PackageAsync(string name, Guid? guid, CancellationToken ct) =>
        Match(await _catalog.PackagesAsync(ct), name, guid).FirstOrDefault();

    /// <summary>Installs the newest (or the given) version of a package; false when no repository offers it.</summary>
    public async Task<bool> InstallAsync(string name, Guid? guid, string? version, string? repositoryUrl, CancellationToken ct)
    {
        var packages = Match(await _catalog.PackagesAsync(ct), name, guid);
        var wanted = string.IsNullOrEmpty(version) ? null : PluginMetaFile.NormalizeVersion(version);
        foreach (var package in packages)
        {
            var candidate = package.Versions.FirstOrDefault(v =>
                (wanted is null || v.Version == wanted)
                && (string.IsNullOrEmpty(repositoryUrl) || string.Equals(v.RepositoryUrl, repositoryUrl, StringComparison.OrdinalIgnoreCase)));
            if (candidate is null) continue;
            await _installer.InstallAsync(_directory, package, candidate, ct);
            return true;
        }
        return false;
    }

    private static IEnumerable<PackageInfoDto> Match(IReadOnlyList<PackageInfoDto> packages, string name, Guid? guid) =>
        packages.Where(p => (guid is { } g && g != Guid.Empty && Guid.TryParse(p.Guid, out var pg) && pg == g)
                            || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
