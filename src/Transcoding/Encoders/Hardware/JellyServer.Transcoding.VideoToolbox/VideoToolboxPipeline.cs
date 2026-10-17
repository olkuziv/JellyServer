using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static JellyServer.Transcoding.VideoToolbox.Common;

namespace JellyServer.Transcoding.VideoToolbox;

/// <summary>
/// Apple VideoToolbox (h264_videotoolbox, hevc_videotoolbox; there is no AV1 encoder), mirroring Jellyfin's
/// videotoolbox branch of EncodingHelper. With jellyfin-ffmpeg's Metal filters (scale_vt, yadif_videotoolbox, …)
/// frames stay in videotoolbox surfaces and scale_vt scales and tone-maps in one pass ("preferred" pipeline);
/// otherwise frames are decoded to memory and processed on the CPU (Jellyfin's legacy software chain).
/// HDR is tone-mapped on the GPU only when the dashboard's "Enable VideoToolbox tone mapping" is on
/// (mapped to <see cref="ToneMapping.Enabled"/> for this hardware type).
/// </summary>
internal sealed class VideoToolboxPipeline(IOptionsMonitor<VideoToolboxOptions> options, ILogger<VideoToolboxPipeline> log)
    : IVideoPipeline, IVideoDecoder
{
    // GetVideoToolboxDeviceArgs: device selection is not supported, filters find the only device.
    private static readonly string[] Device = ["-init_hw_device", "videotoolbox=vt"];
    private const string VtToneMap = "color_matrix=bt709:color_primaries=bt709:color_transfer=bt709";
    private static readonly Dictionary<string, string> Encoders = new() { ["h264"] = "h264_videotoolbox", ["hevc"] = "hevc_videotoolbox" };

    /// <param name="Full">IsVideoToolboxFullSupported and the hwupload → scale_vt test ran: Metal filters on VT surfaces.</param>
    /// <param name="ConstantQuality">-q:v works (Apple Silicon).</param>
    private sealed record State(IReadOnlySet<string> Codecs, bool Full, bool Bwdif, bool ConstantQuality, bool Decode, CpuToneMap Cpu);

    private static readonly State Nothing = new(new HashSet<string>(), false, false, false, false, CpuToneMap.None);

    private volatile State _state = Nothing;
    private readonly Lock _probeLock = new();
    private (IFfmpegCapabilities Ffmpeg, Task<State> Task)? _probe;

    /// <summary>Apple Silicon decodes H.264 Hi10P (macOS 14.6+) and 4:2:2/4:4:4 H.264, as Jellyfin checks.</summary>
    internal static bool AppleSilicon { get; set; } = RuntimeInformation.OSArchitecture == Architecture.Arm64;
    internal static bool Hi10P => AppleSilicon && OperatingSystem.IsMacOSVersionAtLeast(14, 6);

    public string Id => "videotoolbox";
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
        // VideoToolbox only exists in macOS builds of ffmpeg: elsewhere nothing is run.
        if (!ffmpeg.HasHwAccel("videotoolbox")) return _state = Nothing;
        var codecs = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
        {
            if (ffmpeg.HasEncoder(encoder)
                && await ffmpeg.TryRunAsync(TestRun(Device, ["-vf", "format=nv12", .. Encode(codec, 2_000_000, "auto", false, 0)]), ct))
                codecs.Add(codec);
        }
        if (codecs.Count == 0)
        {
            log.LogWarning("VideoToolbox test encode failed");
            return _state = Nothing;
        }
        var first = codecs.First();
        var full = ffmpeg.HasFilter("yadif_videotoolbox") && ffmpeg.HasFilter("overlay_videotoolbox") && ffmpeg.HasFilter("tonemap_videotoolbox")
            && ffmpeg.HasFilter("scale_vt")
            && await ffmpeg.TryRunAsync(TestRun(Device,
                ["-vf", "format=nv12|p010le|videotoolbox_vld,hwupload,scale_vt=w=320:h=180", .. Encode(first, 2_000_000, "auto", false, 0)]), ct);
        var quality = await ffmpeg.TryRunAsync(TestRun(Device, ["-vf", "format=nv12", .. Encode(first, null, "auto", true, options.CurrentValue.Quality)]), ct);
        var decode = await WithSampleAsync(ffmpeg, "h264", false, sample => ffmpeg.TryRunAsync(
            ["-hide_banner", "-loglevel", "error", .. Device, "-hwaccel", "videotoolbox", "-hwaccel_output_format", "videotoolbox_vld",
             "-i", sample, "-frames:v", "1", "-vf", "hwdownload,format=nv12", "-f", "null", "-"], ct), ct);

        var state = new State(codecs, full, ffmpeg.HasFilter("bwdif_videotoolbox"), quality, decode, DetectCpuToneMap(ffmpeg));
        _state = state;
        log.LogInformation("VideoToolbox encodes {Codecs}; decoding {Decode}; filters {Filters}; {RateControl}", string.Join(", ", codecs),
            decode ? "on the GPU" : "on the CPU", full ? "on the GPU (Metal)" : "on the CPU",
            quality ? "constant quality without a bitrate cap" : "fixed bitrate without a cap");
        return state;
    }

    public bool CanHandle(VideoJob job) => !job.Target.Copy && job.Decoded is null && _state.Codecs.Contains(job.Target.Codec);

    public bool CanDecode(VideoJob job) => HwDecodes(_state, job);

    public VideoArgs Build(VideoJob job)
    {
        var st = _state;
        var opt = options.CurrentValue;
        var hw = HwDecodes(st, job);
        var output = new List<string>();
        if (Filters(st, job, hw, toEncoder: true) is { Count: > 0 } filters) output.AddRange(["-vf", string.Join(',', filters)]);
        var preset = job.Processing.Software.Preset is { Length: > 0 } p && p != "auto" ? p : opt.Preset;
        var bitrate = CappedBitrate(job.Target) ?? (st.ConstantQuality ? null : opt.FallbackBitrate);
        output.AddRange(Encode(job.Target.Codec, bitrate, preset, st.ConstantQuality, opt.Quality));
        output.AddRange(KeyFrames(job));
        output.AddRange(Hvc1(job.Target));
        return new VideoArgs(Input(st, hw), output);
    }

    DecodedVideo IVideoDecoder.Build(VideoJob job)
    {
        var st = _state;
        return new DecodedVideo(Id, Input(st, hw: true), Filters(st, job, hw: true, toEncoder: false));
    }

    /// <summary>
    /// Jellyfin's GetVideotoolboxVidDecoder: VP8 8-bit, H.264/VP9 8/10-bit, HEVC up to 12-bit 4:4:4. VideoToolbox decoders
    /// fall back to software internally, so only the dashboard's codec list applies (no 10-bit switches, as in Jellyfin).
    /// AV1 is left to the CPU: only M3 and newer decode it.
    /// </summary>
    private static bool HwDecodes(State st, VideoJob job)
    {
        var s = job.Source;
        if (!st.Decode || !job.Processing.HwDecode.Allows(s.Codec)) return false;
        return s.Codec switch
        {
            "vp8" => s.BitDepth <= 8,
            "h264" => (s.BitDepth <= 8 || (s.BitDepth == 10 && Hi10P)) && (!HasProfile(s, "4:2:2", "4:4:4") || AppleSilicon),
            "vp9" => s.BitDepth <= 10,
            "hevc" => s.BitDepth <= 12,
            _ => false,
        };
    }

    private static List<string> Input(State st, bool hw)
    {
        var input = new List<string>(Device);
        if (!hw) return input;
        input.AddRange(["-hwaccel", "videotoolbox"]);
        if (st.Full) input.AddRange(["-hwaccel_output_format", "videotoolbox_vld"]);
        input.Add("-noautorotate");
        return input;
    }

    /// <summary>Jellyfin's GetAppleVidFiltersPreferred, or the software chain without the Metal filters.</summary>
    private static List<string> Filters(State st, VideoJob job, bool hw, bool toEncoder)
    {
        if (!st.Full) return CpuChain(job, hw ? "nv12" : "yuv420p", st.Cpu);

        var s = job.Source;
        var f = new List<string>();
        if (s.Interlaced) f.Add(SwDeinterlace(job, "_videotoolbox", st.Bwdif)); // GetHwDeinterlaceFilter
        // Force 8-bit output for anything but plain 8-bit 4:2:0.
        var format = s.BitDepth > 8 || HasProfile(s, "4:2:2", "4:4:4") ? "format=nv12" : "";
        var height = ScaleHeight(job);
        var scale = height > 0 ? $"scale_vt=w={ScaleWidth(s, height)}:h={height}" + (format.Length > 0 ? ":" + format : "")
            : format.Length > 0 ? "scale_vt=" + format : "";
        // scale_vt scales and tone-maps in one shot (IsVideoToolboxTonemapAvailable: HDR10, HLG and HDR10-based DV).
        if (IsToneMappable(s) && job.Processing.ToneMap.Enabled) scale = scale.Length == 0 ? "scale_vt=" + VtToneMap : scale + ":" + VtToneMap;
        if (scale.Length > 0) f.Add(scale);

        if (!hw && f.Count > 0) f.InsertRange(0, ["format=nv12|p010le|videotoolbox_vld", "hwupload"]); // software-decoded frames
        if (!toEncoder && (hw || f.Count > 0)) f.AddRange(["hwdownload", "format=nv12"]);
        return f;
    }

    /// <summary>Jellyfin's GetEncoderParam (-prio_speed) and GetVideoBitrateParam for *_videotoolbox.</summary>
    private static List<string> Encode(string codec, long? bitrate, string preset, bool constantQuality, int quality)
    {
        var args = new List<string>
        {
            "-c:v", Encoders[codec],
            "-prio_speed", preset is "veryslow" or "slower" or "slow" or "medium" ? "0" : "1",
        };
        // "maxrate and bufsize can potentially lead to performance regression and even encoder hangs."
        if (bitrate is { } b) args.AddRange(["-b:v", Num(b), "-qmin", "-1", "-qmax", "-1"]);
        else if (constantQuality) args.AddRange(["-q:v", Num(Math.Clamp(quality, 1, 100))]);
        args.AddRange(["-profile:v", codec == "h264" ? "high" : "main"]);
        return args;
    }
}
