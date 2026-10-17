using JellyServer.Catalog.Contracts;

namespace JellyServer.Metadata.Contracts;

/// <summary>Language and country a lookup is made for (from the library's options, else the server's).</summary>
public sealed record MetadataContext(string Language, string Country);

public enum ProviderStatus
{
    Found,

    /// <summary>The provider does not know the item; asking again is pointless for a while.</summary>
    NotFound,

    /// <summary>A temporary failure (network, rate limit): try again soon.</summary>
    Failed,
}

public sealed record MetadataRequest(ItemRef Item, IReadOnlyDictionary<string, string> ProviderIds, MetadataContext Context);

public sealed record ProviderResult(ProviderStatus Status, RemoteMetadata? Metadata)
{
    public static ProviderResult NotFound { get; } = new(ProviderStatus.NotFound, null);
    public static ProviderResult Failed { get; } = new(ProviderStatus.Failed, null);
}

/// <summary>A whole season at once: providers answer for the season and all of its episodes with one request.</summary>
public sealed record SeasonRequest(ItemRef Series, IReadOnlyDictionary<string, string> SeriesProviderIds, int SeasonNumber, MetadataContext Context);

public sealed record SeasonProviderResult(ProviderStatus Status, RemoteMetadata? Season, IReadOnlyDictionary<int, RemoteMetadata> Episodes)
{
    public static SeasonProviderResult NotFound { get; } = new(ProviderStatus.NotFound, null, new Dictionary<int, RemoteMetadata>());
    public static SeasonProviderResult Failed { get; } = new(ProviderStatus.Failed, null, new Dictionary<int, RemoteMetadata>());
}

/// <summary>
/// A source of item metadata on the internet (TMDB, TVDB, ...). Providers only talk to their service: no database, no catalog.
/// The metadata module decides which providers run for a library, in which order, and combines what they return.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>The name libraries refer to in their options, e.g. "TheMovieDb".</summary>
    string Name { get; }

    /// <summary>False when the provider cannot work (no API key): it is then ignored and not offered in the dashboard.</summary>
    bool IsAvailable { get; }

    /// <summary>Item types the provider can describe (Movie, Series, Season, Episode).</summary>
    IReadOnlyCollection<string> ItemTypes { get; }

    /// <summary>Artwork kinds it supplies (Primary, Backdrop, Logo, Thumb, Banner).</summary>
    IReadOnlyCollection<string> ImageTypes { get; }

    /// <summary>Metadata of a movie or series.</summary>
    Task<ProviderResult> FetchAsync(MetadataRequest request, CancellationToken ct);

    /// <summary>A season and its episodes.</summary>
    Task<SeasonProviderResult> FetchSeasonAsync(SeasonRequest request, CancellationToken ct);
}

/// <summary>Ask for a refresh of an item ("Refresh metadata" in the dashboard and clients).</summary>
public sealed record MetadataRefreshRequest(Guid ItemId, bool Recursive, bool ReplaceMetadata, bool ReplaceImages);

public interface IMetadataRefresh
{
    /// <summary>Queues the refresh and returns; the work happens in the background.</summary>
    Task RefreshAsync(MetadataRefreshRequest request, CancellationToken ct = default);

    /// <summary>Queues everything nobody has looked up yet (the same walk the engine does once in a while).</summary>
    Task ReconcileAsync(CancellationToken ct = default);

    /// <summary>Queues every item again, also those that already have data; the providers' answers update what an earlier lookup stored.</summary>
    Task QueueAllAsync(CancellationToken ct = default);

    /// <summary>Drops the lookups that have not started yet; the ones in progress finish.</summary>
    void Cancel();

    /// <summary>How much of the current batch of lookups is done.</summary>
    MetadataProgress Progress { get; }
}

/// <summary>Items still waiting out of the <paramref name="Total"/> queued since the queue was last empty.</summary>
public readonly record struct MetadataProgress(int Pending, int Total)
{
    public bool IsRunning => Pending > 0;
    public double Percent => Total <= 0 ? 100 : 100.0 * (Total - Pending) / Total;
}

/// <summary>What the dashboard may offer when a library is set up.</summary>
public sealed record MetadataProviderInfo(string Name, IReadOnlyCollection<string> ItemTypes, IReadOnlyCollection<string> ImageTypes);

public interface IMetadataProviders
{
    IReadOnlyList<MetadataProviderInfo> Available { get; }
}