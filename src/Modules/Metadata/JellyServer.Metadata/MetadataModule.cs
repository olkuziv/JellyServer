using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Metadata.Contracts;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Metadata;

/// <summary>
/// Looks items up on the internet through the registered <see cref="IMetadataProvider"/>s and hands what it finds to the catalog.
/// It owns its bookkeeping (<c>item_state</c>) and what the providers answered (<c>item_remote</c>); the catalog lays the answers over the
/// items it owns. Both tables can sit on a server of their own (<c>ConnectionStrings:metadata</c>).
/// </summary>
public sealed class MetadataModule : IModule
{
    public const string ModuleName = "metadata";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "metadata", 5, typeof(MetadataModule).Assembly, "JellyServer.Metadata.Migrations."));
        services.Configure<MetadataOptions>(configuration.GetSection(MetadataOptions.Section));
        services.AddSingleton<IMetadataStore, MetadataStore>();
        // The catalog asks for the providers' answers when a scan rewrites items (it does not keep them itself).
        services.AddSingleton<IRemoteMetadataSource>(sp => sp.GetRequiredService<IMetadataStore>());
        services.AddSingleton<MetadataEngine>();
        services.AddSingleton<IScheduledTask, DownloadMetadataTask>();
        services.AddSingleton<IScheduledTask, RefreshAllMetadataTask>();
        services.AddSingleton<IMetadataRefresh>(sp => sp.GetRequiredService<MetadataEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<MetadataEngine>());
        services.AddSingleton<IMetadataProviders, ProviderCatalog>();
    }
}

/// <summary>The providers that can work right now.</summary>
public sealed class ProviderCatalog(IEnumerable<IMetadataProvider> providers) : IMetadataProviders
{
    public IReadOnlyList<MetadataProviderInfo> Available =>
        [.. providers.Where(p => p.IsAvailable).Select(p => new MetadataProviderInfo(p.Name, p.ItemTypes, p.ImageTypes))];
}