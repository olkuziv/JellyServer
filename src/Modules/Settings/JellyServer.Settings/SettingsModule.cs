using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Settings.Contracts;

namespace JellyServer.Settings;

/// <summary>Named server configurations edited on the dashboard.</summary>
public sealed class SettingsModule : IModule
{
    public const string ModuleName = "settings";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "settings", 5, typeof(SettingsModule).Assembly,
            "JellyServer.Settings.Migrations."));
        services.AddSingleton<INamedConfigurationStore, NamedConfigurationStore>();
        services.AddSingleton<IServerConfiguration, ServerConfiguration>();
    }
}
