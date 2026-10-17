
namespace JellyServer.UserData.Contracts;

/// <summary>
/// Public contract of the UserData module. Other modules never touch its tables: the catalog asks for
/// item IDs here and then filters its own data with them, so the two can live on different servers.
/// </summary>
public interface IUserDataStore
{
    /// <summary>State of the given items (missing ones were never touched by the user).</summary>
    Task<Dictionary<Guid, ItemUserData>> GetManyAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>Everything the user has state for; used to order a catalog page by user data.</summary>
    Task<Dictionary<Guid, ItemUserData>> GetAllAsync(Guid userId, CancellationToken ct = default);

    Task<List<Guid>> FavoriteIdsAsync(Guid userId, CancellationToken ct = default);

    Task<List<Guid>> PlayedIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Items with a resume position, most recently played first.</summary>
    Task<List<Guid>> ResumableIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Played items with the time they were last played.</summary>
    Task<List<(Guid ItemId, DateTime? LastPlayedAt)>> PlayedWithTimeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Those of <paramref name="itemIds"/> the user has played.</summary>
    Task<HashSet<Guid>> PlayedAmongAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>
    /// Records a playback position. Periodic reports are buffered (write-behind) and reach the database within a few seconds;
    /// <paramref name="durable"/> writes straight through (playback stopped) and wins over anything still buffered.
    /// </summary>
    Task SaveProgressAsync(Guid userId, Guid itemId, long position, bool durable = false);

    /// <summary>Sets or clears "played"; marking played resets the resume position.</summary>
    Task MarkPlayedAsync(Guid userId, Guid itemId, bool played);

    /// <summary>The same for many items at once (the episodes of a series or season).</summary>
    Task MarkManyPlayedAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, bool played);

    Task SetFavoriteAsync(Guid userId, Guid itemId, bool favorite);

    /// <summary>Partial update from a UserItemDataDto; null fields are left unchanged.</summary>
    Task UpdateAsync(Guid userId, Guid itemId, long? position, bool? played, bool? favorite, int? playCount);

    Task<string?> GetDisplayPreferencesAsync(Guid userId, string prefId, string client);

    Task SaveDisplayPreferencesAsync(Guid userId, string prefId, string client, string json);
}
