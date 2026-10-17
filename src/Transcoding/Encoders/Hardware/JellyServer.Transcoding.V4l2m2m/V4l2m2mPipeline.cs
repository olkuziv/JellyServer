using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static JellyServer.Transcoding.V4l2m2m.Common;

namespace JellyServer.Transcoding.V4l2m2m;

/// <summary>
/// Video4Linux2 memory-to-memory encoding (h264_v4l2m2m, hevc_v4l2m2m), mirroring Jellyfin's v4l2m2m handling:
/// there are no V4L2 filters, so frames are processed by Jellyfin's software chain (GetSwVidFilterChain) into
/// yuv420p with widths aligned to 64 (the encoders' stride requirement) and passed to the M2M encoder, which ffmpeg
/// finds by scanning /dev/video*. Decoding is in software unless <see cref="V4l2m2mOptions.HwDecode"/> is set.
/// </summary>
internal sealed class V4l2m2mPipeline(IOptionsMonitor<V4l2m2mOptions> options, ILogger<V4l2m2mPipeline> log) : IVideoPipeline, IVideoDecoder
{
    private static readonly Dictionary<string, string> Encoders = new() { ["h264"] = "h264_v4l2m2m", ["hevc"] = "hevc_v4l2m2m" };

    private static readonly Dictionary<string, string> Decoders = new()
    {
        ["h264"] = "h264_v4l2m2m", ["hevc"] = "hevc_v4l2m2m", ["mpeg2video"] = "mpeg2_v4l2m2m", ["mpeg4"] = "mpeg4_v4l2m2m",
        ["vp8"] = "vp8_v4l2m2m", ["vp9"] = "vp9_v4l2m2m",
    };

    /// <param name="Decodes">Source codecs whose M2M decoder passed a test decode.</param>
    private sealed record State(IReadOnlySet<string> Codecs, IReadOnlySet<string> Decodes, CpuToneMap Cpu);

    private static readonly State Nothing = new(new HashSet<string>(), new HashSet<string>(), CpuToneMap.None);

    private volatile State _state = Nothing;
    private readonly Lock _probeLock = new();
    private (IFfmpegCapabilities Ffmpeg, Task<State> Task)? _probe;

    public string Id => "v4l2m2m";
    public IReadOnlySet<string> OutputCodecs => _state.Codecs;
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => false;

    async Task<bool> IVideoPipeline.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeOnceAsync(ffmpeg, ct)).Codecs.Count > 0;
    async Task<bool> IVideoDecoder.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeOnceAsync(ffmpeg, ct)).Decodes.Count > 0;

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
        // Without an M2M encoder device ffmpeg fails at once ("Could not find a valid device").
        var codecs = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
        {
            if (ffmpeg.HasEncoder(encoder) && await ffmpeg.TryRunAsync(TestRun([], ["-vf", "format=yuv420p", .. Encode(codec, 2_000_000)]), ct))
                codecs.Add(codec);
        }
        var decodes = new HashSet<string>();
        if (options.CurrentValue.HwDecode)
        {
            foreach (var (codec, decoder) in Decoders)
            {
                if (ffmpeg.HasDecoder(decoder) && await WithSampleAsync(ffmpeg, codec, false, sample => ffmpeg.TryRunAsync(
                        ["-hide_banner", "-loglevel", "error", "-c:v", decoder, "-i", sample, "-frames:v", "1", "-f", "null", "-"], ct), ct))
                    decodes.Add(codec);
            }
        }
        var state = new State(codecs, decodes, DetectCpuToneMap(ffmpeg));
        _state = state;
        if (codecs.Count == 0 && Encoders.Values.Any(ffmpeg.HasEncoder))
            log.LogWarning("V4L2 M2M test encode failed: no /dev/video* encoder device is mapped into the container");
        else if (codecs.Count > 0)
            log.LogInformation("V4L2 M2M encodes {Codecs}; decoding {Decode}", string.Join(", ", codecs),
                decodes.Count > 0 ? "on the SoC for " + string.Join(", ", decodes) : "on the CPU");
        return state;
    }

    public bool CanHandle(VideoJob job) => !job.Target.Copy && job.Decoded is null && _state.Codecs.Contains(job.Target.Codec);

    public bool CanDecode(VideoJob job) => HwDecodes(_state, job);

    public VideoArgs Build(VideoJob job)
    {
        var st = _state;
        var hw = HwDecodes(st, job);
        var output = new List<string> { "-vf", string.Join(',', CpuChain(job, "yuv420p", st.Cpu, widthAlign: 64)) };
        output.AddRange(Encode(job.Target.Codec, CappedBitrate(job.Target) ?? options.CurrentValue.FallbackBitrate));
        output.AddRange(KeyFrames(job));
        output.AddRange(Hvc1(job.Target));
        return new VideoArgs(hw ? ["-c:v", Decoders[job.Source.Codec]] : [], output);
    }

    DecodedVideo IVideoDecoder.Build(VideoJob job) =>
        new(Id, ["-c:v", Decoders[job.Source.Codec]], CpuChain(job, "yuv420p", _state.Cpu));

    /// <summary>The M2M decoders take 8-bit 4:2:0 only, under the dashboard's codec list.</summary>
    private static bool HwDecodes(State st, VideoJob job)
    {
        var s = job.Source;
        return st.Decodes.Contains(s.Codec) && DashboardAllows(s, job.Processing.HwDecode) && s.BitDepth <= 8 && !HasProfile(s, "4:2:2", "4:4:4");
    }

    /// <summary>
    /// Jellyfin's default GetVideoBitrateParam branch. No -profile: Jellyfin skips it for h264_v4l2m2m and the wrappers only
    /// take numeric profiles, so the driver default (High/Main) applies.
    /// </summary>
    private static List<string> Encode(string codec, long bitrate) =>
        ["-c:v", Encoders[codec], "-b:v", Num(bitrate), "-maxrate", Num(bitrate), "-bufsize", Num(bitrate * 2)];
}
