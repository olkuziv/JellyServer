using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Caching;
using JellyServer.Kernel.Events;
using JellyServer.People.Contracts;

namespace JellyServer.Catalog;

/// <summary>Lays provider metadata over items (see <see cref="RemoteMetadataOverlay"/>) and keeps the caches honest.</summary>
public sealed class RemoteMetadataSink(LibraryStore store, ItemRepository items, IPeopleStore people, RedisCache cache, IEventBus events, ILogger<RemoteMetadataSink> log)
    : IRemoteMetadataSink
{
    public async Task<int> ApplyAsync(IReadOnlyList<RemoteMetadataUpdate> updates, CancellationToken ct = default)
    {
        if (updates.Count == 0) return 0;
        var ids = updates.Select(u => u.ItemId).Distinct().ToList();
        var current = (await items.GetManyAsync(ids, ct)).ToDictionary(i => i.Id);

        var rows = new List<Item>(updates.Count);
        var changed = 0;
        var nameGone = new List<Guid>();
        foreach (var update in updates)
        {
            if (!current.TryGetValue(update.ItemId, out var item)) continue; // removed while the provider was working
            // The provider named the item before and no longer does (no translation): the local name has to come back with the next scan.
            if (!item.NameLocked && update.Previous?.Name is { Length: > 0 } was && was == item.Name && string.IsNullOrWhiteSpace(update.Metadata.Name))
                nameGone.Add(item.Id);
            if (RemoteMetadataOverlay.Apply(item, update.Previous, update.Metadata, update.ReplaceAll, update.ReplaceImages)) changed++;
            rows.Add(item);
        }

        // Credits first, like a scan writes: the catalog row is the last part of an item to change.
        await people.SetCreditsAsync(rows.Select(r => (r.Id, (IReadOnlyList<Person>?)r.People)).ToList(), ct);
        await store.SaveOverlayAsync(rows, ct);
        await store.ForgetFingerprintsAsync(nameGone, ct);
        await store.EnsureGenresAsync(rows.SelectMany(r => r.Genres), ct);
        await cache.DeleteAsync(rows.Select(r => "item:" + r.Id.ToString("N")));
        if (changed > 0) await cache.BumpGenerationAsync();
        await events.PublishAsync(new ItemsMetadataApplied(rows.Select(r => r.Id).ToList()), ct);
        log.LogDebug("Provider metadata stored for {Count} items, {Changed} changed", rows.Count, changed);
        return changed;
    }
}

public sealed class MovieSets(LibraryStore store) : IMovieSets
{
    public Task<List<MovieSetRef>> OfMoviesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => store.MovieSetsAsync(ids, ct);

    public Task<List<MovieSetRef>> InSetsAsync(IReadOnlyCollection<string> setNames, IReadOnlyCollection<Guid> libraryIds, CancellationToken ct = default) =>
        store.MovieSetMembersAsync(setNames, libraryIds, ct);
}

/// <summary>Catalog read access for the metadata system.</summary>
public sealed class ItemRefs(LibraryStore store) : IItemRefs
{
    public Task<List<ItemRef>> PageAsync(Guid? afterId, int limit, CancellationToken ct = default) => store.PageRefsAsync(afterId, limit, ct);

    public Task<List<ItemRef>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => store.GetRefsAsync(ids, ct);

    public Task<List<ItemRef>> GetDescendantsAsync(Guid itemId, CancellationToken ct = default) => store.GetDescendantRefsAsync(itemId, ct);
}
