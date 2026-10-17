using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Catalog.Contracts;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Catalog;

/// <summary>Libraries and items: the large, read-heavy data set (a candidate for read replicas).</summary>
public sealed class CatalogModule : IModule
{
    public const string ModuleName = "catalog";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "catalog", 30, typeof(CatalogModule).Assembly,
            "JellyServer.Catalog.Migrations."));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.Section));
        services.Configure<ScannerOptions>(configuration.GetSection(ScannerOptions.Section));
        services.PostConfigure<ScannerOptions>(o =>
        {
            o.Workers = Math.Max(1, o.Workers);
            o.BatchSize = Math.Max(50, o.BatchSize);
        });
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<LibraryRegistry>();
        services.AddSingleton<ILibraryDirectory>(sp => sp.GetRequiredService<LibraryRegistry>());
        services.AddSingleton<IItemRefs, ItemRefs>();
        services.AddSingleton<IRemoteMetadataSink, RemoteMetadataSink>();
        services.AddSingleton<ScanCoordinator>();
        services.AddSingleton<IScheduledTask, ScanLibraryTask>();
        services.AddSingleton<ItemRepository>();
        services.AddSingleton<LibraryManager>();
    }
}
