using JellyServer.Catalog;
using JellyServer.Collections;
using JellyServer.Identity;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Media;
using JellyServer.Metadata;
using JellyServer.People;
using JellyServer.Plugins;
using JellyServer.Settings;
using JellyServer.Tasks;
using JellyServer.UserData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.DbTool;

/// <summary>
/// Which modules own a schema and where their migrations are. Read from the modules themselves (each registers a
/// <see cref="ModuleDatabaseSpec"/>), so the tool never keeps its own list of schema names.
/// </summary>
public static class ModuleCatalog
{
    private static readonly IModule[] Modules =
    [
        new IdentityModule(), new UserDataModule(), new CatalogModule(), new MediaModule(), new PeopleModule(), new CollectionsModule(),
        new SettingsModule(), new TasksModule(), new MetadataModule(), new PluginsModule(loadPlugins: false),
    ];

    public static IReadOnlyList<ModuleDatabaseSpec> Specs { get; } = Discover();

    public static ModuleDatabaseSpec? Find(string module) =>
        Specs.FirstOrDefault(s => string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase));

    private static List<ModuleDatabaseSpec> Discover()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        foreach (var module in Modules) module.Register(services, config);
        return services.Where(d => d.ServiceType == typeof(ModuleDatabaseSpec) && d.ImplementationInstance is ModuleDatabaseSpec)
            .Select(d => (ModuleDatabaseSpec)d.ImplementationInstance!)
            .OrderBy(s => s.Module, StringComparer.Ordinal)
            .ToList();
    }
}
