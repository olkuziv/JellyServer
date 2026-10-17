using JellyServer.Catalog.Contracts;
using JellyServer.Media.Contracts;

namespace JellyServer.Services;

/// <summary>A DeviceProfile SubtitleProfile: how a client can take a subtitle format (Embed, External, Hls, Encode).</summary>
public sealed class SubtitleProfileDto
{
    public string? Format { get; set; }
    public string? Method { get; set; }
    public string? DidlMode { get; set; }
    public string? Language { get; set; }
    public string? Container { get; set; }
}

/// <summary>Subtitle codec families, as Jellyfin's MediaStream classifies them.</summary>
public static class SubtitleCodecs
{
    /// <summary>ffprobe codec names to Jellyfin's (lower-case: clients compare case-insensitively).</summary>
    public static string Normalize(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "hdmv_pgs_subtitle" => "pgssub",
        "dvd_subtitle" => "dvdsub",
        "dvb_subtitle" => "dvbsub",
        "dvb_teletext" => "dvbtxt",
        var x => x,
    };

    public static bool IsText(string? codec)
    {
        var c = codec ?? "";
        return c.Contains("microdvd", StringComparison.OrdinalIgnoreCase)
               || (!c.Contains("pgs", StringComparison.OrdinalIgnoreCase)
                   && !c.Contains("dvdsub", StringComparison.OrdinalIgnoreCase)
                   && !c.Contains("vobsub", StringComparison.OrdinalIgnoreCase)
                   && !c.Contains("dvbsub", StringComparison.OrdinalIgnoreCase)
                   && !c.Equals("sup", StringComparison.OrdinalIgnoreCase)
                   && !c.Equals("sub", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPgs(string? codec) =>
        (codec ?? "").Contains("pgs", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "sup", StringComparison.OrdinalIgnoreCase);

    public static bool IsVobSub(string? codec) =>
        (codec ?? "").Contains("dvdsub", StringComparison.OrdinalIgnoreCase) || (codec ?? "").Contains("vobsub", StringComparison.OrdinalIgnoreCase);

    /// <summary>ASS/SSA keep their styling only as ASS/SSA, so Jellyfin never converts from or to them.</summary>
    public static bool CanConvert(string fromCodec, string toFormat) =>
        fromCodec is not ("ass" or "ssa") && toFormat.ToLowerInvariant() is not ("ass" or "ssa");
}

/// <summary>
/// One subtitle entry of an item's MediaStreams. Layout (shared by DTOs and the subtitle endpoint):
/// videos, audio, embedded subtitles, then sidecar files.
/// </summary>
public sealed record SubtitleTrack(int Index, string Codec, string? Language, EmbeddedSubtitle? Embedded, int Ordinal, SubtitleFile? File)
{
    public bool IsExternal => File is not null;
    /// <summary>An embedded stream without a known codec (NFO data) is neither text nor image, like in Jellyfin.</summary>
    public bool IsText => (IsExternal || Codec.Length > 0) && SubtitleCodecs.IsText(Codec);
    public bool IsPgs => Codec.Length > 0 && SubtitleCodecs.IsPgs(Codec);
    public bool IsVobSub => Codec.Length > 0 && SubtitleCodecs.IsVobSub(Codec);
    /// <summary>Jellyfin's SupportsExternalStream: sidecars, and embedded streams ffmpeg can extract.</summary>
    public bool SupportsExternalStream => IsExternal || IsText || IsPgs || IsVobSub;

    public static IReadOnlyList<SubtitleTrack> List(Item it)
    {
        var list = new List<SubtitleTrack>();
        var index = 0;
        if (it.MediaInfo is { } mi)
        {
            index = mi.Video.Count + mi.Audio.Count;
            foreach (var (s, i) in mi.EmbeddedSubtitles.Select((s, i) => (s, i)))
                list.Add(new SubtitleTrack(index++, s.Codec, s.Language, s, i, null));
        }
        for (var i = 0; it.Subtitles is not null && i < it.Subtitles.Count; i++)
        {
            var f = it.Subtitles[i];
            list.Add(new SubtitleTrack(index++, f.Format == "srt" ? "subrip" : f.Format, f.Language, null, i, f));
        }
        return list;
    }

    public static SubtitleTrack? Find(Item it, int index) => List(it).FirstOrDefault(t => t.Index == index);
}

/// <summary>
/// Port of Jellyfin's StreamBuilder.GetSubtitleProfile: per subtitle stream, Embed / External (with the format the
/// client asked for) or Encode. Burn-in and HLS subtitle playlists are not implemented, so Encode and Hls end up as
/// Embed (the stream stays in the file; native players may still show it).
/// </summary>
public static class SubtitleDelivery
{
    public const string Embed = "Embed", External = "External", Encode = "Encode", Hls = "Hls";

    /// <summary>What jellyfin-web sends by default; used when a client sends no DeviceProfile.</summary>
    public static readonly IReadOnlyList<SubtitleProfileDto> DefaultProfiles =
    [
        new() { Format = "vtt", Method = External },
        new() { Format = "ass", Method = External },
        new() { Format = "ssa", Method = External },
    ];

    /// <param name="transcode">Jellyfin's PlayMethod.Transcode (our HLS remux/transcode).</param>
    /// <param name="outputContainer">Media container for direct play, segment container when transcoding.</param>
    /// <param name="hls">The transcode is delivered over HLS (TranscodingSubProtocol=hls).</param>
    /// <param name="canExtract">EnableSubtitleExtraction (Jellyfin's ITranscoderSupport.CanExtractSubtitles).</param>
    public static (string Method, string Format) Decide(SubtitleTrack s, IReadOnlyList<SubtitleProfileDto> profiles, bool transcode,
        string? outputContainer, bool hls, bool canExtract)
    {
        var (method, format) = Jellyfin(s, profiles, transcode, outputContainer, hls, canExtract);
        return method is Encode or Hls ? (Embed, format) : (method, format);
    }

    /// <summary>The unmodified Jellyfin decision (Encode = burn in).</summary>
    public static (string Method, string Format) Jellyfin(SubtitleTrack s, IReadOnlyList<SubtitleProfileDto> profiles, bool transcode,
        string? outputContainer, bool hls, bool canExtract)
    {
        if (CanConsiderEmbed(s, transcode, hls, outputContainer))
        {
            // Supported embedded subs of the same format, then of a convertible one.
            foreach (var p in EmbedProfiles())
                if (s.IsText == SubtitleCodecs.IsText(p.Format) && SameFormat(p.Format, s.Codec)) return (Embed, p.Format!);
            foreach (var p in EmbedProfiles())
                if (s.IsText && SubtitleCodecs.CanConvert(s.Codec, p.Format ?? "")) return (Embed, p.Format!);
        }
        return ExternalProfile(false) ?? ExternalProfile(true) ?? (Encode, s.Codec);

        IEnumerable<SubtitleProfileDto> EmbedProfiles() => profiles.Where(p =>
            SupportsLanguage(p, s.Language) && Same(p.Method, Embed) && ContainsContainer(p.Container, outputContainer)
            && !(transcode && !IsEmbedSupported(outputContainer)) && !string.IsNullOrEmpty(p.Format));

        (string, string)? ExternalProfile(bool allowConversion)
        {
            foreach (var p in profiles)
            {
                var isExternal = Same(p.Method, External);
                if ((!isExternal && !Same(p.Method, Hls)) || string.IsNullOrEmpty(p.Format)) continue;
                if (!isExternal && !transcode) continue;
                if (!SupportsLanguage(p, s.Language)) continue;
                if (!s.IsExternal && transcode && !canExtract && !s.IsPgs && !s.IsVobSub) continue;

                var vobSubMks = IsVobSubMksDelivery(p) && s.IsVobSub && !s.IsExternal;
                if ((isExternal && (vobSubMks || (!IsVobSubMksDelivery(p) && s.IsText == SubtitleCodecs.IsText(p.Format))))
                    || (!isExternal && s.IsText))
                {
                    if (vobSubMks || SameFormat(s.Codec, p.Format)) return (p.Method!, p.Format);
                    if (!allowConversion) continue;
                    if (s.IsText && s.SupportsExternalStream && SubtitleCodecs.CanConvert(s.Codec, p.Format)) return (p.Method!, p.Format);
                }
            }
            return null;
        }
    }

    private static bool CanConsiderEmbed(SubtitleTrack s, bool transcode, bool hls, string? container) =>
        s.IsExternal ? transcode && !hls && IsEmbedSupported(container) : !transcode || !hls;

    private static bool IsEmbedSupported(string? container) =>
        !string.IsNullOrEmpty(container) && !ContainsContainer("ts,mpegts,mp4", container) && ContainsContainer("mkv,matroska", container);

    private static bool IsVobSubMksDelivery(SubtitleProfileDto p) =>
        SubtitleCodecs.IsVobSub(p.Format) && !string.IsNullOrWhiteSpace(p.Container) && ContainsContainer(p.Container, "mks");

    private static bool SupportsLanguage(SubtitleProfileDto p, string? language) =>
        string.IsNullOrEmpty(p.Language) || ContainsContainer(p.Language, string.IsNullOrEmpty(language) ? "und" : language);

    /// <summary>Jellyfin's ContainerHelper.ContainsContainer: comma lists, "-" negates, empty profile accepts all.</summary>
    public static bool ContainsContainer(string? profile, string? input)
    {
        var negative = profile?.StartsWith('-') == true;
        if (negative) profile = profile![1..];
        if (string.IsNullOrEmpty(input)) return negative;
        if (string.IsNullOrEmpty(profile)) return true;
        var allowed = profile.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(i => allowed.Contains(i, StringComparer.OrdinalIgnoreCase)) != negative;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ffprobe names vs. file extensions: subrip is srt, webvtt is vtt.
    private static bool SameFormat(string? a, string? b) => Same(Alias(a), Alias(b));

    private static string? Alias(string? f) => f?.ToLowerInvariant() switch { "subrip" => "srt", "webvtt" => "vtt", var x => x };
}
