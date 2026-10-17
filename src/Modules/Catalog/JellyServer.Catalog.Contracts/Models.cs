using System.Text.Json.Serialization;

namespace JellyServer.Catalog.Contracts;

public static class ItemTypes
{
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string Season = "Season";
    public const string Episode = "Episode";
}

/// <summary>One browsable entity: Movie, Series, Season or Episode.</summary>
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

public sealed record Person(string Name, string? Role, string Type)
{
    /// <summary>The provider's id of the person (TMDB person id).</summary>
    public string? ProviderId { get; init; }

    /// <summary>Portrait as http(s) URL.</summary>
    public string? ImageUrl { get; init; }
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

public sealed class MediaInfo
{
    /// <summary>True when the streams come from ffprobe rather than NFO streamdetails.</summary>
    public bool Probed { get; set; }
    public long Bitrate { get; set; }
    public double DurationSec { get; set; }
    public List<VideoStream> Video { get; set; } = [];
    public List<AudioStream> Audio { get; set; } = [];
    /// <summary>Languages of embedded subtitle streams.</summary>
    public List<string> Subtitles { get; set; } = [];
    /// <summary>
    /// ffprobe details of the embedded subtitle streams, in file order. Null for NFO data and for items
    /// probed before this field existed (those only have <see cref="Subtitles"/> languages).
    /// </summary>
    public List<EmbeddedSubtitle>? SubtitleStreams { get; set; }

    /// <summary>Embedded subtitle streams: probed details, else language-only entries (codec unknown).</summary>
    [JsonIgnore]
    public IReadOnlyList<EmbeddedSubtitle> EmbeddedSubtitles =>
        SubtitleStreams ?? [.. Subtitles.Select(l => new EmbeddedSubtitle(-1, "", l.Length > 0 ? l : null))];
}

/// <summary>An embedded subtitle stream. <paramref name="Index"/> is ffprobe's absolute stream index (-1 unknown).</summary>
public sealed record EmbeddedSubtitle(int Index, string Codec, string? Language)
{
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsHearingImpaired { get; init; }
}

public sealed record VideoStream(string Codec, int Width, int Height, double Aspect, string? HdrType)
{
    public string? Profile { get; init; }
    public int BitDepth { get; init; }
    /// <summary>Frames per second, 0 when unknown.</summary>
    public double FrameRate { get; init; }
    /// <summary>Interlaced or telecined (ffprobe field_order, or NFO scantype).</summary>
    public bool Interlaced { get; init; }
}
public sealed record AudioStream(string Codec, string? Language, int Channels);
public sealed record SubtitleFile(string Path, string Format, string? Language, bool Forced);

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
