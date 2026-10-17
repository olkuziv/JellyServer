using System.Globalization;

namespace JellyServer.Transcoding.Software;

/// <summary>How a software pipeline turns HDR into SDR; decided by the probe.</summary>
internal enum ToneMapper { None, TonemapX, Zscale }

/// <summary>
/// The CPU video path shared by the software encoders (x264, x265, SVT-AV1), after Jellyfin's
/// EncodingHelper.GetSwVidFilterChain: deinterlace → scale → tone map, or the frames of a hardware
/// decoder as they are. Compiled into each software module as a linked source file.
/// </summary>
internal static class SoftwareVideo
{
    private const string Hdr10In = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";
    private const string HlgIn = "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc";

    /// <summary>jellyfin-ffmpeg's SIMD tonemapx like Jellyfin; zscale + tonemap with other builds.</summary>
    public static ToneMapper ProbeToneMapper(IFfmpegCapabilities ffmpeg) =>
        ffmpeg.HasFilter("tonemapx") ? ToneMapper.TonemapX
        : ffmpeg.HasFilter("zscale") && ffmpeg.HasFilter("tonemap") ? ToneMapper.Zscale
        : ToneMapper.None;

    /// <summary>Height of the encoded picture: the source capped at <see cref="VideoTarget.MaxHeight"/>.</summary>
    public static int OutputHeight(VideoJob job) =>
        job.Target.MaxHeight > 0 && job.Source.Height > job.Target.MaxHeight ? job.Target.MaxHeight : job.Source.Height;

    /// <summary>The <c>-vf</c> chain, or null when the encoder's <c>-pix_fmt yuv420p</c> is all the frames need.</summary>
    public static string? Filters(VideoJob job, ToneMapper toneMapper)
    {
        // Frames from a hardware decoder arrive in system memory already scaled, deinterlaced and tone-mapped.
        if (job.Decoded is { } decoded) return string.Join(',', [.. decoded.Filters, "format=yuv420p"]);

        var source = job.Source;
        var toneMap = source.IsHdr && toneMapper != ToneMapper.None;
        var filters = new List<string>();
        if (toneMap) filters.Add(source.HdrType == "HLG" ? HlgIn : Hdr10In);
        if (source.Interlaced) filters.Add(Deinterlace(source, job.Processing.Deinterlace));
        var height = OutputHeight(job);
        if (height != source.Height) filters.Add($"scale=-2:{height.ToString(CultureInfo.InvariantCulture)}");
        if (toneMap) filters.Add(toneMapper == ToneMapper.TonemapX ? TonemapX(job.Processing.ToneMap) : Zscale(job.Processing.ToneMap));
        return filters.Count > 0 ? string.Join(',', filters) : null;
    }

    /// <summary>Jellyfin's software deinterlacer: field rate only for sources of at most 30 fps.</summary>
    private static string Deinterlace(VideoSource source, Deinterlacing d)
    {
        var doubleRate = d.DoubleRate && source.FrameRate is > 0 and <= 30;
        return $"{(d.Method == "bwdif" ? "bwdif" : "yadif")}={(doubleRate ? 1 : 0)}:-1:0";
    }

    private static string TonemapX(ToneMapping t)
    {
        var s = $"tonemapx=tonemap={t.Algorithm}:desat={Num(t.Desat)}:peak={Num(t.Peak)}:t=bt709:m=bt709:p=bt709:format=yuv420p";
        if (t.Param != 0) s += $":param={Num(t.Param)}";
        if (t.Range is "tv" or "pc") s += $":range={t.Range}";
        return s;
    }

    /// <summary>Stock ffmpeg: linear light via zimg. Its tonemap filter has no bt2390, so that becomes hable.</summary>
    private static string Zscale(ToneMapping t)
    {
        var algorithm = t.Algorithm == "bt2390" ? "hable" : t.Algorithm;
        var param = t.Param != 0 ? $":param={Num(t.Param)}" : "";
        return $"zscale=t=linear:npl={Num(t.Peak > 0 ? t.Peak : 100)},format=gbrpf32le,zscale=p=bt709,"
            + $"tonemap={algorithm}:desat={Num(t.Desat)}{param},zscale=t=bt709:m=bt709:r={(t.Range == "pc" ? "pc" : "tv")}";
    }

    /// <summary>"auto" keeps the module's configured preset.</summary>
    public static string Preset(SoftwareEncoding s, string fallback) => s.Preset is "auto" or "" ? fallback : s.Preset;

    /// <summary>Jellyfin's EncodingThreadCount: -1 leaves ffmpeg's default, 0 is "-threads 0", n is capped at the core count.</summary>
    public static string[] Threads(int threads) => threads < 0 ? []
        : ["-threads", (threads == 0 ? 0 : Math.Min(threads, Environment.ProcessorCount)).ToString(CultureInfo.InvariantCulture)];

    /// <summary>Keyframes exactly on segment boundaries: segments match the playlist precisely.</summary>
    public static string[] Keyframes(SegmentTiming timing) =>
        ["-force_key_frames", $"expr:gte(t,n_forced*{timing.SegmentSeconds.ToString(CultureInfo.InvariantCulture)})"];

    /// <summary>Peak bitrate below the client's limit (audio and muxing need the rest); quality stays CRF.</summary>
    public static string[] MaxRate(VideoTarget target)
    {
        if (target.MaxBitrate is not (> 0 and var b)) return [];
        var max = Math.Min(b * 9 / 10, 40_000_000);
        return ["-maxrate", max.ToString(CultureInfo.InvariantCulture), "-bufsize", (max * 2).ToString(CultureInfo.InvariantCulture)];
    }

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}
