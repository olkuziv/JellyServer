namespace JellyServer.Transcoding.Pipelines;

/// <summary>Remux: the video track is copied untouched, so it costs almost no CPU and is never limited.</summary>
public sealed class CopyPipeline : IVideoPipeline
{
    public const string PipelineId = "copy";

    public string Id => PipelineId;
    public IReadOnlySet<string> OutputCodecs { get; } = new HashSet<string>();
    public int MaxConcurrent => 0;
    public bool AcceptsDecodedInput => false;
    public bool CanHandle(VideoJob job) => CanHandle(job.Source, job.Target);
    public VideoArgs Build(VideoJob job) => Build(job.Source, job.Target, job.Timing);

    public Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => Task.FromResult(true);

    public bool CanHandle(VideoSource source, VideoTarget target) => target.Copy;

    public VideoArgs Build(VideoSource source, VideoTarget target, SegmentTiming timing)
    {
        var output = new List<string> { "-c:v", "copy" };
        // Apple players require the hvc1 tag for HEVC in fMP4.
        if (target.Codec == "hevc") output.AddRange(["-tag:v", "hvc1"]);
        if (target.Codec == "h264" && target.SegmentContainer == "ts") output.AddRange(["-bsf:v", "h264_mp4toannexb"]);
        return new VideoArgs(["-noaccurate_seek"], output);
    }
}
