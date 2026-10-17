using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog.Contracts;

/// <summary>
/// Metadata about an item as reported by an internet provider (TMDB, ...). It is stored apart from the item and laid over it:
/// local data (NFO, artwork files) wins, a provider only fills what is empty - unless a refresh replaces everything.
/// </summary>
public sealed class RemoteMetadata
{
    public string? Name { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public int? ProductionYear { get; set; }
    public DateTime? PremiereDate { get; set; }
    public DateTime? EndDate { get; set; }
    public float? CommunityRating { get; set; }
    public float? CriticRating { get; set; }
    public string? OfficialRating { get; set; }
    public long? RuntimeTicks { get; set; }
    public string[] Genres { get; set; } = [];
    public string[] Studios { get; set; } = [];
    public string? CollectionName { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public List<Person>? People { get; set; }
    /// <summary>Artwork as http(s) URLs.</summary>
    public ItemImages Images { get; set; } = new();

    /// <summary>
    /// This metadata with every gap filled from <paramref name="lowerPriority"/>: how several providers are combined
    /// (the first provider in the library's order wins, later ones add what is missing).
    /// </summary>
    public RemoteMetadata MergeUnder(RemoteMetadata lowerPriority)
    {
        var o = lowerPriority;
        var ids = new Dictionary<string, string>(o.ProviderIds);
        foreach (var kv in ProviderIds) ids[kv.Key] = kv.Value;
        var images = new ItemImages
        {
            Primary = Images.Primary ?? o.Images.Primary, Logo = Images.Logo ?? o.Images.Logo,
            Thumb = Images.Thumb ?? o.Images.Thumb, Banner = Images.Banner ?? o.Images.Banner,
            Backdrops = Images.Backdrops.Count > 0 ? [.. Images.Backdrops] : [.. o.Images.Backdrops],
        };
        return new RemoteMetadata
        {
            Name = NonEmpty(Name) ?? NonEmpty(o.Name), OriginalTitle = NonEmpty(OriginalTitle) ?? NonEmpty(o.OriginalTitle),
            Overview = NonEmpty(Overview) ?? NonEmpty(o.Overview), Tagline = NonEmpty(Tagline) ?? NonEmpty(o.Tagline),
            ProductionYear = ProductionYear ?? o.ProductionYear, PremiereDate = PremiereDate ?? o.PremiereDate, EndDate = EndDate ?? o.EndDate,
            CommunityRating = CommunityRating ?? o.CommunityRating, CriticRating = CriticRating ?? o.CriticRating,
            OfficialRating = NonEmpty(OfficialRating) ?? NonEmpty(o.OfficialRating), RuntimeTicks = RuntimeTicks ?? o.RuntimeTicks,
            Genres = Genres.Length > 0 ? Genres : o.Genres, Studios = Studios.Length > 0 ? Studios : o.Studios,
            CollectionName = NonEmpty(CollectionName) ?? NonEmpty(o.CollectionName), ProviderIds = ids,
            People = People is { Count: > 0 } ? People : o.People, Images = images,
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}