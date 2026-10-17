using JellyServer.Kernel.Events;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JellyServer.Catalog.Contracts;

/// <summary>
/// Data another module keeps per catalog item (its files, its people, the playlists it is in, ...). There are no foreign keys
/// between modules: the module registers it with <see cref="ItemLinkedDataExtensions.AddItemLinkedData{T}"/> and is told when items
/// go away; the catalog's clean-up task also sweeps rows whose item no longer exists (a crash between two writes, a lost event).
/// </summary>
public interface IItemLinkedData
{
    /// <summary>What the data is, for logs.</summary>
    string Name { get; }

    /// <summary>A page of the item ids this module keeps data for, in id order after <paramref name="afterId"/> (null = from the start).</summary>
    Task<IReadOnlyList<Guid>> PageItemIdsAsync(Guid? afterId, int limit, CancellationToken ct);

    Task RemoveAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct);
}

public static class ItemLinkedDataExtensions
{
    /// <summary>Registers <typeparamref name="T"/> (already registered as a singleton by the module) to be cleaned up with the catalog's items.</summary>
    public static IServiceCollection AddItemLinkedData<T>(this IServiceCollection services) where T : class, IItemLinkedData
    {
        services.AddSingleton<IItemLinkedData>(sp => sp.GetRequiredService<T>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ItemLinkedDataCleanup>());
        return services;
    }
}

/// <summary>Removes the linked data of items the catalog deleted, as soon as it says so.</summary>
internal sealed class ItemLinkedDataCleanup(IEnumerable<IItemLinkedData> owners, IEventBus events, ILogger<ItemLinkedDataCleanup> log) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken ct)
    {
        _subscriptions.Add(events.Subscribe<ItemsRemoved>((e, c) => RemoveAsync(e.ItemIds, c)));
        _subscriptions.Add(events.Subscribe<LibraryRemoved>((e, c) => RemoveAsync(e.ItemIds, c)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private async Task RemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        foreach (var owner in owners)
        {
            try
            {
                await owner.RemoveAsync(ids, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The clean-up task removes it later.
                log.LogWarning(ex, "Removing {Data} of {Count} deleted items failed", owner.Name, ids.Count);
            }
        }
    }
}
