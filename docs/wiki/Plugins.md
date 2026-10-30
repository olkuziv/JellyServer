# Plugins

A plugin is an assembly dropped into the server's `plugins` folder. Today a plugin can add **scheduled tasks** (they show up on the dashboard's *Scheduled Tasks* page like built-in ones) and keep its data in the server's PostgreSQL.

These are JellyServer's own plugins. They are **not** compatible with Jellyfin plugins.

## Installing

```
{DataDir}/plugins/UAKino/UAKino.dll        a folder per plugin; the main assembly has the folder's name
{DataDir}/plugins/UAKino/AngleSharp.dll    the plugin's own dependencies sit next to it
{DataDir}/plugins/Simple.dll               or a single loose dll
```

The folder is `Plugins:Directory` (default `{DataDir}/plugins`). Plugins load once at startup, so **restart the server** after adding, updating or removing one. A plugin that fails to load is skipped and logged (`Plugin skipped: …`); the server starts anyway.

Each plugin gets its own load context: its dependencies can't clash with the server's or with other plugins'. Assemblies the server already has (the framework, the contracts) are shared.

Settings of a plugin live under `Plugins:Settings:<plugin id>`, e.g. `Plugins__Settings__uakino__TmdbApiKey` as an environment variable. They are read by the plugin when it runs.

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
