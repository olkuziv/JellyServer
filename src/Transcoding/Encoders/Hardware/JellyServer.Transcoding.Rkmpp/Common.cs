using System.Globalization;

namespace JellyServer.Transcoding.Rkmpp;

/// <summary>
/// Filter and argument helpers that mirror Jellyfin's EncodingHelper (software chain, keyframes, tone-mapping
/// strings). Every hardware module keeps its own copy so modules stay independent of each other.
/// </summary>
internal static class Common
{
    public const string SdrParams = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709";

    public static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Output height when the source is taller than <see cref="VideoTarget.MaxHeight"/>, otherwise 0 (keep the size).</summary>
    public static int ScaleHeight(VideoJob job) =>
        job.Target.MaxHeight > 0 && job.Source.Height > job.Target.MaxHeight ? job.Target.MaxHeight : 0;

    /// <summary>Width for <paramref name="height"/> with the source aspect kept, rounded to a multiple of <paramref name="align"/>.</summary>
    public static int ScaleWidth(VideoSource s, int height, int align = 2) =>
        s.Height <= 0 ? align : Math.Max(align, (int)Math.Round((double)s.Width * height / s.Height / align) * align);

    /// <summary>Jellyfin only doubles the rate for sources of 30 fps or less (GetSwDeinterlaceFilter / GetHwDeinterlaceFilter).</summary>
    public static bool DoubleRate(VideoJob job) => job.Processing.Deinterlace.DoubleRate && job.Source.FrameRate is > 0 and <= 30;

    public static bool Bwdif(VideoJob job) => string.Equals(job.Processing.Deinterlace.Method, "bwdif", StringComparison.OrdinalIgnoreCase);

    /// <summary>yadif=0:-1:0 / bwdif=1:-1:0, as Jellyfin's GetSwDeinterlaceFilter.</summary>
    public static string SwDeinterlace(VideoJob job, string suffix = "", bool bwdifAvailable = true) =>
        $"{(Bwdif(job) && bwdifAvailable ? "bwdif" : "yadif")}{suffix}={(DoubleRate(job) ? 1 : 0)}:-1:0";

    /// <summary>Frame rate after deinterlacing, used for the GOP length; 0 when unknown.</summary>
    public static double OutputFps(VideoJob job) =>
        job.Source.Interlaced && DoubleRate(job) ? job.Source.FrameRate * 2 : job.Source.FrameRate;

    /// <summary>Only 10-bit (or deeper) HDR sources are tone-mapped (Jellyfin's IsHwTonemapAvailable / IsSwTonemapAvailable).</summary>
    public static bool IsToneMappable(VideoSource s) => s.IsHdr && s.BitDepth >= 10;

    /// <summary>Jellyfin's GetInputHdrParam: tag the input so the tone mapper knows the transfer.</summary>
    public static string HdrParams(VideoSource s) => string.Equals(s.HdrType, "HLG", StringComparison.OrdinalIgnoreCase)
        ? "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc"
        : "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";

    /// <summary>
    /// Jellyfin's GetSwVidFilterChain (used by every hardware type whose GPU filters are unavailable):
    /// colour tags, CPU deinterlace, CPU scale, CPU tone mapping (always, as for software encoders) or a format conversion.
    /// </summary>
    /// <param name="widthAlign">64 for V4L2 (Jellyfin's GetSwScaleFilter scaleVal), otherwise 2.</param>
    public static List<string> CpuChain(VideoJob job, string outFormat, CpuToneMap toneMap, int widthAlign = 2)
    {
        var s = job.Source;
        var doToneMap = IsToneMappable(s) && toneMap != CpuToneMap.None;
        var f = new List<string> { doToneMap ? HdrParams(s) : SdrParams };
        if (s.Interlaced) f.Add(SwDeinterlace(job));
        if (ScaleHeight(job) is > 0 and var h) f.Add($"scale=trunc(oh*a/{widthAlign})*{widthAlign}:{h}");
        if (!doToneMap) f.Add("format=" + outFormat);
        else if (toneMap == CpuToneMap.Tonemapx) f.Add(Tonemapx(job.Processing.ToneMap, outFormat));
        else f.Add(ZscaleToneMap(job.Processing.ToneMap, outFormat));
        return f;
    }

    /// <summary>Which CPU tone mapper the installed ffmpeg has: jellyfin-ffmpeg's tonemapx, else zscale+tonemap.</summary>
    public static CpuToneMap DetectCpuToneMap(IFfmpegCapabilities ffmpeg) =>
        ffmpeg.HasFilter("tonemapx") ? CpuToneMap.Tonemapx
        : ffmpeg.HasFilter("zscale") && ffmpeg.HasFilter("tonemap") ? CpuToneMap.Zscale
        : CpuToneMap.None;

    /// <summary>Jellyfin's tonemapx string (GetSwVidFilterChain).</summary>
    public static string Tonemapx(ToneMapping t, string format)
    {
        var f = $"tonemapx=tonemap={t.Algorithm}:desat={Num(t.Desat)}:peak={Num(t.Peak)}:t=bt709:m=bt709:p=bt709:format={format}";
        if (t.Param != 0) f += ":param=" + Num(t.Param);
        if (t.Range is "tv" or "pc") f += ":range=" + t.Range;
        return f;
    }

    /// <summary>Stock-ffmpeg fallback: zscale linearises, tonemap (no bt2390 there: hable instead) maps, zscale returns to BT.709.</summary>
    public static string ZscaleToneMap(ToneMapping t, string format)
    {
        var algorithm = t.Algorithm is "bt2390" ? "hable" : t.Algorithm;
        var tonemap = $"tonemap={algorithm}:desat={Num(t.Desat)}";
        if (t.Param != 0) tonemap += ":param=" + Num(t.Param);
        return $"zscale=t=linear:npl={Num(t.Peak)},format=gbrpf32le,zscale=p=bt709,{tonemap},zscale=t=bt709:m=bt709:r={(t.Range == "pc" ? "pc" : "tv")},format={format}";
    }

    /// <summary>Jellyfin's GetHwTonemapFilter for OpenCL/CUDA/VideoToolbox (tonemap_{suffix}).</summary>
    public static string HwToneMap(ToneMapping t, string suffix, string format = "nv12")
    {
        var f = $"tonemap_{suffix}=format={format}:p=bt709:t=bt709:m=bt709:tonemap={t.Algorithm}:peak={Num(t.Peak)}:desat={Num(t.Desat)}";
        if (t.Mode is "max" or "rgb" or "lum" or "itp") f += ":tonemap_mode=" + t.Mode;
        if (t.Param != 0) f += ":param=" + Num(t.Param);
        if (t.Range is "tv" or "pc") f += ":range=" + t.Range;
        return f;
    }

    /// <summary>
    /// Keyframes exactly on segment boundaries: forced keyframes (the encoder turns them into IDR pictures) plus a fixed
    /// GOP of one segment when the frame rate is known, so no scene-cut keyframe shifts the next forced one
    /// (Jellyfin's GetHlsVideoKeyFrameArguments).
    /// </summary>
    public static List<string> KeyFrames(VideoJob job) => KeyFrames(job, OutputFps(job));

    /// <param name="fps">Output frame rate (0 when unknown: forced keyframes only).</param>
    public static List<string> KeyFrames(VideoJob job, double fps)
    {
        var seg = job.Timing.SegmentSeconds;
        var args = new List<string> { "-force_key_frames:0", $"expr:gte(t,n_forced*{seg})" };
        if (fps > 0)
        {
            var gop = Num((long)Math.Ceiling(seg * fps));
            args.AddRange(["-g:v:0", gop, "-keyint_min:v:0", gop]);
        }
        return args;
    }

    /// <summary>The client cap minus room for audio and muxing overhead (same rule as the other pipelines).</summary>
    public static long? CappedBitrate(VideoTarget target) => target.MaxBitrate is > 0 and var b ? Math.Min(b * 9 / 10, 40_000_000) : null;

    /// <summary>Apple players need the hvc1 tag for HEVC in fMP4.</summary>
    public static IEnumerable<string> Hvc1(VideoTarget target) =>
        target.Codec == "hevc" && target.SegmentContainer == "mp4" ? ["-tag:v", "hvc1"] : [];

    public static bool HasProfile(VideoSource s, params string[] parts) =>
        s.Profile is { } p && parts.Any(x => p.Contains(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>HEVC range extensions (4:2:2/4:4:4/12-bit), as Jellyfin's IsVideoStreamHevcRext.</summary>
    public static bool IsHevcRext(VideoSource s) => s.Codec == "hevc" && (HasProfile(s, "Rext", "4:2:2", "4:4:4") || s.BitDepth > 10);

    /// <summary>
    /// Jellyfin's dashboard gates for GPU decoding (GetHwaccelType / GetHwDecoderName): the codec list,
    /// 10-bit HEVC/VP9 switches and the HEVC RExt switches.
    /// </summary>
    public static bool DashboardAllows(VideoSource s, HardwareDecoding d)
    {
        if (!d.Allows(s.Codec)) return false;
        if (s.Codec == "hevc")
        {
            if (IsHevcRext(s)) return s.BitDepth <= 10 ? d.HevcRext10Bit : s.BitDepth == 12 && d.HevcRext12Bit;
            if (s.BitDepth == 10 && !d.Hevc10Bit) return false;
        }
        return !(s.Codec == "vp9" && s.BitDepth == 10 && !d.Vp9_10Bit);
    }

    /// <summary>A one-frame test encode: <paramref name="input"/> before -i, <paramref name="output"/> before the null muxer.</summary>
    public static List<string> TestRun(IEnumerable<string> input, IEnumerable<string> output) =>
    [
        "-hide_banner", "-loglevel", "error", .. input, "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25", "-frames:v", "1",
        .. output, "-f", "null", "-",
    ];

    /// <summary>
    /// Writes a two-frame sample of <paramref name="codec"/> (10-bit HDR10 when <paramref name="hdr"/>) with a software encoder,
    /// runs <paramref name="test"/> on it and deletes it. False when no software encoder can make the sample.
    /// </summary>
    public static async Task<bool> WithSampleAsync(IFfmpegCapabilities ffmpeg, string codec, bool hdr, Func<string, Task<bool>> test, CancellationToken ct)
    {
        var (encoder, extra) = codec switch
        {
            "h264" => ("libx264", Array.Empty<string>()),
            "hevc" => ("libx265", hdr
                ? ["-x265-params", "log-level=error:hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"
                    + ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400"]
                : ["-x265-params", "log-level=error"]),
            "mpeg2video" => ("mpeg2video", []),
            "mpeg4" => ("mpeg4", []),
            "vp8" => ("libvpx", []),
            "vp9" => ("libvpx-vp9", []),
            _ => ("", []),
        };
        if (encoder.Length == 0 || !ffmpeg.HasEncoder(encoder)) return false;
        var sample = Path.Combine(Path.GetTempPath(), $"jellyserver-probe-{Guid.NewGuid():N}.mkv");
        try
        {
            string[] make =
            [
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25", "-frames:v", "2",
                "-pix_fmt", hdr ? "yuv420p10le" : "yuv420p", "-c:v", encoder, .. extra, "-y", sample,
            ];
            return await ffmpeg.TryRunAsync(make, ct) && await test(sample);
        }
        finally
        {
            try { File.Delete(sample); } catch (IOException) { }
        }
    }
}

internal enum CpuToneMap { None, Tonemapx, Zscale }
