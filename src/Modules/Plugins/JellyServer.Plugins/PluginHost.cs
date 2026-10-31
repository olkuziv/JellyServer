using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using JellyServer.Plugins.Contracts;
using JellyServer.Tasks.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JellyServer.Plugins;

/// <summary>
/// Gives every plugin its own service container, built the first time one of its jobs runs. The container holds what the plugin
/// registered plus a few host services (logging, HTTP clients, its storage, its settings); the plugin sees nothing else of the host.
/// </summary>
internal sealed class PluginHost(IReadOnlyList<LoadedPlugin> plugins, IServiceProvider hostServices) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<ServiceProvider>> _providers = new();

    public IReadOnlyList<LoadedPlugin> Plugins => plugins;

    public IServiceProvider ServicesOf(LoadedPlugin plugin) => _providers.GetOrAdd(plugin.Plugin.Id, _ => new Lazy<ServiceProvider>(() => Build(plugin))).Value;

    private ServiceProvider Build(LoadedPlugin plugin)
    {
        var id = plugin.Plugin.Id;
        var services = new ServiceCollection();
        foreach (var d in plugin.Context.Services) services.Add(d);

        services.TryAddSingleton(hostServices.GetRequiredService<ILoggerFactory>());
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));
        services.TryAddSingleton(hostServices.GetRequiredService<IHttpClientFactory>());
        services.TryAddSingleton<IPluginStorage>(new PluginStorage(id, hostServices.GetRequiredService<PluginStore>()));
        services.TryAddSingleton(new PluginEnvironment(id, plugin.Context.DataDirectory));
        services.TryAddSingleton<IConfiguration>(plugin.Context.Configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public void Dispose()
    {
        foreach (var p in _providers.Values.Where(p => p.IsValueCreated)) p.Value.Dispose();
    }
}

/// <summary>A plugin job as the Tasks module sees it. The id comes from the host (plugin id + key), so plugins cannot collide with each other or with built-in tasks.</summary>
internal sealed class PluginScheduledTask(LoadedPlugin plugin, PluginTaskRegistration registration, PluginHost host, Func<PluginSettingsService> settings) : IScheduledTask
{
    private readonly PluginTaskInfo _info = registration.Info;

    public string Id { get; } = IdFor(plugin.Plugin.Id, registration.Info.Key);
    public string Key { get; } = plugin.Plugin.Id + "." + registration.Info.Key;
    public string Name => _info.Name;
    public string Description => _info.Description;
    public string Category => _info.Category;
    public IReadOnlyList<TaskTrigger> DefaultTriggers => _info.DefaultTriggers ?? [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await settings().Ready.WaitAsync(ct); // the administrator's saved settings are applied before the first run
        await using var scope = host.ServicesOf(plugin).CreateAsyncScope();
        var job = (IPluginJob)scope.ServiceProvider.GetRequiredService(registration.JobType);
        await job.ExecuteAsync(progress, ct);
    }

    internal static string IdFor(string pluginId, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"jellyserver-plugin:{pluginId}:{key}")).AsSpan(0, 16));
}

/// <summary>Reports at startup which plugins loaded and which did not.</summary>
internal sealed class PluginStartupReport(PluginLoadReport report, ILogger<PluginStartupReport> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        foreach (var p in report.Loaded)
            log.LogInformation("Plugin {Id} {Version} ({Source}): {Tasks} scheduled task(s)", p.Plugin.Id, p.Plugin.Version, p.Source, p.Context.Tasks.Count);
        foreach (var failure in report.Failures) log.LogError("Plugin skipped: {Failure}", failure);
        if (report.Loaded.Count == 0 && report.Failures.Count == 0) log.LogDebug("No plugins in {Directory}", report.Directory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
