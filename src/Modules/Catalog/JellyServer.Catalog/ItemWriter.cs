using JellyServer.Catalog.Contracts;
using JellyServer.Media.Contracts;
using JellyServer.People.Contracts;

namespace JellyServer.Catalog;

/// <summary>
/// Saves whole items across the three modules that keep them. There is no transaction spanning three databases, so the order
/// matters: files and credits first, the catalog row last. Until the row is written nobody lists the item, and a write that fails
/// half-way leaves the old row with its old fingerprint, so the next scan simply writes the item again.
/// </summary>
public sealed class ItemWriter(LibraryStore store, IMediaSourceStore media, IPeopleStore people)
{
    public async Task UpsertAsync(IReadOnlyList<Item> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var sources = items.SelectMany(ItemParts.Sources).ToList();
        await Task.WhenAll(
            media.ReplaceAsync(items.Select(i => i.Id).ToList(), sources, ct),
            people.SetCreditsAsync(items.Select(i => (i.Id, (IReadOnlyList<Person>?)i.People)).ToList(), ct));
        await store.UpsertAsync(items, ct);
    }
}
