namespace JellyServer.Media.Contracts;

/// <summary>
/// The files behind catalog items: what plays, where it is and what is inside. Only playback and the item's own page need it,
/// so it lives apart from the catalog (its own schema, and its own database when <c>ConnectionStrings:media</c> is set).
/// </summary>
public interface IMediaSourceStore
{
    /// <summary>The sources of each item, main file first (items without any are not returned).</summary>
    Task<Dictionary<Guid, List<MediaSource>>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>The item a source belongs to, null when no source has that id.</summary>
    Task<Guid?> FindItemAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>Makes <paramref name="sources"/> the complete list of these items' files: sources not in it are removed.</summary>
    Task ReplaceAsync(IReadOnlyCollection<Guid> itemIds, IReadOnlyList<MediaSource> sources, CancellationToken ct = default);

    /// <summary>Stores what ffprobe found in one source.</summary>
    Task SaveMediaInfoAsync(Guid sourceId, MediaInfo info, CancellationToken ct = default);
}
