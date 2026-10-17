using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static JellyServer.Transcoding.Amf.Common;

namespace JellyServer.Transcoding.Amf;

/// <summary>
/// AMD AMF encoding (h264_amf, hevc_amf, av1_amf), mirroring Jellyfin's AMF branch of EncodingHelper:
/// on Windows the GPU decodes through d3d11va (AMD adapter, vendor 0x1002) and, when OpenCL works, scales,
/// deinterlaces and tone-maps with OpenCL filters mapped onto the d3d11 frames ("preferred" pipeline);
/// otherwise (Linux with AMD's AMF runtime, or no OpenCL) frames are decoded/copied back and processed on the
/// CPU like Jellyfin's GetSwVidFilterChain, then handed to AMF.
/// </summary>
internal sealed class AmfPipeline(IOptionsMonitor<AmfOptions> options, ILogger<AmfPipeline> log) : IVideoPipeline, IVideoDecoder
{
    // GetD3d11vaDeviceArgs(0, "0x1002", "dx11") and GetOpenclDeviceArgs(0, null, "dx11", "ocl").
    private static readonly string[] D3d11Device = ["-init_hw_device", "d3d11va=dx11:,vendor=0x1002"];
    private static readonly string[] OpenClDevice = ["-init_hw_device", "opencl=ocl@dx11", "-filter_hw_device", "ocl"];
    private static readonly Dictionary<string, string> Encoders = new() { ["h264"] = "h264_amf", ["hevc"] = "hevc_amf", ["av1"] = "av1_amf" };

    /// <summary>Probed once per ffmpeg detection and swapped in as a whole.</summary>
    /// <param name="D3d11">Windows: ffmpeg has the d3d11va hwaccel (the AMD adapter is selected by vendor id).</param>
    /// <param name="OpenCl">The d3d11va-OpenCL interop chain ran: GPU scaling, deinterlacing and tone mapping.</param>
    /// <param name="Decode">A real d3d11va test decode succeeded.</param>
    private sealed record State(IReadOnlySet<string> Codecs, bool D3d11, bool OpenCl, bool OclDeinterlace, bool Decode, CpuToneMap Cpu);

    private static readonly State Nothing = new(new HashSet<string>(), false, false, false, false, CpuToneMap.None);

    private volatile State _state = Nothing;
    private readonly Lock _probeLock = new();
    private (IFfmpegCapabilities Ffmpeg, Task<State> Task)? _probe;

    public string Id => "amf";
    public IReadOnlySet<string> OutputCodecs => _state.Codecs;
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => false;

    async Task<bool> IVideoPipeline.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeOnceAsync(ffmpeg, ct)).Codecs.Count > 0;
    async Task<bool> IVideoDecoder.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeOnceAsync(ffmpeg, ct)).Decode;

    /// <summary>The pipeline and the decoder share one probe per detected ffmpeg.</summary>
    private Task<State> ProbeOnceAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        lock (_probeLock)
        {
            if (_probe is { } p && ReferenceEquals(p.Ffmpeg, ffmpeg) && !p.Task.IsFaulted && !p.Task.IsCanceled) return p.Task;
            var task = ProbeAsync(ffmpeg, ct);
            _probe = (ffmpeg, task);
            return task;
        }
    }

    private async Task<State> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        var d3d11 = ffmpeg.HasHwAccel("d3d11va");
        string[] device = d3d11 ? D3d11Device : [];
        // Listed encoders prove nothing: without an AMD GPU (or AMD's runtime on Linux) the encoder fails to open at once.
        var codecs = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
        {
            if (ffmpeg.HasEncoder(encoder)
                && await ffmpeg.TryRunAsync(TestRun(device, ["-vf", "format=nv12", .. Encode(codec, null, "auto", 24)]), ct))
                codecs.Add(codec);
        }
        if (codecs.Count == 0)
        {
            _state = Nothing;
            if (Encoders.Values.Any(ffmpeg.HasEncoder))
                log.LogWarning("AMF test encode failed: no AMD GPU, or AMD's AMF runtime (amf-amdgpu-pro on Linux) is not installed");
            return Nothing;
        }

        var openCl = d3d11 && ffmpeg.HasHwAccel("opencl") && ffmpeg.HasFilter("scale_opencl") && ffmpeg.HasFilter("tonemap_opencl")
            && await ffmpeg.TryRunAsync(TestRun([.. D3d11Device, .. OpenClDevice],
            [
                "-vf", "format=nv12,hwupload=derive_device=d3d11va:extra_hw_frames=24,format=d3d11,hwmap=derive_device=opencl:mode=read,"
                + "scale_opencl=w=320:h=180:format=nv12,hwmap=derive_device=d3d11va:mode=write:reverse=1,format=d3d11",
                .. Encode(codecs.First(), null, "auto", 24),
            ]), ct);
        var decode = d3d11 && await WithSampleAsync(ffmpeg, "h264", false, sample => ffmpeg.TryRunAsync(
            ["-hide_banner", "-loglevel", "error", .. D3d11Device, "-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11",
             "-i", sample, "-frames:v", "1", "-vf", "hwdownload,format=nv12", "-f", "null", "-"], ct), ct);

        var state = new State(codecs, d3d11, openCl, ffmpeg.HasFilter("yadif_opencl") && ffmpeg.HasFilter("bwdif_opencl"), decode,
            DetectCpuToneMap(ffmpeg));
        _state = state;
        log.LogInformation("AMF encodes {Codecs}; decoding {Decode}; filters {Filters}", string.Join(", ", codecs),
            decode ? "on the GPU (d3d11va)" : "on the CPU", openCl ? "on the GPU (OpenCL)" : "on the CPU");
        return state;
    }

    public bool CanHandle(VideoJob job) =>
        !job.Target.Copy && job.Decoded is null && _state.Codecs.Contains(job.Target.Codec)
        && (job.Target.Codec != "av1" || job.Target.SegmentContainer == "mp4"); // AV1 only in fMP4 segments

    public bool CanDecode(VideoJob job) => HwDecodes(_state, job);

    public VideoArgs Build(VideoJob job)
    {
        var st = _state;
        var opt = options.CurrentValue;
        var hw = HwDecodes(st, job);
        var output = new List<string>();
        if (Filters(st, job, hw, toEncoder: true) is { Count: > 0 } filters) output.AddRange(["-vf", string.Join(',', filters)]);
        output.AddRange(Encode(job.Target.Codec, CappedBitrate(job.Target), Preset(job, opt), opt.Qp));
        output.AddRange(KeyFrames(job));
        output.AddRange(Hvc1(job.Target));
        return new VideoArgs(Input(st, job, hw), output);
    }

    DecodedVideo IVideoDecoder.Build(VideoJob job)
    {
        var st = _state;
        return new DecodedVideo(Id, Input(st, job, hw: true), Filters(st, job, hw: true, toEncoder: false));
    }

    /// <summary>
    /// Jellyfin's GetAmfVidDecoder + GetHwaccelType: H.264/MPEG-2/VC-1 in 8-bit, HEVC/VP9/AV1 in 8 or 10-bit 4:2:0,
    /// under the dashboard's codec and bit-depth switches.
    /// </summary>
    private static bool HwDecodes(State st, VideoJob job)
    {
        var s = job.Source;
        if (!st.Decode || !DashboardAllows(s, job.Processing.HwDecode) || HasProfile(s, "4:2:2", "4:4:4") || IsHevcRext(s)) return false;
        return s.Codec switch
        {
            "h264" or "mpeg2video" or "vc1" => s.BitDepth <= 8,
            "hevc" or "vp9" or "av1" => s.BitDepth <= 10,
            _ => false,
        };
    }

    private static List<string> Input(State st, VideoJob job, bool hw)
    {
        var input = new List<string>();
        if (st.D3d11) input.AddRange(D3d11Device);
        if (st.OpenCl) input.AddRange(OpenClDevice);
        if (!hw) return input;
        input.AddRange(["-hwaccel", "d3d11va"]);
        if (st.OpenCl) input.AddRange(["-hwaccel_output_format", "d3d11", "-noautorotate"]); // frames stay on the GPU
        if (job.Source.Codec == "h264" && string.Equals(job.Source.Profile, "baseline", StringComparison.OrdinalIgnoreCase))
            input.AddRange(["-hwaccel_flags", "+allow_profile_mismatch"]);
        input.AddRange(["-threads", "2"]);
        return input;
    }

    /// <param name="toEncoder">True: frames go to AMF (d3d11 or system memory); false: system-memory nv12 for a software encoder.</param>
    private static List<string> Filters(State st, VideoJob job, bool hw, bool toEncoder)
    {
        var s = job.Source;
        var t = job.Processing.ToneMap;
        // Legacy copy-back pipeline: everything on the CPU, Jellyfin's software chain (which always tone-maps HDR).
        if (!st.OpenCl) return CpuChain(job, hw ? "nv12" : "yuv420p", st.Cpu);

        var toneMap = IsToneMappable(s) && t.Enabled;
        var height = ScaleHeight(job);
        var f = new List<string> { toneMap ? HdrParams(s) : SdrParams };
        if (hw)
        {
            // GetAmdDx11VidFiltersPrefered: map the d3d11 frames to OpenCL, process there.
            f.Add("hwmap=derive_device=opencl:mode=read");
            if (s.Interlaced && st.OclDeinterlace) f.Add(SwDeinterlace(job, "_opencl"));
            var format = toneMap ? "" : "format=nv12";
            if (height > 0) f.Add($"scale_opencl=w={ScaleWidth(s, height)}:h={height}" + (format.Length > 0 ? ":" + format : ""));
            else if (format.Length > 0) f.Add("scale_opencl=" + format);
        }
        else
        {
            // Software decode: CPU deinterlace and scale; upload only for OpenCL tone mapping.
            if (s.Interlaced) f.Add(SwDeinterlace(job));
            if (height > 0) f.Add($"scale=trunc(oh*a/2)*2:{height}");
            f.Add(toneMap ? "format=yuv420p10le" : "format=yuv420p");
            if (toneMap) f.AddRange(["hwupload=derive_device=d3d11va:extra_hw_frames=24", "format=d3d11", "hwmap=derive_device=opencl:mode=read"]);
        }
        if (toneMap) f.Add(HwToneMap(t, "opencl"));

        if (!toEncoder) f.AddRange(["hwmap=mode=read", "format=nv12"]); // OpenCL → system memory
        else if (hw || toneMap) f.AddRange(["hwmap=derive_device=d3d11va:mode=write:reverse=1", "format=d3d11"]); // back to d3d11 for AMF
        return f;
    }

    private static string Preset(VideoJob job, AmfOptions opt) =>
        job.Processing.Software.Preset is { Length: > 0 } p && p != "auto" ? p : opt.Preset;

    /// <summary>Jellyfin's GetEncoderParam + GetVideoBitrateParam for *_amf; constant QP when there is no cap.</summary>
    private static List<string> Encode(string codec, long? bitrate, string preset, int qp)
    {
        var args = new List<string>
        {
            "-c:v", Encoders[codec],
            "-quality", preset switch { "veryslow" or "slower" or "slow" => "quality", "medium" => "balanced", _ => "speed" },
        };
        if (codec != "h264") args.AddRange(["-header_insertion_mode", "gop"]);
        if (codec == "hevc") args.AddRange(["-gops_per_idr", "1"]);
        if (bitrate is { } b)
        {
            // "Override the too high default qmin 18 in transcoding preset in legacy h26x_amf."
            if (codec != "av1") args.AddRange(["-rc", "cbr", "-qmin", "0", "-qmax", "32"]);
            args.AddRange(["-b:v", Num(b), "-maxrate", Num(b), "-bufsize", Num(b * 2)]);
        }
        else
        {
            var q = Num(codec == "av1" ? Math.Clamp(qp, 0, 51) * 5L : Math.Clamp(qp, 0, 51)); // AV1 QP runs 0-255
            args.AddRange(["-rc", "cqp", "-qp_i", q, "-qp_p", q]);
            if (codec == "h264") args.AddRange(["-qp_b", q]);
        }
        args.AddRange(["-profile:v", codec == "h264" ? "high" : "main"]);
        return args;
    }
}
