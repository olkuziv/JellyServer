using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Kernel.Events;

namespace JellyServer.Collections;

/// <summary>
/// Playlists and box sets ("collections"): hand-made lists of catalog items. Schema <c>collections</c>, its own server with
/// <c>ConnectionStrings:collections</c>. Entries of deleted items are removed when the catalog says so (<see cref="IItemLinkedData"/>).
/// </summary>
public sealed class CollectionsModule : IModule
{
    public const string ModuleName = "collections";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "collections", 5, typeof(CollectionsModule).Assembly,
            "JellyServer.Collections.Migrations."));
        services.AddSingleton<CollectionStore>();
        services.AddItemLinkedData<CollectionStore>();
        services.AddSingleton<CollectionManager>();
        services.AddSingleton<ICollectionManager>(sp => sp.GetRequiredService<CollectionManager>());
        services.AddHostedService<CollectionsUserCleanup>();
    }
}

/// <summary>A deleted user's playlists are handed over, kept or removed the way Jellyfin does it.</summary>
public sealed class CollectionsUserCleanup(CollectionManager manager, IEventBus events, ILogger<CollectionsUserCleanup> log) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = events.Subscribe<UserDeleted>(async (e, c) =>
        {
            await manager.ForgetUserAsync(e.UserId, c);
            log.LogInformation("Playlists of deleted user {UserId} handed over or removed", e.UserId);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
