namespace JellyServer.Catalog.Contracts;

/// <summary>Library content types. Jellyfin stores "mixed movies and shows" as a null collection type; here it is "mixed" internally.</summary>
public static class LibraryTypes
{
    public const string Movies = "movies";
    public const string TvShows = "tvshows";
    public const string Mixed = "mixed";

    /// <summary>The type to store, or null when the content type is not supported (yet).</summary>
    public static string? Normalize(string? clientValue)
    {
        if (string.IsNullOrWhiteSpace(clientValue)) return Mixed;
        var v = clientValue.Trim().ToLowerInvariant();
        return v is Movies or TvShows or Mixed ? v : null;
    }

    /// <summary>The value clients see: mixed libraries have no collection type.</summary>
    public static string? ForClient(string stored) => stored == Mixed ? null : stored;

    /// <summary>The item types a library shows at its top level.</summary>
    public static string[] TopLevelTypes(string stored) => stored switch
    {
        TvShows => [ItemTypes.Series],
        Mixed => [ItemTypes.Movie, ItemTypes.Series],
        _ => [ItemTypes.Movie],
    };
}
