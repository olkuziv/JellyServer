using System.Globalization;
using JellyServer.Transcoding.Software;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.X265;

/// <summary>
/// Software HEVC (Main, 8-bit) with libx265, with Jellyfin's encoder tuning. Same CPU processing as x264:
/// deinterlace, scale and tone map, unless a hardware decoder delivers the frames.
/// </summary>
internal sealed class X265Pipeline(IOptionsMonitor<X265Options> options, ILogger<X265Pipeline> log) : IVideoPipeline
{
    // Jellyfin's EncodingHelper: no scene-cut or open-GOP keyframes, so only the forced ones start a GOP.
    private const string Params = "no-scenecut=1:no-open-gop=1:no-info=1";

    // Faster than the presets these replace; Jellyfin drops them with "ultrafast", which is faster still.
    private const string FastParams = ":subme=3:merange=25:rc-lookahead=10:me=star:ctu=32:max-tu-size=32:min-cu-size=16"
        + ":rskip=2:rskip-edge-threshold=2:no-sao=1:no-strong-intra-smoothing=1";

    private volatile ToneMapper _toneMapper = ToneMapper.Zscale;

    public string Id => "x265";
    public IReadOnlySet<string> OutputCodecs { get; } = new HashSet<string> { "hevc" };
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => true;

    public Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        if (!ffmpeg.HasEncoder("libx265")) return Task.FromResult(false);
        var toneMapper = SoftwareVideo.ProbeToneMapper(ffmpeg);
        if (toneMapper == ToneMapper.None) log.LogWarning("ffmpeg has neither tonemapx nor zscale/tonemap: HDR sources will be encoded without tone mapping");
        _toneMapper = toneMapper;
        return Task.FromResult(true);
    }

    public bool CanHandle(VideoJob job) => !job.Target.Copy && job.Target.Codec == "hevc";

    public VideoArgs Build(VideoJob job)
    {
        var opt = options.CurrentValue;
        var sw = job.Processing.Software;
        var preset = SoftwareVideo.Preset(sw, opt.Preset);
        List<string> args =
        [
            "-c:v", "libx265", "-preset", preset, "-crf", (sw.H265Crf ?? opt.Crf).ToString(CultureInfo.InvariantCulture),
            "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-x265-params", preset == "ultrafast" ? Params : Params + FastParams,
            // Forced keyframes as IDR frames: every segment decodes on its own.
            .. SoftwareVideo.Keyframes(job.Timing), "-forced-idr", "1",
        ];
        if (job.Target.SegmentContainer == "mp4") args.AddRange(["-tag:v", "hvc1"]); // Apple players reject hev1
        args.AddRange(SoftwareVideo.Threads(sw.Threads));
        if (SoftwareVideo.Filters(job, _toneMapper) is { } filters) args.AddRange(["-vf", filters]);
        args.AddRange(SoftwareVideo.MaxRate(job.Target));
        return new VideoArgs(job.Decoded?.Input ?? [], args);
    }
}
