# Plugins

A plugin is an assembly dropped into the server's `plugins` folder. Today a plugin can add **scheduled tasks** (they show up on the dashboard's *Scheduled Tasks* page like built-in ones) and keep its data in the server's PostgreSQL.

These are JellyServer's own plugins. They are **not** compatible with Jellyfin plugins.

## Installing

```
{DataDir}/plugins/MyPlugin/MyPlugin.dll        a folder per plugin; the main assembly has the folder's name
{DataDir}/plugins/MyPlugin/SomeLibrary.dll    the plugin's own dependencies sit next to it
{DataDir}/plugins/Simple.dll               or a single loose dll
```

The folder is `Plugins:Directory` (default `{DataDir}/plugins`). Plugins load once at startup, so **restart the server** after adding, updating or removing one. A plugin that fails to load is skipped and logged (`Plugin skipped: …`); the server starts anyway.

Each plugin gets its own load context: its dependencies can't clash with the server's or with other plugins'. Assemblies the server already has (the framework, the contracts) are shared.

Settings of a plugin live under `Plugins:Settings:<plugin id>`, e.g. `Plugins__Settings__myplugin__ApiKey` as an environment variable. They are read by the plugin when it runs.

## Settings in the dashboard

A plugin that declares its settings (`context.AddSettingsSection(...)`) gets a **Settings** page: a button on its plugin page and an entry in the dashboard's plugin menu. The server builds the form from the declaration, so a plugin ships no HTML or script. The page shows the plugin's **defaults**; whatever you change and save is stored in the plugins database and **wins over everything else**. Nothing has to be put in `appsettings` or environment variables.

For the plugin, `IConfiguration` (section `Plugins:Settings:<id>`) is layered, lowest first:

1. the defaults the plugin declared,
2. configuration files and environment variables (still honoured, for example `TMDB_API_KEY` from `docker-compose.yml`),
3. the values saved in the dashboard.

Only values that differ from the default are stored: setting a field back to its default removes the override, so a later change of that default in a plugin update still applies. Secrets (`PluginSettingType.Secret`) are shown masked and never sent back in clear; leaving the mask untouched keeps the stored value. Settings are read when a task starts, so a change applies to its next run; a setting the plugin reads once at startup is declared with `RestartRequired` and the page says so.

```csharp
context.AddSettingsSection(new PluginSettingsSection("General", [
    new("TmdbApiKey", "TMDB API key", PluginSettingType.Secret),
    new("MaxPages", "Listing pages", PluginSettingType.Number, "5000", Min: 1, Max: 100000),
    new("Mode", "Mode", PluginSettingType.Select, "fast", Options: ["fast", "slow"]),
]));
// The same keys for each of several profiles: a prefix puts them under Profiles:anime, Profiles:series, ...
context.AddSettingsSection(new PluginSettingsSection("Anime task", [ new("StrmDir", "Library folder", Default: "/media/anime") ], Prefix: "Profiles:anime"));
```

Types: `Text`, `Secret`, `Number` (whole numbers, optional `Min`/`Max`), `Toggle` (`true`/`false`) and `Select` (one of `Options`). The server validates every saved value against its declaration. The calls behind the page are `GET` and `POST /Plugins/{id}/Configuration`, administrators only.

## Installing from the dashboard

The dashboard's **Plugins** page (`/web/#/dashboard/plugins`) works as in Jellyfin: *Repositories* holds the addresses of plugin catalogs, *Catalog* lists what they offer, and a plugin's page installs, disables, enables or removes it. Every change is written to the plugin's `meta.json`, takes effect **at the next start** and shows the plugin as *Restart required* until then. The dashboard's **Restart** button (`POST /System/Restart`) ends the process; Docker's restart policy or hostim starts it again.

A repository is the address of a JSON file in Jellyfin's repository format, so any static web server can host one:

```json
[{
  "guid": "00000000-0000-0000-0000-000000000001",
  "name": "MyPlugin", "description": "…", "overview": "…", "owner": "me", "category": "General",
  "versions": [{
    "version": "1.0.0.0", "changelog": "…", "targetAbi": "",
    "sourceUrl": "MyPlugin_1.0.0.0.zip", "checksum": "<md5 of the zip>", "timestamp": "2026-10-30T22:14:40Z"
  }]
}]
```

`sourceUrl` may be relative to the manifest's address. The zip holds one folder named like the plugin, with the main assembly `<Name>.dll` (or the file named by `"mainAssembly"` in a `meta.json` inside) and its dependencies. Keep the same `guid` for every version.

`tools/pack-plugin.ps1` builds all of it: it publishes a plugin project, zips it, computes the MD5 and adds the version to `manifest.json` in an output folder you serve with nginx:

```powershell
./tools/pack-plugin.ps1 -Project plugins-dev/MyPlugin/MyPlugin.csproj `
    -OutDir C:\nginx\html\plugins -Guid 00000000-0000-0000-0000-000000000001 -Owner me
# then add http://<your host>/plugins/manifest.json under Plugins > Repositories
```

A plugin runs inside the server process, so install only plugins you trust. The server checks the MD5 from the manifest, refuses zip entries that point outside the plugin's folder and caps the package size.

| Setting | Default | |
|---|---|---|
| `Plugins:RequireHttps` | `false` | `true` rejects repositories and packages served over plain `http` |
| `Plugins:MaxPackageMB` | `100` | Largest package the server downloads |

Only administrators can use these calls. A plugin copied into the folder by hand (without `meta.json`) is listed too and can be disabled or removed; a single loose `.dll` cannot.

## Writing one

Reference `JellyServer.Plugins.Contracts` (and not copy it: `<Private>false</Private>`, see below) and implement `IServerPlugin`:

```csharp
public sealed class MyPlugin : IServerPlugin
{
    public string Id => "my-plugin";          // a-z 0-9 . _ - ; prefixes the plugin's tasks, storage and settings
    public string Name => "My plugin";
    public string Version => "1.0.0";

    public void Register(IPluginContext context)
    {
        context.Services.AddSingleton<MyClient>();                    // the plugin's own container
        context.AddScheduledTask<CleanupJob>(new PluginTaskInfo(
            "cleanup", "My plugin: cleanup", "Removes old things.", "My plugin",
            DefaultTriggers: [TaskTrigger.Daily(TimeSpan.FromHours(3))]));
    }
}

public sealed class CleanupJob(ILogger<CleanupJob> log, IPluginStorage storage) : IPluginJob
{
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await storage.SetAsync("state", "last-run", DateTime.UtcNow, ct);
        progress.Report(100);
    }
}
```

```xml
<ProjectReference Include="…/JellyServer.Plugins.Contracts.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
<!-- and <EnableDynamicLoading>true</EnableDynamicLoading> in the PropertyGroup -->
```

- **Task id and key** come from the server: the id is derived from the plugin id and the task key, the key shown to clients is `<plugin id>.<key>`. Plugins can't collide with each other or with built-in tasks.
- **Jobs** are created by the plugin's own container for every run, so they can take any service the plugin registered plus `ILogger<T>`, `IHttpClientFactory`, `IPluginStorage`, `PluginEnvironment` (id and data directory) and `IConfiguration` (the plugin's settings section).
- **Files:** `IPluginContext.DataDirectory` / `PluginEnvironment.DataDirectory` is a folder only this plugin writes to (`{DataDir}/plugin-data/<id>`).
- **Storage:** `IPluginStorage` keeps JSON documents in collections, in the `plugins` schema (`plugin_kv`). A plugin only sees its own documents. Move it to another server with `ConnectionStrings:plugins` like any module.
- **Versioning:** the major version of `JellyServer.Plugins.Contracts` is the plugin API version. A plugin built against another major is refused.

## Inside the server

`JellyServer.Plugins.Contracts` is the plugin SDK (plain contracts, depends only on `Tasks.Contracts`). The `Plugins` module loads the assemblies when the host builds its services, wraps every job as an `IScheduledTask` and hands those to the Tasks module, which schedules, runs and cancels them like any other task. Nothing else in the server knows about plugins.
