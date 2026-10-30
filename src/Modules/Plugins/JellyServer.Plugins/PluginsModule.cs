using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Plugins;

/// <summary>
/// Loads JellyServer plugins (assemblies from the plugins directory) and turns the scheduled jobs they register into
/// <see cref="IScheduledTask"/>s, which the Tasks module then runs like any built-in task. Gives plugins JSON storage in this module's schema.
/// </summary>
/// <param name="loadPlugins">False for tools that only need the module's database spec.</param>
public sealed class PluginsModule(bool loadPlugins = true) : IModule
{
    public const string ModuleName = "plugins";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "plugins", 2, typeof(PluginsModule).Assembly, "JellyServer.Plugins.Migrations."));
        services.AddSingleton<PluginStore>();
        if (!loadPlugins) return;

        var report = PluginLoader.Load(configuration);
        services.AddSingleton(report);
        services.AddSingleton(sp => new PluginHost(report.Loaded, sp));
        services.AddHostedService<PluginStartupReport>();
        foreach (var plugin in report.Loaded)
            foreach (var registration in plugin.Context.Tasks)
                services.AddSingleton<IScheduledTask>(sp => new PluginScheduledTask(plugin, registration, sp.GetRequiredService<PluginHost>()));
    }
}
