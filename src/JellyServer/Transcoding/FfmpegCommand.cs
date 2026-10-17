using System.Globalization;
using JellyServer.Configuration;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Transcoding;

/// <summary>
/// Assembles one ffmpeg HLS run. The video part comes from an <see cref="IVideoPipeline"/>;
/// input, stream mapping, audio, timestamps and the HLS muxer are the same for every pipeline.
/// </summary>
public static class FfmpegCommand
{
    public static VideoSource Source(Item item)
    {
        var v = item.MediaInfo?.Video.FirstOrDefault();
        return new VideoSource(Codecs.NormalizeVideo(v?.Codec ?? ""), v?.Width ?? 0, v?.Height ?? 0, v?.BitDepth ?? 0, v?.HdrType, v?.Profile,
            v?.FrameRate ?? 0, v?.Interlaced ?? false);
    }

    public static VideoTarget Target(TranscodePlan plan, int maxHeight = 1080) =>
        new(plan.CopyVideo, plan.VideoCodec, plan.SegmentContainer, plan.MaxBitrate, maxHeight);

    /// <param name="item">The source: its path is the input, its MediaInfo describes the audio tracks.</param>
    /// <param name="ffmpeg">The installed ffmpeg, to pick the AAC encoder like Jellyfin (aac_at, libfdk_aac, aac); null uses aac.</param>
    public static List<string> Build(Item item, TranscodePlan plan, VideoArgs video, TranscodingOptions options, int startSegment, string dir,
        IFfmpegCapabilities? ffmpeg = null)
    {
        var segmentSeconds = options.SegmentSeconds;
        var start = startSegment * segmentSeconds;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        if (start > 0) args.AddRange(["-ss", start.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(video.Input);
        args.AddRange(["-fflags", "+genpts", "-i", item.Path]);
        args.AddRange(["-map", "0:v:0", "-map", $"0:a:{plan.AudioIndex}?", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);
        args.AddRange(video.Output);

        args.AddRange(AudioArgs(item, plan, options.Audio, ffmpeg));

        // Keep source timestamps so every segment lines up with the playlist timeline after a restart.
        args.AddRange(["-copyts", "-avoid_negative_ts", "disabled", "-start_at_zero",
            "-max_muxing_queue_size", options.MaxMuxingQueueSize.ToString(CultureInfo.InvariantCulture), "-max_delay", "5000000"]);
        args.AddRange(["-f", "hls", "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_list_size", "0",
            "-hls_playlist_type", "event", "-start_number", startSegment.ToString(CultureInfo.InvariantCulture),
            "-hls_flags", "temp_file+independent_segments",
            "-hls_segment_filename", Path.Combine(dir, $"%d.{plan.SegmentExtension}")]);
        if (plan.SegmentContainer == "mp4") args.AddRange(["-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "-1.mp4"]);
        else args.AddRange(["-hls_segment_type", "mpegts"]);
        args.Add(Path.Combine(dir, "ffmpeg.m3u8"));
        return args;
    }

    /// <summary>
    /// Audio codec, channels, bitrate/VBR and downmix filter, as Jellyfin's DynamicHlsController.GetAudioArguments
    /// builds them from the dashboard's audio settings.
    /// </summary>
    public static List<string> AudioArgs(Item item, TranscodePlan plan, AudioOptions options, IFfmpegCapabilities? ffmpeg = null)
    {
        var sourceChannels = item.MediaInfo is { } mi && plan.AudioIndex < mi.Audio.Count ? mi.Audio[plan.AudioIndex].Channels : 0;
        string? encoder;
        int channels;
        if (!plan.CopyAudio)
        {
            encoder = Encoder(plan.AudioCodec, ffmpeg) ?? Encoder("aac", ffmpeg)!;
            channels = plan.AudioChannels;
        }
        // Jellyfin's HlsAudioSeekStrategy=TranscodeAudio: with re-encoded video, copied audio is re-encoded for the whole
        // job (same codec), so after a seek restart it starts exactly at -ss instead of at the copied packet nearest to it.
        else if (plan.CopyVideo || !options.SeekStrategy.Equals("TranscodeAudio", StringComparison.OrdinalIgnoreCase)
                 || Encoder(plan.AudioCodec, ffmpeg) is not { } same)
            return ["-c:a", "copy"];
        else
        {
            encoder = same;
            channels = plan.AudioChannels > 0 ? plan.AudioChannels : sourceChannels > 0 ? sourceChannels : 2;
        }
        channels = Math.Min(channels, EncoderChannelLimit(encoder));

        var args = new List<string> { "-c:a", encoder };
        var layout = InferChannelLayout(sourceChannels);
        var algorithm = DownMixFilters.GetValueOrDefault((options.StereoDownmix.ToLowerInvariant(), layout));
        // A downmix algorithm's pan filter sets the stereo layout itself.
        if (channels != 2 || sourceChannels == 0 || algorithm is null)
            args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);

        var bitrate = Math.Clamp(channels * 96, 128, 640);
        if (options.Vbr && VbrArgs(encoder, bitrate * 1000, channels) is { } vbr) args.AddRange(vbr);
        else args.AddRange(["-b:a", $"{bitrate}k"]);

        if (channels == 2 && sourceChannels > 2)
        {
            List<string> filters = [];
            if (algorithm is not null) filters.Add(algorithm);
            if (!options.DownMixBoost.Equals(1)) filters.Add("volume=" + options.DownMixBoost.ToString(CultureInfo.InvariantCulture));
            if (filters.Count > 0) args.AddRange(["-af", string.Join(',', filters)]);
        }
        return args;
    }

    /// <summary>Jellyfin's GetAudioEncoder for the codecs a segment can carry; null when there is no encoder for it.</summary>
    public static string? Encoder(string codec, IFfmpegCapabilities? ffmpeg) => codec.ToLowerInvariant() switch
    {
        "aac" when ffmpeg?.HasEncoder("aac_at") == true => "aac_at",
        "aac" when ffmpeg?.HasEncoder("libfdk_aac") == true => "libfdk_aac",
        "aac" => "aac",
        "mp3" => "libmp3lame",
        "ac3" => "ac3",
        "eac3" => "eac3",
        _ => null,
    };

    /// <summary>Jellyfin's _audioTranscodeChannelLookup; other encoders take at least 8 channels.</summary>
    private static int EncoderChannelLimit(string encoder) => encoder switch
    {
        "libmp3lame" => 2,
        "libfdk_aac" or "ac3" or "eac3" => 6,
        _ => 8,
    };

    /// <summary>Jellyfin's GetAudioVbrModeParam; null when the encoder has no VBR mode (CBR -b:a is used).</summary>
    public static string[]? VbrArgs(string encoder, int bitrate, int channels)
    {
        var perChannel = bitrate / Math.Max(channels, 1);
        var bps = bitrate.ToString(CultureInfo.InvariantCulture);
        return encoder switch
        {
            "libfdk_aac" => ["-vbr:a", perChannel switch { < 32000 => "1", < 48000 => "2", < 64000 => "3", < 96000 => "4", _ => "5" }],
            // lame's VBR is only good for a certain bitrate range; outside it use ABR.
            "libmp3lame" when perChannel is < 122500 and > 48000 =>
                ["-qscale:a", perChannel switch { < 64000 => "6", < 88000 => "4", < 112000 => "2", _ => "0" }],
            "libmp3lame" => ["-abr:a", "1", "-b:a", bps],
            "aac_at" => ["-aac_at_mode:a", "2", "-b:a", bps],
            "libvorbis" => ["-qscale:a", perChannel switch { < 40000 => "0", < 56000 => "2", < 80000 => "4", < 112000 => "6", _ => "8" }],
            _ => null,
        };
    }

    /// <summary>Jellyfin's DownMixAlgorithmsHelper.InferChannelLayout from the channel count.</summary>
    public static string InferChannelLayout(int channels) => channels switch
    {
        1 => "mono", 2 => "stereo", 3 => "2.1", 4 => "4.0", 5 => "5.0", 6 => "5.1", 7 => "6.1", 8 => "7.1", _ => "",
    };

    private const string Side71To51 = "pan=5.1(side)|c0=c0|c1=c1|c2=c2|c3=c3|c4=0.707*c4+0.707*c6|c5=0.707*c5+0.707*c7,";

    /// <summary>Jellyfin's DownMixAlgorithmsHelper.AlgorithmFilterStrings, keyed by (lower-case algorithm, layout).</summary>
    private static readonly Dictionary<(string, string), string> DownMixFilters = new()
    {
        [("dave750", "5.1")] = "pan=stereo|c0=0.5*c2+0.707*c0+0.707*c4+0.5*c3|c1=0.5*c2+0.707*c1+0.707*c5+0.5*c3",
        // 7.1 goes to 5.1 first with the AC-4 coefficients.
        [("dave750", "7.1")] = Side71To51 + "pan=stereo|c0=0.5*c2+0.707*c0+0.707*c4+0.5*c3|c1=0.5*c2+0.707*c1+0.707*c5+0.5*c3",
        [("nightmodedialogue", "5.1")] = "pan=stereo|c0=c2+0.30*c0+0.30*c4|c1=c2+0.30*c1+0.30*c5",
        [("nightmodedialogue", "7.1")] = Side71To51 + "pan=stereo|c0=c2+0.30*c0+0.30*c4|c1=c2+0.30*c1+0.30*c5",
        [("rfc7845", "3.0")] = "pan=stereo|c0=0.414214*c2+0.585786*c0|c1=0.414214*c2+0.585786*c1",
        [("rfc7845", "quad")] = "pan=stereo|c0=0.422650*c0+0.366025*c2+0.211325*c3|c1=0.422650*c1+0.366025*c3+0.211325*c2",
        [("rfc7845", "5.0")] = "pan=stereo|c0=0.460186*c2+0.650802*c0+0.563611*c3+0.325401*c4|c1=0.460186*c2+0.650802*c1+0.563611*c4+0.325401*c3",
        [("rfc7845", "5.1")] = "pan=stereo|c0=0.374107*c2+0.529067*c0+0.458186*c4+0.264534*c5+0.374107*c3|c1=0.374107*c2+0.529067*c1+0.458186*c5+0.264534*c4+0.374107*c3",
        [("rfc7845", "6.1")] = "pan=stereo|c0=0.321953*c2+0.455310*c0+0.394310*c5+0.227655*c6+0.278819*c4+0.321953*c3|c1=0.321953*c2+0.455310*c1+0.394310*c6+0.227655*c5+0.278819*c4+0.321953*c3",
        [("rfc7845", "7.1")] = "pan=stereo|c0=0.274804*c2+0.388631*c0+0.336565*c6+0.194316*c7+0.336565*c4+0.194316*c5+0.274804*c3|c1=0.274804*c2+0.388631*c1+0.336565*c7+0.194316*c6+0.336565*c5+0.194316*c4+0.274804*c3",
        [("ac4", "3.0")] = "pan=stereo|c0=c0+0.707*c2|c1=c1+0.707*c2",
        [("ac4", "5.0")] = "pan=stereo|c0=c0+0.707*c2+0.707*c3|c1=c1+0.707*c2+0.707*c4",
        [("ac4", "5.1")] = "pan=stereo|c0=c0+0.707*c2+0.707*c4|c1=c1+0.707*c2+0.707*c5",
        [("ac4", "7.0")] = "pan=5.0(side)|c0=c0|c1=c1|c2=c2|c3=0.707*c3+0.707*c5|c4=0.707*c4+0.707*c6,pan=stereo|c0=c0+0.707*c2+0.707*c3|c1=c1+0.707*c2+0.707*c4",
        [("ac4", "7.1")] = Side71To51 + "pan=stereo|c0=c0+0.707*c2+0.707*c4|c1=c1+0.707*c2+0.707*c5",
    };
}
