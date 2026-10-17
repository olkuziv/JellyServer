using System.Text.Json.Serialization;

namespace JellyServer.Media.Contracts;

/// <summary>
/// One playable file of a catalog item: its main file (<see cref="Id"/> equals <see cref="ItemId"/>, <see cref="Ordinal"/> 0) or
/// another version of it (another resolution, voice-over, ...). The id is what clients call the MediaSourceId.
/// </summary>
public sealed class MediaSource
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>0 for the main file, then the versions in the order the scanner found them.</summary>
    public int Ordinal { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Target of a .strm file.</summary>
    public string? StreamUrl { get; set; }
    public string? Container { get; set; }
    public long Size { get; set; }
    public long RuntimeTicks { get; set; }
    public MediaInfo? MediaInfo { get; set; }
    /// <summary>Sidecar subtitle files.</summary>
    public List<SubtitleFile>? Subtitles { get; set; }
    public string Fingerprint { get; set; } = "";
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
