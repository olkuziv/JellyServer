using System.Globalization;
using JellyServer.Transcoding.Software;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.SvtAv1;

/// <summary>
/// Software AV1 (Main, 8-bit) with SVT-AV1. AV1 in HLS needs fMP4 segments, so MPEG-TS targets go elsewhere.
/// Same CPU processing as x264: deinterlace, scale and tone map, unless a hardware decoder delivers the frames.
/// </summary>
internal sealed class SvtAv1Pipeline(IOptionsMonitor<SvtAv1Options> options, ILogger<SvtAv1Pipeline> log) : IVideoPipeline
{
    // Jellyfin's tuning without its rc=1 (VBR): quality here is CRF, capped by -maxrate when the client has a limit.
    private const string Params = "tune=0:film-grain=0:enable-overlays=1:enable-tf=0";

    private volatile ToneMapper _toneMapper = ToneMapper.Zscale;

    public string Id => "svtav1";
    public IReadOnlySet<string> OutputCodecs { get; } = new HashSet<string> { "av1" };
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => true;

    public Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        if (!ffmpeg.HasEncoder("libsvtav1")) return Task.FromResult(false);
        var toneMapper = SoftwareVideo.ProbeToneMapper(ffmpeg);
        if (toneMapper == ToneMapper.None) log.LogWarning("ffmpeg has neither tonemapx nor zscale/tonemap: HDR sources will be encoded without tone mapping");
        _toneMapper = toneMapper;
        return Task.FromResult(true);
    }

    public bool CanHandle(VideoJob job) => !job.Target.Copy && job.Target.Codec == "av1" && job.Target.SegmentContainer == "mp4";

    public VideoArgs Build(VideoJob job)
    {
        var opt = options.CurrentValue;
        var sw = job.Processing.Software;
        List<string> args =
        [
            "-c:v", "libsvtav1", "-preset", Preset(sw.Preset, opt.Preset),
            "-crf", (sw.H265Crf ?? opt.Crf).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
            "-svtav1-params", Params,
            // libsvtav1 turns forced frames into key frames (checked with SVT-AV1 3.1): no fps-based GOP needed.
            .. SoftwareVideo.Keyframes(job.Timing),
        ];
        args.AddRange(SoftwareVideo.Threads(sw.Threads));
        if (SoftwareVideo.Filters(job, _toneMapper) is { } filters) args.AddRange(["-vf", filters]);
        args.AddRange(SoftwareVideo.MaxRate(job.Target)); // capped CRF
        return new VideoArgs(job.Decoded?.Input ?? [], args);
    }

    /// <summary>Jellyfin's map of the x264-style preset names; presets below 5 are too slow for live encoding.</summary>
    private static string Preset(string preset, int fallback) => (preset switch
    {
        "veryslow" => 5,
        "slower" => 6,
        "slow" => 7,
        "medium" => 8,
        "fast" => 9,
        "faster" => 10,
        "veryfast" => 11,
        "superfast" => 12,
        "ultrafast" => 13,
        _ => fallback,
    }).ToString(CultureInfo.InvariantCulture);
}
