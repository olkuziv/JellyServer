using JellyServer.Catalog.Contracts;

namespace JellyServer.Collections.Contracts;

/// <summary>
/// Playlists and box sets with Jellyfin's rules (PlaylistManager, CollectionManager). Entries point at catalog items by id, in order;
/// like Jellyfin's linked children, a line is identified by its item id (the PlaylistItemId clients get). Adding a series or season to
/// a playlist adds its episodes, a box set keeps the series itself. Checks that depend on the caller's role (administrator, collection
/// management) are the caller's; ownership and sharing are known here (<see cref="Collection.CanEdit"/>).
/// </summary>
public interface ICollectionManager
{
    Task<Collection?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>All collections of a kind that <paramref name="userId"/> may see, by name.</summary>
    Task<List<Collection>> ListAsync(string kind, Guid userId, CancellationToken ct = default);

    /// <summary>The item ids of a collection, in order (a playlist may hold an item more than once).</summary>
    Task<List<Guid>> EntriesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Entries of several collections at once (listing pages show counts and artwork).</summary>
    Task<Dictionary<Guid, List<Guid>>> EntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    Task<Collection> CreatePlaylistAsync(Guid ownerId, string name, IReadOnlyList<Guid> itemIds, string? mediaType,
        IReadOnlyList<CollectionShare> shares, bool openAccess, CancellationToken ct = default);

    /// <summary>Jellyfin's UpdatePlaylist: each value that is not null replaces the current one (<paramref name="itemIds"/>: all entries).</summary>
    Task UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid>? itemIds, IReadOnlyList<CollectionShare>? shares, bool? openAccess,
        CancellationToken ct = default);

    Task<Collection> CreateBoxSetAsync(string name, IReadOnlyList<Guid> itemIds, bool isLocked, CancellationToken ct = default);

    /// <summary>Adds items: to a playlist at <paramref name="position"/> (or at the end), to a box set unless already in it.</summary>
    Task AddAsync(Guid id, IReadOnlyList<Guid> itemIds, int? position = null, CancellationToken ct = default);

    /// <summary>Removes every line pointing at these items.</summary>
    Task RemoveAsync(Guid id, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>Moves the line of <paramref name="itemId"/> so it ends up at <paramref name="newIndex"/>.</summary>
    Task MoveAsync(Guid id, Guid itemId, int newIndex, CancellationToken ct = default);

    /// <summary>Shares a playlist with a user (or changes whether they may edit it).</summary>
    Task SetShareAsync(Guid id, Guid userId, bool canEdit, CancellationToken ct = default);

    Task RemoveShareAsync(Guid id, Guid userId, CancellationToken ct = default);

    Task SetImagesAsync(Guid id, ItemImages images, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>The playable items behind ids: series and seasons become their episodes, collections their (expanded) entries.</summary>
    Task<List<Guid>> ExpandAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct = default);
}
