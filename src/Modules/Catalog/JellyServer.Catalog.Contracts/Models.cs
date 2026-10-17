using System.Text.Json.Serialization;
using JellyServer.Media.Contracts;
using JellyServer.People.Contracts;

namespace JellyServer.Catalog.Contracts;

public static class ItemTypes
{
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string Season = "Season";
    public const string Episode = "Episode";
}

/// <summary>
/// One browsable entity: Movie, Series, Season or Episode, as scanners produce it and clients see it. The catalog stores what lists,
/// filters and sorts need; the files (<see cref="StreamUrl"/>, <see cref="Container"/>, <see cref="MediaInfo"/>, <see cref="Subtitles"/>,
/// <see cref="Versions"/>) belong to the Media module and <see cref="People"/> to the People module, each in its own database.
/// </summary>
public sealed class Item
{
    public Guid Id { get; set; }
    public Guid LibraryId { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? SeriesId { get; set; }
    public Guid? SeasonId { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string SortName { get; set; } = "";
    [JsonIgnore] public string SearchText { get; set; } = "";
    /// <summary>The name comes from local metadata (an NFO title): internet providers must not rename the item.</summary>
    public bool NameLocked { get; set; }
    public string Path { get; set; } = "";
    public string? StreamUrl { get; set; }
    public string? Container { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public int? ProductionYear { get; set; }
    public DateTime? PremiereDate { get; set; }
    public DateTime? EndDate { get; set; }
    public float? CommunityRating { get; set; }
    public float? CriticRating { get; set; }
    public string? OfficialRating { get; set; }
    public long RuntimeTicks { get; set; }
    public int? IndexNumber { get; set; }
    public int? ParentIndexNumber { get; set; }
    public string[] Genres { get; set; } = [];
    public string[] Studios { get; set; } = [];
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public List<Person>? People { get; set; }
    public MediaInfo? MediaInfo { get; set; }
    public ItemImages Images { get; set; } = new();
    public List<SubtitleFile>? Subtitles { get; set; }
    public string? CollectionName { get; set; }
    public int ChildCount { get; set; }
    public int RecursiveChildCount { get; set; }
    public long Size { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTime DateCreated { get; set; }
    public DateTime DateModified { get; set; }
    public DateTime? DateLastMediaAdded { get; set; }

    /// <summary>Other files of the same movie or episode (another resolution, voice-over, ...). They have no rows of their own.</summary>
    public List<ItemVersion>? Versions { get; set; }

    /// <summary>Set on the view <see cref="AsVersion"/> returns: the item the version belongs to (user data lives there).</summary>
    [JsonIgnore] public Guid? PrimaryId { get; set; }

    /// <summary>The item as seen when playing <paramref name="v"/>: same metadata, the version's file and id.</summary>
    public Item AsVersion(ItemVersion v)
    {
        var view = (Item)MemberwiseClone();
        view.PrimaryId = Id;
        view.Versions = null;
        view.Id = v.Id;
        view.Path = v.Path;
        view.StreamUrl = v.StreamUrl;
        view.Container = v.Container;
        view.Size = v.Size;
        view.MediaInfo = v.MediaInfo;
        view.Subtitles = v.Subtitles;
        view.Fingerprint = v.Fingerprint;
        if (v.RuntimeTicks > 0) view.RuntimeTicks = v.RuntimeTicks;
        return view;
    }

    [JsonIgnore] public bool IsFolder => Type is ItemTypes.Series or ItemTypes.Season;
    [JsonIgnore] public bool IsStrm => !string.IsNullOrEmpty(StreamUrl);
}

/// <summary>One more file of an <see cref="Item"/>; the id is what clients call the MediaSourceId.</summary>
public sealed class ItemVersion
{
    public Guid Id { get; set; }
    public string Path { get; set; } = "";
    public string? StreamUrl { get; set; }
    public string? Container { get; set; }
    public long Size { get; set; }
    public long RuntimeTicks { get; set; }
    public MediaInfo? MediaInfo { get; set; }
    public List<SubtitleFile>? Subtitles { get; set; }
    public string Fingerprint { get; set; } = "";
}

/// <summary>Artwork as absolute paths or http(s) URLs.</summary>
public sealed class ItemImages
{
    public string? Primary { get; set; }
    public List<string> Backdrops { get; set; } = [];
    public string? Logo { get; set; }
    public string? Thumb { get; set; }
    public string? Banner { get; set; }
    /// <summary>The other Jellyfin image types (<see cref="OtherTypes"/>), by type name.</summary>
    public Dictionary<string, string>? Other { get; set; }

    public static readonly string[] OtherTypes = ["Art", "Box", "BoxRear", "Disc", "Menu", "Screenshot", "Profile"];

    public string? Get(string type, int index) => type switch
    {
        "Primary" => Primary,
        "Backdrop" => index >= 0 && index < Backdrops.Count ? Backdrops[index] : null,
        "Logo" => Logo,
        "Thumb" => Thumb,
        "Banner" => Banner,
        _ => Other is not null && Other.TryGetValue(type, out var p) ? p : null,
    };
}

public sealed class Library
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string CollectionType { get; set; } = "";
    public string[] Paths { get; set; } = [];
    /// <summary>Jellyfin LibraryOptions as JSON (PathInfos are derived from <see cref="Paths"/>).</summary>
    public string OptionsJson { get; set; } = "{}";
    /// <summary>Raw column; <see cref="Images"/> is parsed from it when the libraries are loaded.</summary>
    public string ImagesJson { get; set; } = "{}";
    public ItemImages Images { get; set; } = new();
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}
