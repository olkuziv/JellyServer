using JellyServer.Kernel;

namespace JellyServer.People.Contracts;

/// <summary>Someone credited on an item: the cast with their role, the crew with their job (<paramref name="Type"/>, Jellyfin's PersonKind).</summary>
public sealed record Person(string Name, string? Role, string Type)
{
    /// <summary>The provider's id of the person (TMDB person id).</summary>
    public string? ProviderId { get; init; }

    /// <summary>Portrait as http(s) URL.</summary>
    public string? ImageUrl { get; init; }
}

/// <summary>A person as an entity of its own (what <c>/Persons</c> lists).</summary>
public sealed record PersonInfo(Guid Id, string Name, string? ImageUrl, string? ProviderId);

public static class PersonIds
{
    /// <summary>A person's id: one per name, like Jellyfin's Person items.</summary>
    public static Guid Of(string name) => Ids.FromKey("person", name);
}

/// <summary>Jellyfin's PersonKind, in its order (the numbers are what the database stores).</summary>
public static class PersonKinds
{
    public static readonly string[] All =
    [
        "Unknown", "Actor", "Director", "Composer", "Writer", "GuestStar", "Producer", "Conductor", "Lyricist", "Arranger", "Engineer",
        "Mixer", "Remixer", "Creator", "Artist", "AlbumArtist", "Author", "Illustrator", "Penciller", "Inker", "Colorist", "Letterer",
        "CoverArtist", "Editor", "Translator", "Narrator",
    ];

    private static readonly Dictionary<string, short> Codes =
        All.Select((k, i) => (k, (short)i)).ToDictionary(x => x.k, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    public static short Code(string? kind) => kind is not null && Codes.TryGetValue(kind, out var c) ? c : (short)0;

    public static string Name(short code) => code >= 0 && code < All.Length ? All[code] : All[0];
}

/// <summary>Backend form of Jellyfin's <c>/Persons</c> query.</summary>
public sealed class PersonQuery
{
    /// <summary>Lower-case text the name has to contain.</summary>
    public string? SearchTerm { get; set; }
    public string? NameStartsWith { get; set; }
    public string? NameLessThan { get; set; }
    public string? NameStartsWithOrGreater { get; set; }
    /// <summary>Only people credited with one of these kinds (any kind when null).</summary>
    public string[]? PersonTypes { get; set; }
    public string[]? ExcludePersonTypes { get; set; }
    /// <summary>Only people credited on one of these items.</summary>
    public Guid[]? AppearsInItemIds { get; set; }
    /// <summary>Only these people (favourites, ...).</summary>
    public Guid[]? Ids { get; set; }
    public int StartIndex { get; set; }
    public int? Limit { get; set; }
}
