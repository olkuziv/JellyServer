using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.Qsv;

/// <summary>
/// Intel Quick Sync (h264_qsv / hevc_qsv / av1_qsv) and the matching hardware decode stage for software encoders,
/// following Jellyfin's QSV-on-VA-API filter chains. Sources decode on the GPU either through the native VA-API
/// hwaccel mapped to QSV (PreferNativeDecoder) or through the <c>*_qsv</c> decoders; frames then stay on the GPU
/// through deinterlacing, scaling and tone mapping (VPP, or OpenCL via interop). Other sources decode on the CPU.
/// Always produces 8-bit SDR 4:2:0 (unless HDR passes through untouched because tone mapping is off).
/// </summary>
internal sealed class QsvPipeline(IOptionsMonitor<QsvOptions> options, ILogger<QsvPipeline> log) : IVideoPipeline, IVideoDecoder
{
    private static readonly Dictionary<string, string> Encoders = new() { ["h264"] = "h264_qsv", ["hevc"] = "hevc_qsv", ["av1"] = "av1_qsv" };

    private static readonly Dictionary<string, string> Decoders = new()
    {
        ["h264"] = "h264_qsv", ["hevc"] = "hevc_qsv", ["mpeg2video"] = "mpeg2_qsv", ["vc1"] = "vc1_qsv",
        ["vp8"] = "vp8_qsv", ["vp9"] = "vp9_qsv", ["av1"] = "av1_qsv",
    };

    /// <summary>Presets h264/hevc/av1_qsv accept (Jellyfin's valid_presets); anything else becomes veryfast.</summary>
    private static readonly string[] Presets = ["veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast"];

    private const string HdrParams = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";
    private const string HlgParams = "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc";
    private const string SdrParams = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709";
    private const string TestSource = "testsrc2=size=1280x720:rate=25";

    private enum Decode { Software, Qsv, Vaapi }

    private enum Tone { None, VppQsv, VppVaapi, Ocl }

    /// <summary>What the last probe proved, published as one reference.</summary>
    /// <param name="Device">Render node the probe ran on; the jobs use the same one.</param>
    /// <param name="Works">The device opens and VPP + download work (enough for the decoder).</param>
    /// <param name="LowPowerOnly">Encoders whose test encode only succeeded with <c>-low_power 1</c>.</param>
    /// <param name="QsvDecoders">Source codecs with a <c>*_qsv</c> decoder in this ffmpeg.</param>
    /// <param name="Vaapi">Native VA-API decoding mapped to QSV works.</param>
    private sealed record State(string Device, bool Works, IReadOnlySet<string> Codecs, IReadOnlySet<string> LowPowerOnly,
        IReadOnlySet<string> QsvDecoders, bool Vaapi, bool VppToneMap, bool VaapiToneMap, bool Ocl,
        IFfmpegCapabilities? ProbedWith = null, DateTime ProbedAt = default)
    {
        public static readonly State None = new("", false, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), false, false, false, false);
    }

    private sealed record Plan(Decode Decode, Tone Tone, (int W, int H)? Size, bool Deinterlace, bool DoubleRate, bool Rext);

    private volatile State _state = State.None;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    public string Id => "qsv";
    public IReadOnlySet<string> OutputCodecs => _state.Codecs;
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => false;

    private static string DeviceOf(QsvOptions o) => string.IsNullOrWhiteSpace(o.Device) ? QsvOptions.DefaultDevice : o.Device.Trim();

    // ---------------------------------------------------------------- probe

    async Task<bool> IVideoPipeline.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) =>
        (await ProbeStateAsync(ffmpeg, ct)).Codecs.Count > 0;

    async Task<bool> IVideoDecoder.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeStateAsync(ffmpeg, ct)).Works;

    /// <summary>
    /// Pipeline and decoder are one instance and are probed back to back: a probe just made on the same device with
    /// the same ffmpeg is reused instead of running every test again.
    /// </summary>
    private async Task<State> ProbeStateAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        await _probeGate.WaitAsync(ct);
        try
        {
            var device = DeviceOf(options.CurrentValue);
            var last = _state;
            if (ReferenceEquals(last.ProbedWith, ffmpeg) && last.Device == device && DateTime.UtcNow - last.ProbedAt < TimeSpan.FromSeconds(10))
                return last;
            var state = await RunProbesAsync(ffmpeg, device, ct) with { ProbedWith = ffmpeg, ProbedAt = DateTime.UtcNow };
            _state = state;
            return state;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<State> RunProbesAsync(IFfmpegCapabilities ffmpeg, string device, CancellationToken ct)
    {
        var none = State.None with { Device = device };
        if (!ffmpeg.HasHwAccel("qsv") || !ffmpeg.HasFilter("vpp_qsv") || !ffmpeg.HasFilter("hwupload") || !ffmpeg.HasFilter("hwdownload"))
            return none;

        // Opens the device and runs VPP once; without a working GPU this fails immediately and nothing else is tried.
        if (!await ffmpeg.TryRunAsync(TestRun(device, false,
                "format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=w=640:h=360:format=nv12,hwdownload,format=nv12"), ct))
        {
            log.LogWarning("Quick Sync device {Device} does not work (VPP test failed)", device);
            return none;
        }

        // A listed encoder proves nothing about the GPU: encode one real frame. Some GPUs only encode in low-power mode.
        var codecs = new HashSet<string>();
        var lowPowerOnly = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
        {
            if (!ffmpeg.HasEncoder(encoder)) continue;
            if (await ffmpeg.TryRunAsync(TestEncode(device, codec, false), ct)) codecs.Add(codec);
            else if (await ffmpeg.TryRunAsync(TestEncode(device, codec, true), ct)) { codecs.Add(codec); lowPowerOnly.Add(codec); }
            else log.LogWarning("{Encoder} test encode on {Device} failed; Quick Sync will not encode {Codec}", encoder, device, codec);
        }

        var qsvDecoders = Decoders.Where(d => ffmpeg.HasDecoder(d.Value)).Select(d => d.Key).ToHashSet();

        // Tone mappers, each confirmed with a 10-bit HDR frame through the exact filters jobs use.
        var vppToneMap = await ffmpeg.TryRunAsync(TestRun(device, false,
            $"format=p010le,hwupload=extra_hw_frames=64,{HdrParams},vpp_qsv=format=p010:brightness=16:contrast=1.5:procamp=1:async_depth=2," +
            "vpp_qsv=tonemap=1:format=nv12:async_depth=2,hwdownload,format=nv12"), ct);

        var vaapi = ffmpeg.HasHwAccel("vaapi") && ffmpeg.HasFilter("scale_vaapi") && ffmpeg.HasFilter("hwmap")
            && await ffmpeg.TryRunAsync(TestRun(device, false,
                "format=nv12,hwupload=derive_device=vaapi,scale_vaapi=w=640:h=360:format=nv12:extra_hw_frames=24," +
                "hwmap=derive_device=qsv,format=qsv,hwdownload,format=nv12"), ct);

        var vaapiToneMap = vaapi && ffmpeg.HasFilter("procamp_vaapi") && ffmpeg.HasFilter("tonemap_vaapi")
            && await ffmpeg.TryRunAsync(TestRun(device, false,
                $"format=p010le,hwupload=derive_device=vaapi,{HdrParams},procamp_vaapi=b=16:c=1.5," +
                "tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32,hwmap=mode=read,format=nv12"), ct);

        var ocl = ffmpeg.HasHwAccel("opencl") && ffmpeg.HasFilter("tonemap_opencl") && ffmpeg.HasFilter("hwmap")
            && await ffmpeg.TryRunAsync(TestRun(device, true,
                $"format=p010le,hwupload=extra_hw_frames=64,{HdrParams},hwmap=derive_device=opencl:mode=read," +
                OclToneMap(new ToneMapping()) + ",hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16,format=qsv,hwdownload,format=nv12"), ct);

        log.LogInformation("Quick Sync on {Device}: encodes {Codecs}{LowPower}; native VA-API decoding {Vaapi}; " +
            "tone mapping: vpp_qsv {Vpp}, tonemap_vaapi {VaapiTm}, OpenCL {Ocl}", device, string.Join(", ", codecs),
            lowPowerOnly.Count > 0 ? $" (low power only: {string.Join(", ", lowPowerOnly)})" : "", vaapi, vppToneMap, vaapiToneMap, ocl);
        return new State(device, true, codecs, lowPowerOnly, qsvDecoders, vaapi, vppToneMap, vaapiToneMap, ocl);
    }

    private static List<string> TestRun(string device, bool opencl, string filters) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", .. DeviceArgs(device, opencl, "qs"),
        "-f", "lavfi", "-i", TestSource, "-frames:v", "1", "-vf", filters, "-f", "null", "-",
    ];

    private static List<string> TestEncode(string device, string codec, bool lowPower) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", .. DeviceArgs(device, false, "qs"),
        "-f", "lavfi", "-i", TestSource, "-frames:v", "1", "-vf", "format=nv12,hwupload=extra_hw_frames=64",
        "-c:v", Encoders[codec], "-preset", "veryfast", .. lowPower ? ["-low_power", "1"] : Array.Empty<string>(),
        "-global_quality", "23", "-f", "null", "-",
    ];

    /// <summary>
    /// On Linux QSV sits on top of VA-API: open the render node with the iHD driver (the only one oneVPL supports)
    /// and derive QSV (and OpenCL, for tone mapping) from it, as Jellyfin does.
    /// </summary>
    private static string[] DeviceArgs(string device, bool opencl, string filterDevice) =>
    [
        "-init_hw_device", $"vaapi=va:{device},driver=iHD", "-init_hw_device", "qsv=qs@va",
        .. opencl ? ["-init_hw_device", "opencl=ocl@va"] : Array.Empty<string>(), "-filter_hw_device", filterDevice,
    ];

    // ---------------------------------------------------------------- planning

    public bool CanHandle(VideoJob job)
    {
        var s = _state;
        var t = job.Target;
        return !t.Copy && s.Codecs.Contains(t.Codec)
            && (t.Codec != "av1" || t.SegmentContainer == "mp4") // AV1 in HLS only in fMP4
            && PlanFor(job, s) is not null;
    }

    public bool CanDecode(VideoJob job) => _state is { Works: true } s && PlanFor(job, s) is { Decode: not Decode.Software };

    /// <summary>
    /// Decoder and tone mapper for a job, or null when tone mapping is wanted but impossible here (another pipeline,
    /// e.g. x264 which always tone-maps, takes the job then).
    /// </summary>
    private static Plan? PlanFor(VideoJob job, State s)
    {
        var src = job.Source;
        var p = job.Processing;
        var decode = DecoderFor(src, p.HwDecode, s);
        var rext = IsHevcRext(src);

        var tone = Tone.None;
        // Like Jellyfin: only 10-bit+ HDR is tone-mapped. VPP tone mapping needs HDR10 static metadata and GPU frames.
        if (src.IsHdr && src.BitDepth >= 10)
        {
            if (p.VppToneMap.Enabled && src.HdrType == "HDR10" && decode == Decode.Qsv && s.VppToneMap) tone = Tone.VppQsv;
            else if (p.VppToneMap.Enabled && src.HdrType == "HDR10" && decode == Decode.Vaapi && s.VaapiToneMap) tone = Tone.VppVaapi;
            else if (p.ToneMap.Enabled && s.Ocl) tone = Tone.Ocl;
            else if (p.ToneMap.Enabled || p.VppToneMap.Enabled) return null;
        }

        var maxH = job.Target.MaxHeight / 2 * 2;
        (int, int)? size = maxH > 0 && src.Height > maxH && src.Width > 0
            ? (Math.Max(2, (int)Math.Round(src.Width * (double)maxH / src.Height / 2) * 2), maxH)
            : null;
        // Jellyfin doubles the rate only for sources up to 30 fps; an unknown rate counts as 60 for hardware.
        var doubleRate = p.Deinterlace.DoubleRate && src.FrameRate is > 0 and <= 30;
        return new Plan(decode, tone, size, src.Interlaced, doubleRate, rext);
    }

    /// <summary>Jellyfin's GetQsvHwVidDecoder/GetHwaccelType gating, then native VA-API or the *_qsv decoder.</summary>
    private static Decode DecoderFor(VideoSource src, HardwareDecoding hw, State s)
    {
        if (!Decoders.ContainsKey(src.Codec) || !hw.Allows(src.Codec)) return Decode.Software;
        var profile = src.Profile ?? "";
        switch (src.Codec)
        {
            case "hevc" when IsHevcRext(src):
                if (src.BitDepth <= 10 ? !hw.HevcRext10Bit : src.BitDepth != 12 || !hw.HevcRext12Bit) return Decode.Software;
                break;
            case "hevc":
                if (src.BitDepth > 10 || (src.BitDepth == 10 && !hw.Hevc10Bit)) return Decode.Software;
                break;
            case "vp9":
                if (src.BitDepth > 10 || (src.BitDepth == 10 && !hw.Vp9_10Bit) || NotYuv420(profile)) return Decode.Software;
                break;
            case "av1":
                if (src.BitDepth > 10 || !(profile.Length == 0 || profile.Equals("Main", StringComparison.OrdinalIgnoreCase))) return Decode.Software;
                break;
            default: // h264, mpeg2video, vc1, vp8: 8-bit 4:2:0 only
                if (src.BitDepth > 8 || NotYuv420(profile)) return Decode.Software;
                break;
        }

        if (hw.PreferNativeDecoder && s.Vaapi) return Decode.Vaapi;
        return s.QsvDecoders.Contains(src.Codec) ? Decode.Qsv : Decode.Software;
    }

    private static readonly string[] NotYuv420Profiles = ["4:2:2", "4:4:4", "Profile 1", "Profile 3", "Professional"];

    private static bool NotYuv420(string profile) => NotYuv420Profiles.Any(x => profile.Contains(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>HEVC range extensions: 4:2:2/4:4:4 (ffprobe profile "Rext") or 12-bit.</summary>
    private static bool IsHevcRext(VideoSource s) =>
        s.Codec == "hevc" && ((s.Profile ?? "").Contains("Rext", StringComparison.OrdinalIgnoreCase) || s.BitDepth == 12);

    // ---------------------------------------------------------------- building

    public VideoArgs Build(VideoJob job)
    {
        var s = _state;
        var plan = PlanFor(job, s) ?? throw new InvalidOperationException("Quick Sync cannot handle this job");
        var (input, filters) = DecodeAndProcess(job, plan, s, toQsvEncoder: true);
        var target = job.Target;
        var output = new List<string>();
        if (filters.Count > 0) output.AddRange(["-vf", string.Join(',', filters)]);
        output.AddRange(EncoderArgs(target.Codec, job.Processing, s));
        if (target.MaxBitrate is > 0 and var b)
        {
            // Plain VBR under a cap: every QSV generation supports it. (global_quality together with -maxrate would
            // select QVBR, which older GPUs lack.)
            var max = Math.Min(b * 9 / 10, 40_000_000);
            output.AddRange(["-b:v", Str(max * 3 / 4), "-maxrate", Str(max), "-bufsize", Str(max * 2)]);
        }
        else output.AddRange(["-global_quality", Str(options.CurrentValue.GlobalQuality)]); // ICQ, like x264's CRF

        // Keyframes exactly on segment boundaries: ffmpeg marks the forced frames as I, -forced_idr turns them into IDR,
        // and adaptive I/B placement is off so the encoder never moves or adds frame types on its own.
        output.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{job.Timing.SegmentSeconds})", "-forced_idr", "1"]);
        if (target.Codec == "hevc" && target.SegmentContainer == "mp4") output.AddRange(["-tag:v", "hvc1"]); // Apple needs hvc1 in fMP4
        return new VideoArgs(input, output);
    }

    DecodedVideo IVideoDecoder.Build(VideoJob job)
    {
        var s = _state;
        var plan = PlanFor(job, s) is { Decode: not Decode.Software } p ? p : throw new InvalidOperationException("Quick Sync cannot decode this job");
        var (input, filters) = DecodeAndProcess(job, plan, s, toQsvEncoder: false);
        return new DecodedVideo(Id, input, filters);
    }

    /// <summary>
    /// Input arguments and the main filter chain of Jellyfin's GetIntelQsvVaapiVidFiltersPrefered (VPP tone mapping on
    /// QSV surfaces as in its QSV/D3D11 chain). Ends in QSV surfaces (or nv12 for upload) for a QSV encoder, or in nv12
    /// system memory for a software encoder.
    /// </summary>
    private (List<string> Input, List<string> Filters) DecodeAndProcess(VideoJob job, Plan plan, State s, bool toQsvEncoder)
    {
        var src = job.Source;
        var p = job.Processing;
        var ocl = plan.Tone == Tone.Ocl;
        var swOcl = ocl && plan.Decode == Decode.Software;
        var input = new List<string>(DeviceArgs(s.Device, ocl, swOcl ? "ocl" : "qs"));
        var f = new List<string>();
        // Overwrite colour properties first: the HDR input signal for tone mapping, otherwise bt709 (Jellyfin).
        f.Add(plan.Tone == Tone.None ? SdrParams : src.HdrType == "HLG" ? HlgParams : HdrParams);

        switch (plan.Decode)
        {
            case Decode.Software:
                if (plan.Deinterlace)
                    f.Add($"{(p.Deinterlace.Method == "bwdif" ? "bwdif" : "yadif")}={(plan.DoubleRate ? 1 : 0)}:-1:0");
                if (plan.Size is var (sw, sh)) f.Add($"scale={sw}:{sh}");
                f.Add(ocl ? "format=yuv420p10le" : "format=nv12");
                if (ocl) f.AddRange(["hwupload=derive_device=opencl", OclToneMap(p.ToneMap), "hwmap=mode=read", "format=nv12"]);
                // The QSV encoder takes system memory after OpenCL; otherwise upload with a fixed pool for its surfaces.
                else f.Add("hwupload=extra_hw_frames=64");
                return (input, f);

            case Decode.Vaapi:
                input.AddRange(["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"]);
                if (src.Codec == "h264" && string.Equals(src.Profile, "baseline", StringComparison.OrdinalIgnoreCase))
                    input.AddRange(["-hwaccel_flags", "+allow_profile_mismatch"]);
                if (plan.Deinterlace) f.Add($"deinterlace_vaapi=rate={(plan.DoubleRate ? "field" : "frame")}");
                var vaFormat = plan.Tone == Tone.None ? "nv12" : plan.Rext ? "p010" : null;
                var scale = SizeAndFormat(plan.Size, vaFormat);
                if (scale.Count > 0) f.Add("scale_vaapi=" + string.Join(':', scale) + ":extra_hw_frames=24");
                if (plan.Tone == Tone.VppVaapi) f.AddRange(VaapiToneMap(p.VppToneMap));
                if (ocl) f.AddRange(["hwmap=derive_device=opencl:mode=read", OclToneMap(p.ToneMap)]);
                if (!toQsvEncoder) f.AddRange(["hwmap=mode=read", "format=nv12"]);
                else if (ocl) f.AddRange(["hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16", "format=qsv"]);
                else f.AddRange(["hwmap=derive_device=qsv", "format=qsv"]);
                return (input, f);

            default:
                input.AddRange(["-hwaccel", "qsv", "-hwaccel_device", "qs", "-hwaccel_output_format", "qsv", "-c:v", Decoders[src.Codec]]);
                var vpp = plan.Tone == Tone.VppQsv;
                var procamp = vpp ? Procamp(p.VppToneMap) : [];
                var twoPass = vpp && (plan.Rext || procamp.Count > 0); // VPP tone mapping needs p010 input
                var qsvFormat = plan.Tone switch
                {
                    Tone.None => "nv12",
                    Tone.VppQsv => twoPass ? "p010" : "nv12",
                    _ => plan.Rext ? "p010" : null,
                };
                var opts = new List<string>();
                if (plan.Deinterlace) opts.AddRange(["deinterlace=advanced", .. plan.DoubleRate ? ["rate=field"] : Array.Empty<string>()]);
                opts.AddRange(SizeAndFormat(plan.Size, qsvFormat));
                // Jellyfin: ProcAmp in the scaling pass and tone mapping in a second one, or tone mapping right here.
                if (vpp) opts.AddRange(procamp.Count > 0 ? [.. procamp, "procamp=1", "async_depth=2"] : twoPass ? [] : ["tonemap=1"]);
                if (opts.Count > 0) f.Add("vpp_qsv=" + string.Join(':', opts));
                if (twoPass) f.Add("vpp_qsv=tonemap=1:format=nv12:async_depth=2");
                if (vpp) f.Add(SdrParams); // bt709 in case VPP did not tone-map (no HDR metadata)
                if (ocl) f.AddRange(["hwmap=derive_device=opencl:mode=read", OclToneMap(p.ToneMap)]);
                if (!toQsvEncoder) f.AddRange([ocl ? "hwmap=mode=read" : "hwdownload", "format=nv12"]);
                else if (ocl) f.AddRange(["hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16", "format=qsv"]);
                return (input, f);
        }
    }

    private static List<string> SizeAndFormat((int W, int H)? size, string? format)
    {
        var o = new List<string>();
        if (size is var (w, h)) o.AddRange([$"w={w}", $"h={h}"]);
        if (format is not null) o.Add("format=" + format);
        return o;
    }

    /// <summary>Jellyfin's VPP ProcAmp ranges: brightness only when non-zero in [-100, 100], contrast only in (1, 10].</summary>
    private static List<string> Procamp(VppToneMapping v)
    {
        var o = new List<string>();
        if (v.Brightness != 0 && v.Brightness is >= -100 and <= 100) o.Add("brightness=" + Str(v.Brightness));
        if (v.Contrast is > 1 and <= 10) o.Add("contrast=" + Str(v.Contrast));
        return o;
    }

    private static IEnumerable<string> VaapiToneMap(VppToneMapping v)
    {
        var procamp = new List<string>();
        if (v.Brightness != 0 && v.Brightness is >= -100 and <= 100) procamp.Add("b=" + Str(v.Brightness));
        if (v.Contrast is > 1 and <= 10) procamp.Add("c=" + Str(v.Contrast));
        if (procamp.Count > 0) yield return "procamp_vaapi=" + string.Join(':', procamp);
        yield return "tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32";
    }

    /// <summary>Jellyfin's GetHwTonemapFilter for OpenCL (jellyfin-ffmpeg 7.x knows every tonemap_mode).</summary>
    private static string OclToneMap(ToneMapping t)
    {
        var a = $"tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap={t.Algorithm}:peak={Str(t.Peak)}:desat={Str(t.Desat)}";
        if (t.Mode is "max" or "rgb" or "lum" or "itp") a += ":tonemap_mode=" + t.Mode;
        if (t.Param != 0) a += ":param=" + Str(t.Param);
        if (t.Range is "tv" or "pc") a += ":range=" + t.Range;
        return a;
    }

    private List<string> EncoderArgs(string codec, VideoProcessing p, State s)
    {
        var args = new List<string> { "-c:v", Encoders[codec], "-preset", PresetFor(p.Software.Preset), "-profile:v", codec == "h264" ? "high" : "main" };
        var forced = codec switch { "h264" => p.HwEncode.LowPowerH264, "hevc" => p.HwEncode.LowPowerHevc, _ => null };
        if (forced ?? s.LowPowerOnly.Contains(codec)) args.AddRange(["-low_power", "1"]);
        args.AddRange(["-adaptive_i", "0", "-adaptive_b", "0"]);
        return args;
    }

    /// <summary>The dashboard preset ("auto" = this module's), limited to what QSV accepts like Jellyfin.</summary>
    private string PresetFor(string preset)
    {
        var p = (preset is "" or "auto" ? options.CurrentValue.Preset : preset).ToLowerInvariant();
        return Presets.Contains(p) ? p : "veryfast";
    }

    private static string Str(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Str(double v) => v.ToString(CultureInfo.InvariantCulture);
}
