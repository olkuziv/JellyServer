namespace JellyServer.Catalog.Contracts;

/// <summary>What the metadata system needs to know about an item to look it up.</summary>
public sealed record ItemRef(
    Guid Id, string Type, string Name, int? ProductionYear, Guid LibraryId, Guid? SeriesId, int? ParentIndexNumber, int? IndexNumber,
    IReadOnlyDictionary<string, string> ProviderIds);

/// <summary>Read access to the catalog for the metadata system: ids, types, names and known provider ids, nothing heavier.</summary>
public interface IItemRefs
{
    /// <summary>A page of items ordered by id, after <paramref name="afterId"/> (null = from the start).</summary>
    Task<List<ItemRef>> PageAsync(Guid? afterId, int limit, CancellationToken ct = default);

    Task<List<ItemRef>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Seasons and episodes below a series, or the episodes of a season (empty for anything else).</summary>
    Task<List<ItemRef>> GetDescendantsAsync(Guid itemId, CancellationToken ct = default);
}

/// <summary>One provider answer to lay over an item.</summary>
/// <param name="ReplaceAll">A manual refresh: the provider's values replace local ones, not only fill the gaps.</param>
/// <param name="ReplaceImages">Whether artwork is replaced too; null follows <paramref name="ReplaceAll"/>.</param>
/// <param name="Previous">What the providers reported for this item last time (null when nothing was stored); the catalog uses it to tell
/// values that came from a provider from values the user or the NFO set.</param>
public sealed record RemoteMetadataUpdate(Guid ItemId, RemoteMetadata Metadata, bool ReplaceAll = false, bool? ReplaceImages = null, RemoteMetadata? Previous = null);

/// <summary>
/// The raw provider answers, kept by the metadata system (the catalog does not store them). A scan rewrites an item's local values
/// and asks for them here to lay them over again.
/// </summary>
public interface IRemoteMetadataSource
{
    /// <summary>What providers reported for these items (items without any are not returned).</summary>
    Task<Dictionary<Guid, RemoteMetadata>> LoadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <summary>Where the metadata system delivers what it found; the catalog lays it over the items and refreshes caches.</summary>
public interface IRemoteMetadataSink
{
    /// <summary>Applies the updates in one batch; returns how many items changed.</summary>
    Task<int> ApplyAsync(IReadOnlyList<RemoteMetadataUpdate> updates, CancellationToken ct = default);
}

/// <summary>The configured libraries (they only change through the catalog's own API).</summary>
public interface ILibraryDirectory
{
    IReadOnlyList<Library> All { get; }

    Library? Find(Guid id);
}

/// <summary>A movie and the set (TMDb collection, NFO <c>&lt;set&gt;</c>) it belongs to.</summary>
public sealed record MovieSetRef(Guid Id, Guid LibraryId, string SetName);

/// <summary>Movies grouped by set, for the Collections module's automatic box sets.</summary>
public interface IMovieSets
{
    /// <summary>The movies among <paramref name="ids"/> that belong to a set.</summary>
    Task<List<MovieSetRef>> OfMoviesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Every movie of these libraries that belongs to one of the sets (names compared without case).</summary>
    Task<List<MovieSetRef>> InSetsAsync(IReadOnlyCollection<string> setNames, IReadOnlyCollection<Guid> libraryIds, CancellationToken ct = default);

    /// <summary>Every movie of these libraries that belongs to a set.</summary>
    Task<List<MovieSetRef>> OfLibrariesAsync(IReadOnlyCollection<Guid> libraryIds, CancellationToken ct = default);
}

/// <summary>A library's options were saved (an option such as automatic collections may have just been switched on).</summary>
public sealed record LibraryOptionsChanged(Guid LibraryId);

/// <summary>Provider metadata was laid over these items (their set names may have changed).</summary>
public sealed record ItemsMetadataApplied(IReadOnlyList<Guid> ItemIds);

/// <summary>A library scan finished: these items were added, changed or lost an aggregate.</summary>
public sealed record ScanCompleted(Guid LibraryId, IReadOnlyList<Guid> ChangedItemIds);