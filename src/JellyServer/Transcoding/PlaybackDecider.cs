using System.Globalization;
using JellyServer.Api;
using JellyServer.Catalog.Contracts;
using JellyServer.Media.Contracts;

namespace JellyServer.Transcoding;

/// <summary>How a stream will be converted: each track is either copied or re-encoded.</summary>
public sealed record TranscodePlan(
    bool CopyVideo, string VideoCodec, bool CopyAudio, string AudioCodec, int AudioChannels,
    int AudioIndex, string SegmentContainer, long? MaxBitrate)
{
    public bool IsRemux => CopyVideo;
    public string SegmentExtension => SegmentContainer == "mp4" ? "mp4" : "ts";

    public string ToQuery() => string.Join('&',
        $"VideoCodec={VideoCodec}", $"CopyVideo={CopyVideo}", $"AudioCodec={AudioCodec}", $"CopyAudio={CopyAudio}",
        $"AudioChannels={AudioChannels}", $"AudioIndex={AudioIndex}", $"SegmentContainer={SegmentContainer}",
        MaxBitrate is { } b ? $"MaxBitrate={b}" : "MaxBitrate=");

    public static TranscodePlan? FromQuery(IQueryCollection q)
    {
        var container = q.Str("segmentContainer");
        if (container is not ("ts" or "mp4")) return null;
        return new TranscodePlan(
            q.Bool("copyVideo") ?? false, q.Str("videoCodec") ?? "h264", q.Bool("copyAudio") ?? false,
            q.Str("audioCodec") ?? "aac", q.Int("audioChannels") ?? 2, q.Int("audioIndex") ?? 0, container, q.Long("maxBitrate"));
    }

    /// <summary>Stable key: the same plan for the same item and session reuses one ffmpeg job.</summary>
    public string Key(Guid itemId, string playSessionId) => $"{itemId:N}-{playSessionId}-{ToQuery().GetHashCode():x8}";
}

public sealed record PlaybackDecision(bool DirectPlay, TranscodePlan? Plan, string Reason);

/// <summary>
/// Decides between direct play and conversion using the client's DeviceProfile:
/// direct play when container + codecs are supported; otherwise HLS where each track
/// is copied if the client's HLS profile accepts it, else re-encoded (H.264 / AAC).
/// </summary>
public static class PlaybackDecider
{
    private static readonly string[] EncodableAudio = ["aac", "mp3", "ac3", "eac3"];

    /// <param name="encodable">Codecs a working video pipeline can encode to (see <see cref="PipelineSelector.EncodableCodecs"/>).</param>
    /// <param name="allowHevc">Jellyfin's AllowHevcEncoding: HEVC output when the client's HLS profile lists it.</param>
    /// <param name="allowAv1">Jellyfin's AllowAv1Encoding.</param>
    public static PlaybackDecision Decide(Item item, MediaInfo? mi, PlaybackInfoRequest req, bool transcodingEnabled, IReadOnlySet<string> encodable,
        bool allowHevc = false, bool allowAv1 = false)
    {
        var video = mi?.Video.FirstOrDefault();
        var audioIndex = SelectedAudio(mi, req.AudioStreamIndex);
        var audio = mi is not null && audioIndex < mi.Audio.Count ? mi.Audio[audioIndex] : null;
        var vCodec = Codecs.NormalizeVideo(video?.Codec ?? "");
        var aCodec = Codecs.NormalizeAudio(audio?.Codec ?? "");
        var profile = req.DeviceProfile;

        if (profile?.DirectPlayProfiles is null || !transcodingEnabled)
            return new(true, null, "no device profile");

        var directAllowed = req.EnableDirectPlay != false || req.EnableDirectStream != false;
        if (directAllowed)
        {
            var match = profile.DirectPlayProfiles.FirstOrDefault(p =>
                string.Equals(p.Type, "Video", StringComparison.OrdinalIgnoreCase)
                && Codecs.ContainerMatches(item.Container, p.Container)
                && (vCodec.Length == 0 || Accepts(p.VideoCodec, vCodec, video: true))
                && (aCodec.Length == 0 || Accepts(p.AudioCodec, aCodec, video: false)));
            if (match is not null) return new(true, null, $"direct play ({item.Container}/{Or(vCodec)}/{Or(aCodec)} supported)");
        }
        if (req.EnableTranscoding == false) return new(true, null, "transcoding disabled by client");

        var hlsProfiles = (profile.TranscodingProfiles ?? [])
            .Where(t => string.Equals(t.Type, "Video", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Protocol, "hls", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var allowVideoCopy = req.AllowVideoStreamCopy != false;
        var allowAudioCopy = req.AllowAudioStreamCopy != false;

        // Codecs we may encode to: H.264 always, HEVC/AV1 only when the settings allow them; each needs a working pipeline.
        string[] allowed = ["h264", .. allowHevc ? ["hevc"] : Array.Empty<string>(), .. allowAv1 ? ["av1"] : Array.Empty<string>()];
        string? Encodable(TranscodingProfileDto t) =>
            Ordered(t.VideoCodec).FirstOrDefault(c => allowed.Contains(c) && encodable.Contains(c));

        // Prefer the first HLS profile that can carry the source video untouched (remux), else the first we can encode for.
        var tp = (allowVideoCopy && vCodec.Length > 0 ? hlsProfiles.FirstOrDefault(t => Codecs.List(t.VideoCodec, true).Contains(vCodec)) : null)
                 ?? hlsProfiles.FirstOrDefault(t => Encodable(t) is not null)
                 ?? new TranscodingProfileDto { Container = "ts", VideoCodec = "h264", AudioCodec = "aac", MaxAudioChannels = "2" };

        var videoCodecs = Codecs.List(tp.VideoCodec, true);
        var copyVideo = allowVideoCopy && vCodec.Length > 0 && videoCodecs.Contains(vCodec);
        // The client's order wins: "hevc,h264" gives HEVC when it is allowed and encodable.
        var outVideo = copyVideo ? vCodec : Encodable(tp);
        if (outVideo is null)
            return new(true, null, $"{item.Container}/{Or(vCodec)}/{Or(aCodec)} not directly playable, but no video pipeline can encode {string.Join('/', allowed)}");

        var maxChannels = int.TryParse(tp.MaxAudioChannels, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mc) && mc > 0 ? mc : 2;
        var sourceChannels = audio?.Channels > 0 ? audio.Channels : 2;
        var audioCodecs = Codecs.List(tp.AudioCodec, false);
        var copyAudio = allowAudioCopy && aCodec.Length > 0 && audioCodecs.Contains(aCodec) && sourceChannels <= maxChannels;
        var outAudio = copyAudio ? aCodec : EncodableAudio.FirstOrDefault(audioCodecs.Contains) ?? "aac";
        var channels = copyAudio ? sourceChannels : Math.Min(Math.Min(sourceChannels, maxChannels), 6);

        var plan = new TranscodePlan(copyVideo, outVideo, copyAudio, outAudio, channels, audioIndex,
            tp.Container == "mp4" ? "mp4" : "ts", req.MaxStreamingBitrate);
        var reason = $"{item.Container}/{Or(vCodec)}/{Or(aCodec)} not directly playable -> HLS({plan.SegmentContainer}) " +
                     $"video {(copyVideo ? "copy" : "→ " + outVideo)}, audio {(copyAudio ? "copy" : $"→ {outAudio} {channels}ch")}";
        return new(false, plan, reason);
    }

    /// <summary>Maps a MediaStreams index (videos first, then audio) to the n-th audio track.</summary>
    private static int SelectedAudio(MediaInfo? mi, int? streamIndex)
    {
        if (mi is null || streamIndex is null) return 0;
        var rel = streamIndex.Value - mi.Video.Count;
        return rel >= 0 && rel < mi.Audio.Count ? rel : 0;
    }

    /// <summary>A profile's video codecs in the client's order of preference.</summary>
    private static IEnumerable<string> Ordered(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Codecs.NormalizeVideo);

    private static bool Accepts(string? csv, string codec, bool video)
    {
        var list = Codecs.List(csv, video);
        return list.Count == 0 || list.Contains(codec);
    }

    private static string Or(string s) => s.Length == 0 ? "?" : s;
}
