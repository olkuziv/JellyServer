using System.Globalization;
using JellyServer.Transcoding.Software;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.X264;

/// <summary>
/// Software H.264 with libx264. Works everywhere ffmpeg does, but costs a lot of CPU. Deinterlacing,
/// scaling and HDR tone mapping run on the CPU too, unless a hardware decoder delivers the frames.
/// </summary>
internal sealed class X264Pipeline(IOptionsMonitor<X264Options> options, ILogger<X264Pipeline> log) : IVideoPipeline
{
    // One enum: a re-probe replaces it in a single atomic write.
    private volatile ToneMapper _toneMapper = ToneMapper.Zscale;

    public string Id => "x264";
    public IReadOnlySet<string> OutputCodecs { get; } = new HashSet<string> { "h264" };
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => true;

    public Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        if (!ffmpeg.HasEncoder("libx264")) return Task.FromResult(false);
        var toneMapper = SoftwareVideo.ProbeToneMapper(ffmpeg);
        if (toneMapper == ToneMapper.None) log.LogWarning("ffmpeg has neither tonemapx nor zscale/tonemap: HDR sources will be encoded without tone mapping");
        _toneMapper = toneMapper;
        return Task.FromResult(true);
    }

    public bool CanHandle(VideoJob job) => !job.Target.Copy && job.Target.Codec == "h264";

    public VideoArgs Build(VideoJob job)
    {
        var opt = options.CurrentValue;
        var sw = job.Processing.Software;
        List<string> args =
        [
            "-c:v", "libx264", "-preset", SoftwareVideo.Preset(sw, opt.Preset),
            "-crf", (sw.H264Crf ?? opt.Crf).ToString(CultureInfo.InvariantCulture),
            "-profile:v", "high", "-level", Level(job), "-pix_fmt", "yuv420p",
            .. SoftwareVideo.Keyframes(job.Timing), "-sc_threshold", "0", // no extra keyframes at scene cuts
        ];
        args.AddRange(SoftwareVideo.Threads(sw.Threads));
        if (SoftwareVideo.Filters(job, _toneMapper) is { } filters) args.AddRange(["-vf", filters]);
        args.AddRange(SoftwareVideo.MaxRate(job.Target));
        return new VideoArgs(job.Decoded?.Input ?? [], args);
    }

    /// <summary>4.1 covers 1080p; larger pictures (MaxHeight 0 or above 1080) need level 5.1, or 5.2 above 30 fps.</summary>
    private static string Level(VideoJob job) =>
        SoftwareVideo.OutputHeight(job) <= 1080 ? "4.1" : job.Source.FrameRate > 30 ? "5.2" : "5.1";
}
