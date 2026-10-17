using System.Globalization;

namespace JellyServer.Transcoding.Vaapi;

/// <summary>How tonemap_opencl reaches VAAPI frames: zero-copy mapping (Intel) or download/upload (other OpenCL runtimes).</summary>
internal enum OpenClMode { None, Interop, Copy }

/// <summary>
/// What the probe proved about the device, published as one immutable object.
/// <paramref name="Normal"/>/<paramref name="LowPower"/>: codecs whose test encode passed in that encoder mode.
/// <paramref name="Decodes"/>: proven decode keys (codec names, plus <c>hevc10</c> for Main 10).
/// </summary>
internal sealed record VaapiState(string Device, IReadOnlySet<string> Normal, IReadOnlySet<string> LowPower,
    IReadOnlySet<string> Decodes, bool IntelIhd, bool Amd, bool VppToneMap, OpenClMode OpenCl)
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    public static readonly VaapiState None = new("", Empty, Empty, Empty, false, false, false, OpenClMode.None);

    /// <summary>The device opened and VPP (upload, scale, download) works on it.</summary>
    public bool Works => Device.Length > 0;

    public IReadOnlySet<string> Codecs { get; } = Normal.Union(LowPower).ToHashSet();
}

/// <summary>
/// The decode and filter half of a VAAPI job, shared by the pipeline (ending in VAAPI surfaces for the encoder)
/// and the decoder (ending in nv12 system memory). Mirrors Jellyfin's EncodingHelper VAAPI branch.
/// </summary>
internal static class VaapiChain
{
    public const string Va = "va";
    public const string Ocl = "ocl";

    public const string SdrParams = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709";

    public static readonly string[] HwAccel = ["-hwaccel", "vaapi", "-hwaccel_device", Va, "-hwaccel_output_format", "vaapi"];

    /// <summary>Input arguments and filters, or null when the job's tone-mapping request can't be met here.</summary>
    public static (List<string> Input, List<string> Filters)? Build(VaapiState s, VideoJob job, bool toMemory)
    {
        var src = job.Source;
        var p = job.Processing;
        var gpu = Decodes(s, src, p.HwDecode);
        if (toMemory && !gpu) return null;

        // VPP tone mapping needs VAAPI-decoded HDR10 (Jellyfin's IsIntelVppTonemapAvailable); OpenCL takes HLG too.
        var vpp = src.IsHdr && p.VppToneMap.Enabled && s.VppToneMap && gpu && IsHdr10(src);
        var ocl = src.IsHdr && !vpp && p.ToneMap.Enabled
            && (s.OpenCl == OpenClMode.Interop || (s.OpenCl == OpenClMode.Copy && toMemory));
        if (src.IsHdr && !vpp && !ocl && (p.ToneMap.Enabled || p.VppToneMap.Enabled)) return null;
        var copy = ocl && s.OpenCl == OpenClMode.Copy;

        var input = new List<string> { "-init_hw_device", $"vaapi={Va}:{s.Device}" };
        if (copy) input.AddRange(["-init_hw_device", $"opencl={Ocl}"]);
        input.AddRange(["-filter_hw_device", copy ? Ocl : Va]);
        if (gpu)
        {
            input.AddRange(HwAccel);
            if (src.Codec == "h264" && string.Equals(src.Profile, "baseline", StringComparison.OrdinalIgnoreCase))
                input.AddRange(["-hwaccel_flags", "+allow_profile_mismatch"]);
        }

        var height = job.Target.MaxHeight > 0 && src.Height > job.Target.MaxHeight ? job.Target.MaxHeight : 0;
        var doubleRate = p.Deinterlace.DoubleRate && (src.FrameRate > 0 ? src.FrameRate : 60) <= 30;
        var f = new List<string>();
        if (vpp || ocl) f.Add(HdrParams(src));
        else if (src.IsHdr) f.Add(SdrParams); // HDR passed through untouched: label it SDR, as Jellyfin does
        if (gpu)
        {
            if (src.Interlaced) f.Add($"deinterlace_vaapi=rate={(doubleRate ? "field" : "frame")}");
            // Tone mappers take the 10-bit surfaces; everything else becomes the 8-bit nv12 the output needs.
            if (Scale(height, vpp || ocl ? (IsRext(src) ? "p010" : null) : "nv12") is { } scale) f.Add(scale);
        }
        else
        {
            if (src.Interlaced) f.Add($"{(p.Deinterlace.Method == "bwdif" ? "bwdif" : "yadif")}={(doubleRate ? 1 : 0)}:-1:0");
            if (height > 0) f.Add($"scale=-2:{height}");
            f.AddRange([ocl ? "format=p010le" : "format=nv12", "hwupload"]);
        }

        if (vpp) f.Add(VppToneMap(p.VppToneMap));
        if (copy) f.AddRange(["hwdownload", "format=p010le", "hwupload", OpenClToneMap(p.ToneMap)]);
        else if (ocl)
        {
            // VAAPI-OpenCL interop: map the surfaces, tone-map, map the result back for the encoder.
            f.AddRange(["hwmap=derive_device=opencl:mode=read", OpenClToneMap(p.ToneMap)]);
            if (!toMemory) f.AddRange(["hwmap=derive_device=vaapi:mode=write:reverse=1", "format=vaapi"]);
        }
        if (toMemory) f.AddRange(["hwdownload", "format=nv12"]);
        return (input, f);
    }

    /// <summary>
    /// Jellyfin's VAAPI decode rules (GetVaapiVidDecoder/GetHwaccelType): 8-bit 4:2:0 H.264/MPEG-2/VC-1/VP8,
    /// 8/10-bit 4:2:0 VP9/AV1, HEVC up to 12-bit with Rext only on Intel iHD; each gated by the dashboard switches.
    /// </summary>
    public static bool Decodes(VaapiState s, VideoSource src, HardwareDecoding hw)
    {
        if (!s.Works || !hw.Allows(src.Codec)) return false;
        var profile = src.Profile ?? "";
        var not420 = profile.Contains("4:4") || profile.Contains("4:2:2");
        var depth = src.BitDepth;
        return src.Codec switch
        {
            "h264" => depth <= 8 && !not420 && !profile.Contains("10") && s.Decodes.Contains("h264"),
            "mpeg2video" or "vp8" => depth <= 8 && !not420 && s.Decodes.Contains(src.Codec),
            // No encoder makes a VC-1 sample: trusted when the GPU decodes at all, as Jellyfin trusts the checkbox.
            "vc1" => depth <= 8 && s.Decodes.Contains("h264"),
            "vp9" => depth <= 10 && !not420 && profile is not ("Profile 1" or "Profile 3")
                && (depth <= 8 || hw.Vp9_10Bit) && s.Decodes.Contains("vp9"),
            "av1" => depth <= 10 && profile is "" or "Main" && s.Decodes.Contains("av1"),
            "hevc" when IsRext(src) => s.IntelIhd && s.Decodes.Contains("hevc10")
                && (depth <= 10 ? hw.HevcRext10Bit : depth == 12 && hw.HevcRext12Bit),
            "hevc" => depth <= 8 ? s.Decodes.Contains("hevc") : depth == 10 && hw.Hevc10Bit && s.Decodes.Contains("hevc10"),
            _ => false,
        };
    }

    public static bool IsRext(VideoSource s) =>
        s.Codec == "hevc" && s.Profile is { } p && (p.Contains("Rext", StringComparison.OrdinalIgnoreCase) || p.Contains("4:"));

    private static bool IsHdr10(VideoSource s) => s.HdrType?.StartsWith("HDR10", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Jellyfin's GetInputHdrParam: tone mappers trust these over missing stream metadata.</summary>
    private static string HdrParams(VideoSource s) =>
        $"setparams=color_primaries=bt2020:color_trc={(s.HdrType == "HLG" ? "arib-std-b67" : "smpte2084")}:colorspace=bt2020nc";

    /// <summary>scale_vaapi with Jellyfin's extra VPP pool; null when neither size nor format changes.</summary>
    public static string? Scale(int height, string? format)
    {
        var args = new List<string>();
        if (height > 0) args.AddRange(["w=-2", $"h={Num(height)}"]);
        if (format is not null) args.Add($"format={format}");
        return args.Count == 0 ? null : $"scale_vaapi={string.Join(':', args)}:extra_hw_frames=24";
    }

    /// <summary>Jellyfin's GetHwTonemapFilter for vaapi: optional procamp brightness/contrast, then VPP tone mapping.</summary>
    public static string VppToneMap(VppToneMapping v)
    {
        var procamp = new List<string>();
        if (v.Brightness != 0 && v.Brightness is >= -100 and <= 100) procamp.Add($"b={Num(v.Brightness)}");
        if (v.Contrast is > 1 and <= 10) procamp.Add($"c={Num(v.Contrast)}");
        var tonemap = "tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32";
        return procamp.Count == 0 ? tonemap : $"procamp_vaapi={string.Join(':', procamp)},{tonemap}";
    }

    /// <summary>Jellyfin's GetHwTonemapFilter for opencl (jellyfin-ffmpeg 7 takes every tonemap_mode but auto).</summary>
    public static string OpenClToneMap(ToneMapping t)
    {
        var f = $"tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap={t.Algorithm}:peak={Num(t.Peak)}:desat={Num(t.Desat)}";
        if (t.Mode is "max" or "rgb" or "lum" or "itp") f += $":tonemap_mode={t.Mode}";
        if (t.Param != 0) f += $":param={Num(t.Param)}";
        if (t.Range is "tv" or "pc") f += $":range={t.Range}";
        return f;
    }

    public static string Num(double n) => n.ToString(CultureInfo.InvariantCulture);
}
