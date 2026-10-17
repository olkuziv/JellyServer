using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;

namespace JellyServer.Collections;

/// <summary>Jellyfin's PlaylistManager and CollectionManager rules on top of <see cref="CollectionStore"/>.</summary>
public sealed class CollectionManager(CollectionStore store, IItemRefs items, ILogger<CollectionManager> log) : ICollectionManager
{
    public Task<Collection?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(id, ct);

    public async Task<List<Collection>> ListAsync(string kind, Guid userId, CancellationToken ct = default) =>
        (await store.ListAsync(kind, ct)).Where(c => c.IsVisibleTo(userId)).ToList();

    public async Task<List<Guid>> EntriesAsync(Guid id, CancellationToken ct = default) => (await store.EntriesAsync([id], ct))[id];

    public Task<Dictionary<Guid, List<Guid>>> EntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => store.EntriesAsync(ids, ct);

    public async Task<Collection> CreatePlaylistAsync(Guid ownerId, string name, IReadOnlyList<Guid> itemIds, string? mediaType,
        IReadOnlyList<CollectionShare> shares, bool openAccess, CancellationToken ct = default)
    {
        var entries = await ExpandAsync(itemIds, ct);
        var now = DateTime.UtcNow;
        var playlist = new Collection
        {
            Id = Guid.NewGuid(), Kind = CollectionKinds.Playlist, Name = name, OwnerId = ownerId, OpenAccess = openAccess,
            Shares = [.. shares.Where(s => s.UserId != ownerId).DistinctBy(s => s.UserId)],
            // Jellyfin: the type the caller gave, else the first item's; Audio when nothing tells.
            MediaType = mediaType is { Length: > 0 } and not "Unknown" ? mediaType : await MediaTypeOfAsync(itemIds, ct) ?? "Audio",
            DateCreated = now, DateModified = now, DateLastMediaAdded = entries.Count > 0 ? now : null, ItemCount = entries.Count,
        };
        await store.InsertAsync(playlist, entries, ct);
        log.LogInformation("Playlist {Name} created with {Count} items", name, entries.Count);
        return playlist;
    }

    /// <summary>Every catalog item is a video; a playlist made of nothing known stays undecided (Jellyfin then picks Audio).</summary>
    private async Task<string?> MediaTypeOfAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0) return null;
        if ((await items.GetManyAsync(itemIds, ct)).Count > 0) return "Video";
        foreach (var id in itemIds)
            if (await store.GetAsync(id, ct) is { } c && (await EntriesAsync(c.Id, ct)).Count > 0)
                return c.MediaType ?? "Video";
        return null;
    }

    public async Task UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid>? itemIds, IReadOnlyList<CollectionShare>? shares, bool? openAccess,
        CancellationToken ct = default)
    {
        if (itemIds is not null)
        {
            var entries = await ExpandAsync(itemIds, ct);
            await store.ChangeEntriesAsync(id, _ => entries, added: entries.Count > 0, ct);
        }
        if (name is null && shares is null && openAccess is null) return;
        if (await store.GetAsync(id, ct) is not { } playlist) return;
        if (name is not null) playlist.Name = name;
        if (shares is not null) playlist.Shares = [.. shares.Where(s => s.UserId != playlist.OwnerId).DistinctBy(s => s.UserId)];
        if (openAccess is { } open) playlist.OpenAccess = open;
        await store.UpdateAsync(playlist, ct);
    }

    public async Task<Collection> CreateBoxSetAsync(string name, IReadOnlyList<Guid> itemIds, bool isLocked, CancellationToken ct = default)
    {
        var entries = await ExistingAsync(itemIds.Distinct().ToList(), ct);
        var now = DateTime.UtcNow;
        var boxSet = new Collection
        {
            Id = Guid.NewGuid(), Kind = CollectionKinds.BoxSet, Name = name, IsLocked = isLocked,
            DateCreated = now, DateModified = now, DateLastMediaAdded = entries.Count > 0 ? now : null, ItemCount = entries.Count,
        };
        await store.InsertAsync(boxSet, entries, ct);
        log.LogInformation("Collection {Name} created with {Count} items", name, entries.Count);
        return boxSet;
    }

    public async Task AddAsync(Guid id, IReadOnlyList<Guid> itemIds, int? position = null, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } collection) throw new KeyNotFoundException($"No collection {id}");
        if (collection.IsPlaylist)
        {
            // Jellyfin adds what the ids stand for (a series: its episodes), duplicates included, at the position or at the end.
            var added = await ExpandAsync(itemIds, ct);
            if (added.Count == 0) return;
            await store.ChangeEntriesAsync(id, current =>
            {
                if (position is not { } p || p >= current.Count) return [.. current, .. added];
                if (p <= 0) return [.. added, .. current];
                return [.. current[..p], .. added, .. current[p..]];
            }, added: true, ct);
            return;
        }

        // A box set keeps each item once, folders included.
        var wanted = await ExistingAsync(itemIds, ct);
        await store.ChangeEntriesAsync(id, current =>
        {
            var fresh = wanted.Where(i => !current.Contains(i)).Distinct().ToList();
            return fresh.Count == 0 ? null : [.. current, .. fresh];
        }, added: true, ct);
    }

    public Task RemoveAsync(Guid id, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var gone = itemIds.ToHashSet();
        return store.ChangeEntriesAsync(id, current =>
        {
            var next = current.Where(i => !gone.Contains(i)).ToList();
            return next.Count == current.Count ? null : next;
        }, added: false, ct);
    }

    public Task MoveAsync(Guid id, Guid itemId, int newIndex, CancellationToken ct = default) =>
        store.ChangeEntriesAsync(id, current =>
        {
            var from = current.IndexOf(itemId);
            if (from < 0 || from == newIndex) return null;
            var next = new List<Guid>(current);
            next.RemoveAt(from);
            next.Insert(Math.Clamp(newIndex, 0, next.Count), itemId);
            return next;
        }, added: false, ct);

    public async Task SetShareAsync(Guid id, Guid userId, bool canEdit, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } playlist || playlist.OwnerId == userId) return;
        playlist.Shares = [.. playlist.Shares.Where(s => s.UserId != userId), new CollectionShare(userId, canEdit)];
        await store.UpdateAsync(playlist, ct);
    }

    public async Task RemoveShareAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } playlist) return;
        playlist.Shares = [.. playlist.Shares.Where(s => s.UserId != userId)];
        await store.UpdateAsync(playlist, ct);
    }

    public async Task SetImagesAsync(Guid id, ItemImages images, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } collection) return;
        collection.Images = images;
        await store.UpdateAsync(collection, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => store.DeleteAsync(id, ct);

    /// <summary>
    /// Jellyfin's RemovePlaylistsAsync for a deleted user: a playlist shared with others goes to the share that may edit (or the first),
    /// one that nobody else sees is deleted, a public one stays. The user also leaves the playlists of others.
    /// </summary>
    public async Task ForgetUserAsync(Guid userId, CancellationToken ct)
    {
        foreach (var playlist in await store.OfUserAsync(userId, ct))
        {
            if (playlist.OwnerId != userId)
            {
                playlist.Shares = [.. playlist.Shares.Where(s => s.UserId != userId)];
                await store.UpdateAsync(playlist, ct);
                continue;
            }
            var ranked = playlist.Shares.OrderByDescending(s => s.CanEdit).ToList();
            if (ranked.Count > 0)
            {
                playlist.OwnerId = ranked[0].UserId;
                playlist.Shares = ranked.Skip(1).ToList();
                await store.UpdateAsync(playlist, ct);
            }
            else if (!playlist.OpenAccess)
            {
                await store.DeleteAsync(playlist.Id, ct);
            }
        }
    }

    public async Task<List<Guid>> ExpandAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct = default) =>
        await ExpandAsync(itemIds, [], ct);

    private async Task<List<Guid>> ExpandAsync(IReadOnlyList<Guid> itemIds, HashSet<Guid> visited, CancellationToken ct)
    {
        var result = new List<Guid>();
        if (itemIds.Count == 0) return result;
        var refs = (await items.GetManyAsync(itemIds, ct)).ToDictionary(r => r.Id);
        foreach (var id in itemIds)
        {
            if (refs.TryGetValue(id, out var r))
            {
                if (r.Type is ItemTypes.Series or ItemTypes.Season)
                    result.AddRange((await items.GetDescendantsAsync(id, ct)).Where(d => d.Type == ItemTypes.Episode).Select(d => d.Id));
                else
                    result.Add(id);
            }
            else if (visited.Add(id) && await store.GetAsync(id, ct) is not null)
            {
                // A playlist or box set added to a playlist: its lines, expanded too (a list never expands into itself twice).
                result.AddRange(await ExpandAsync(await EntriesAsync(id, ct), visited, ct));
            }
        }
        return result;
    }

    /// <summary>The ids that are catalog items or collections, in the given order (unknown ids are skipped, as Jellyfin's lookups do).</summary>
    private async Task<List<Guid>> ExistingAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var known = (await items.GetManyAsync(ids, ct)).Select(r => r.Id).ToHashSet();
        var result = new List<Guid>(ids.Count);
        foreach (var id in ids)
            if (known.Contains(id) || await store.GetAsync(id, ct) is not null) result.Add(id);
        return result;
    }
}
