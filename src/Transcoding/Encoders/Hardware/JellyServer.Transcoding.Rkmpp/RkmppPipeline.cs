using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static JellyServer.Transcoding.Rkmpp.Common;

namespace JellyServer.Transcoding.Rkmpp;

/// <summary>
/// Rockchip MPP (h264_rkmpp, hevc_rkmpp; no AV1 encoder), mirroring Jellyfin's rkmpp branch of EncodingHelper.
/// With the RGA filters and OpenCL (Mali) available, frames stay in DRM PRIME buffers: rkmpp decodes, vpp_rkrga
/// scales/converts, tonemap_opencl tone-maps ("preferred" pipeline, GetRkmppVidFiltersPrefered). Without OpenCL the
/// decoder copies 8-bit frames back and Jellyfin's software chain runs on the CPU; without the RGA filters
/// decoding and filtering are all on the CPU and only the encoder is MPP.
/// </summary>
internal sealed class RkmppPipeline(IOptionsMonitor<RkmppOptions> options, ILogger<RkmppPipeline> log) : IVideoPipeline, IVideoDecoder
{
    // GetRkmppDeviceArgs: device selection is not supported.
    private static readonly string[] Device = ["-init_hw_device", "rkmpp=rk"];
    private static readonly string[] OpenClDevice = ["-init_hw_device", "opencl=ocl@rk", "-filter_hw_device", "ocl"];
    private static readonly Dictionary<string, string> Encoders = new() { ["h264"] = "h264_rkmpp", ["hevc"] = "hevc_rkmpp" };

    /// <param name="Full">IsRkmppFullSupported: rkmpp hwaccel plus scale_rkrga/vpp_rkrga/overlay_rkrga.</param>
    /// <param name="Preferred">Full plus working OpenCL: DRM PRIME end to end (10-bit decoding, GPU tone mapping).</param>
    /// <param name="Decode">A real rkmpp test decode succeeded.</param>
    private sealed record State(IReadOnlySet<string> Codecs, bool Full, bool Preferred, bool Decode, CpuToneMap Cpu);

    private static readonly State Nothing = new(new HashSet<string>(), false, false, false, CpuToneMap.None);

    private volatile State _state = Nothing;
    private readonly Lock _probeLock = new();
    private (IFfmpegCapabilities Ffmpeg, Task<State> Task)? _probe;

    public string Id => "rkmpp";
    public IReadOnlySet<string> OutputCodecs => _state.Codecs;
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => false;

    async Task<bool> IVideoPipeline.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeOnceAsync(ffmpeg, ct)).Codecs.Count > 0;
    async Task<bool> IVideoDecoder.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeOnceAsync(ffmpeg, ct)).Decode;

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
        var hwAccel = ffmpeg.HasHwAccel("rkmpp");
        string[] device = hwAccel ? Device : [];
        // Only rkmpp builds of ffmpeg list these encoders; without the MPP device (/dev/mpp_service) the encoder fails to open.
        var codecs = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
        {
            if (ffmpeg.HasEncoder(encoder) && await ffmpeg.TryRunAsync(TestRun(device, ["-vf", "format=nv12", .. Encode(codec, 2_000_000)]), ct))
                codecs.Add(codec);
        }
        if (codecs.Count == 0)
        {
            if (Encoders.Values.Any(ffmpeg.HasEncoder)) log.LogWarning("RKMPP test encode failed: is /dev/mpp_service (and /dev/rga, /dev/dri) mapped into the container?");
            return _state = Nothing;
        }

        var full = hwAccel && ffmpeg.HasFilter("scale_rkrga") && ffmpeg.HasFilter("vpp_rkrga") && ffmpeg.HasFilter("overlay_rkrga");
        var decode = full && await WithSampleAsync(ffmpeg, "h264", false, sample => ffmpeg.TryRunAsync(
            ["-hide_banner", "-loglevel", "error", .. Device, "-hwaccel", "rkmpp", "-hwaccel_output_format", "drm_prime", "-i", sample,
             "-frames:v", "1", "-vf", "vpp_rkrga=format=nv12,hwdownload,format=nv12", "-f", "null", "-"], ct), ct);
        // OpenCL on the Mali GPU: run the real HDR path (rkmpp decode → RGA → tonemap_opencl → system memory).
        var preferred = decode && ffmpeg.HasHwAccel("opencl") && ffmpeg.HasFilter("scale_opencl") && ffmpeg.HasFilter("tonemap_opencl")
            && await WithSampleAsync(ffmpeg, "hevc", true, sample => ffmpeg.TryRunAsync(
                ["-hide_banner", "-loglevel", "error", .. Device, "-hwaccel", "rkmpp", "-hwaccel_output_format", "drm_prime", "-i", sample,
                 "-frames:v", "1", "-vf", "vpp_rkrga=format=p010,hwmap=derive_device=opencl,"
                 + HwToneMap(new ToneMapping(), "opencl") + ",hwdownload,format=nv12", "-f", "null", "-"], ct), ct);

        var state = new State(codecs, full, preferred, decode, DetectCpuToneMap(ffmpeg));
        _state = state;
        log.LogInformation("RKMPP encodes {Codecs}; decoding {Decode}; filters {Filters}", string.Join(", ", codecs),
            decode ? "on the VPU" : "on the CPU", preferred ? "on RGA/OpenCL" : "on the CPU");
        return state;
    }

    /// <summary>The MPP encoders cannot force keyframes (Jellyfin sets them by GOP), so the frame rate must be known.</summary>
    public bool CanHandle(VideoJob job) =>
        !job.Target.Copy && job.Decoded is null && _state.Codecs.Contains(job.Target.Codec) && job.Source.FrameRate > 0;

    public bool CanDecode(VideoJob job) => HwDecodes(_state, job);

    public VideoArgs Build(VideoJob job)
    {
        var st = _state;
        var hw = HwDecodes(st, job);
        var output = new List<string>();
        if (Filters(st, job, hw, toEncoder: true) is { Count: > 0 } filters) output.AddRange(["-vf", string.Join(',', filters)]);
        output.AddRange(Encode(job.Target.Codec, CappedBitrate(job.Target) ?? options.CurrentValue.FallbackBitrate));
        // DRM frames are deinterlaced by the decoder's IEP at frame rate; CPU deinterlacing may double it.
        output.AddRange(KeyFrames(job, hw && st.Preferred ? job.Source.FrameRate : OutputFps(job)));
        output.AddRange(Hvc1(job.Target));
        return new VideoArgs(Input(st, job, hw), output);
    }

    DecodedVideo IVideoDecoder.Build(VideoJob job)
    {
        var st = _state;
        return new DecodedVideo(Id, Input(st, job, hw: true), Filters(st, job, hw: true, toEncoder: false));
    }

    /// <summary>
    /// Jellyfin's GetRkmppVidDecoder: MPEG-1/2/4 and VP8 in 8-bit; H.264 (Hi10P too), HEVC, VP9 and AV1 in 8-bit, or 10-bit
    /// when frames stay in DRM buffers (NV15 is a bitstream-only format); RGA scales 1/16 to 16 at most.
    /// </summary>
    private static bool HwDecodes(State st, VideoJob job)
    {
        var s = job.Source;
        if (!st.Decode || !DashboardAllows(s, job.Processing.HwDecode) || HasProfile(s, "4:2:2", "4:4:4") || IsHevcRext(s)) return false;
        if (ScaleRatio(job) > 16) return false;
        return s.Codec switch
        {
            "mpeg1video" or "mpeg2video" or "mpeg4" or "vp8" => s.BitDepth <= 8,
            "h264" or "hevc" or "vp9" or "av1" => s.BitDepth <= 8 || (s.BitDepth == 10 && st.Preferred),
            _ => false,
        };
    }

    private static double ScaleRatio(VideoJob job) => ScaleHeight(job) is > 0 and var h ? (double)job.Source.Height / h : 1;

    private static List<string> Input(State st, VideoJob job, bool hw)
    {
        var input = new List<string>();
        if (st.Full) input.AddRange(Device);
        // An OpenCL device only for uploading software-decoded HDR frames to the tone mapper.
        if (!hw && st.Preferred && IsToneMappable(job.Source) && job.Processing.ToneMap.Enabled) input.AddRange(OpenClDevice);
        if (!hw) return input;
        input.AddRange(["-hwaccel", "rkmpp"]);
        if (st.Preferred)
        {
            input.AddRange(["-hwaccel_output_format", "drm_prime", "-noautorotate"]);
            // RGA3 handles AFBC within 1/8 to 8; AV1 AFBC is broken on RK3588.
            if (job.Source.Codec is "h264" or "hevc" or "vp9" && ScaleRatio(job) <= 8) input.AddRange(["-afbc", "rga"]);
        }
        return input;
    }

    /// <summary>Jellyfin's GetRkmppVidFiltersPrefered, or the software chain without OpenCL.</summary>
    private static List<string> Filters(State st, VideoJob job, bool hw, bool toEncoder)
    {
        if (!st.Preferred) return CpuChain(job, hw ? "nv12" : "yuv420p", st.Cpu);

        var s = job.Source;
        var toneMap = IsToneMappable(s) && job.Processing.ToneMap.Enabled;
        var height = ScaleHeight(job);
        var f = new List<string> { toneMap ? HdrParams(s) : SdrParams };
        if (!hw)
        {
            if (s.Interlaced) f.Add(SwDeinterlace(job));
            if (height > 0) f.Add($"scale=trunc(oh*a/2)*2:{height}:flags=fast_bilinear");
            f.Add(toneMap ? "format=yuv420p10le" : "format=nv12");
            if (toneMap) f.Add("hwupload=derive_device=opencl");
        }
        else
        {
            var format = toneMap ? "p010" : "nv12";
            // RGA3 only blits 1/8 to 8 at once: a first pass brings larger ratios into range (NV15 avoids a P010 driver bug).
            if (ScaleRatio(job) > 8)
                f.Add($"scale_rkrga=w=iw/7.9:h=ih/7.9:format={(toneMap ? "nv15" : format)}:force_original_aspect_ratio=increase:force_divisible_by=4:afbc=1");
            var vpp = height > 0 ? $"vpp_rkrga=w={ScaleWidth(s, height)}:h={height}:format={format}" : $"vpp_rkrga=format={format}";
            if (toEncoder && !toneMap) vpp += ":afbc=1"; // full AFBC pipeline into the MPP encoder saves DDR bandwidth
            f.Add(vpp);
            if (toneMap) f.Add("hwmap=derive_device=opencl");
        }
        if (toneMap) f.Add(HwToneMap(job.Processing.ToneMap, "opencl"));

        if (hw && toEncoder && toneMap) f.AddRange(["hwmap=derive_device=rkmpp:reverse=1", "format=drm_prime"]);
        else if ((hw && !toEncoder) || (!hw && toneMap)) f.AddRange(["hwdownload", "format=nv12"]);
        return f;
    }

    /// <summary>Jellyfin's default GetVideoBitrateParam branch (rkmpp has no preset map) and the H.264 High / HEVC Main profile.</summary>
    private static List<string> Encode(string codec, long bitrate) =>
    [
        "-c:v", Encoders[codec], "-b:v", Num(bitrate), "-maxrate", Num(bitrate), "-bufsize", Num(bitrate * 2),
        "-profile:v", codec == "h264" ? "high" : "main",
    ];
}
