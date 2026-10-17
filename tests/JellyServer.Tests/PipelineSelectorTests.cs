using JellyServer.Configuration;
using JellyServer.Transcoding;
using JellyServer.Transcoding.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JellyServer.Tests;

public class PipelineSelectorTests
{
    private sealed class Fake(string id, int max = 1, bool works = true, bool hdr = true) : IVideoPipeline
    {
        public string Id => id;
        public IReadOnlySet<string> OutputCodecs { get; } = new HashSet<string> { "h264" };
        public int MaxConcurrent => max;
        public bool AcceptsDecodedInput => false;
        public Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => Task.FromResult(works);
        public bool CanHandle(VideoJob job) => !job.Target.Copy && (hdr || !job.Source.IsHdr);
        public VideoArgs Build(VideoJob job) => new([], []);
    }

    private static readonly VideoSource Sdr = new("hevc", 1920, 1080, 8, null, null);
    private static readonly VideoSource Hdr = new("hevc", 3840, 2160, 10, "HDR10", null);
    private static readonly VideoTarget H264 = new(false, "h264", "ts", null);
    private static readonly VideoTarget Copy = new(true, "hevc", "mp4", null);
    private static readonly HashSet<string> None = [];

    private static VideoJob Job(VideoSource source, VideoTarget target) => new(source, target, VideoProcessing.Default, new SegmentTiming(6));

    private sealed class StaticMonitor(ServerOptions value) : IOptionsMonitor<ServerOptions>
    {
        public ServerOptions CurrentValue => value;
        public ServerOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ServerOptions, string?> listener) => null;
    }

    private static async Task<PipelineSelector> Selector(string[]? order, params IVideoPipeline[] pipelines)
    {
        var options = new StaticMonitor(new ServerOptions { Transcoding = { Pipelines = order } });
        var selector = new PipelineSelector([new CopyPipeline(), .. pipelines], [], options, NullLogger<PipelineSelector>.Instance);
        await selector.InitializeAsync(FakeFfmpeg.Full, default);
        return selector;
    }

    [Fact]
    public async Task ConfiguredOrderWinsAndUnlistedStayOff()
    {
        var s = await Selector(["hw", "sw"], new Fake("sw"), new Fake("hw"), new Fake("other"));
        Assert.Equal(["copy", "hw", "sw"], s.Status.Select(p => p.Id));
        using var lease = await s.AcquireAsync(Job(Sdr, H264), null, None, default);
        Assert.Equal("hw", lease!.Pipeline.Id);
    }

    [Fact]
    public async Task PipelinesThatFailTheProbeAreSkipped()
    {
        var s = await Selector(null, new Fake("hw", works: false), new Fake("sw"));
        Assert.True(s.IsAvailable);
        Assert.Equal(["copy", "sw"], s.Status.Select(p => p.Id));
    }

    [Fact]
    public async Task CopyTargetsAlwaysGoToCopyAndAreUnlimited()
    {
        var s = await Selector(null, new Fake("sw"));
        var leases = new List<PipelineLease>();
        for (var i = 0; i < 10; i++) leases.Add((await s.AcquireAsync(Job(Hdr, Copy), null, None, default))!);
        Assert.All(leases, l => Assert.Equal("copy", l.Pipeline.Id));
    }

    [Fact]
    public async Task FullPipelineSpillsOverToTheNextOne()
    {
        var s = await Selector(["hw", "sw"], new Fake("hw", max: 1), new Fake("sw", max: 1));
        using var first = await s.AcquireAsync(Job(Sdr, H264), null, None, default);
        using var second = await s.AcquireAsync(Job(Sdr, H264), null, None, default);
        Assert.Equal(("hw", "sw"), (first!.Pipeline.Id, second!.Pipeline.Id));

        first.Dispose();
        first.Dispose(); // releasing twice must not free two slots
        using var third = await s.AcquireAsync(Job(Sdr, H264), null, None, default);
        Assert.Equal("hw", third!.Pipeline.Id);
        Assert.Equal(1, s.Status.Single(p => p.Id == "hw").InUse);
    }

    [Fact]
    public async Task PreferredPipelineIsKeptAcrossRestarts()
    {
        var s = await Selector(["hw", "sw"], new Fake("hw", max: 5), new Fake("sw", max: 5));
        using var lease = await s.AcquireAsync(Job(Sdr, H264), "sw", None, default);
        Assert.Equal("sw", lease!.Pipeline.Id);
    }

    [Fact]
    public async Task SourcesAPipelineCannotHandleGoElsewhere()
    {
        var s = await Selector(["hw", "sw"], new Fake("hw", hdr: false), new Fake("sw"));
        using var lease = await s.AcquireAsync(Job(Hdr, H264), null, None, default);
        Assert.Equal("sw", lease!.Pipeline.Id);
    }

    [Fact]
    public async Task ExcludedAndRepeatedlyFailingPipelinesAreAvoided()
    {
        var s = await Selector(["hw", "sw"], new Fake("hw", max: 5), new Fake("sw", max: 5));
        using (var l = await s.AcquireAsync(Job(Sdr, H264), null, new HashSet<string> { "hw" }, default))
            Assert.Equal("sw", l!.Pipeline.Id);

        var broken = Guid.NewGuid();
        s.ReportFailure("hw", broken);
        s.ReportFailure("hw", broken);
        s.ReportFailure("hw", broken); // a client retrying one bad file
        Assert.Null(s.Status.Single(p => p.Id == "hw").DisabledUntil);
        s.ReportFailure("hw", Guid.NewGuid());
        s.ReportFailure("hw", Guid.NewGuid());
        Assert.NotNull(s.Status.Single(p => p.Id == "hw").DisabledUntil);
        using var benched = await s.AcquireAsync(Job(Sdr, H264), "hw", None, default);
        Assert.Equal("sw", benched!.Pipeline.Id);
    }

    [Fact]
    public async Task SuccessResetsTheFailureCount()
    {
        var s = await Selector(null, new Fake("hw"));
        s.ReportFailure("hw", Guid.NewGuid());
        s.ReportFailure("hw", Guid.NewGuid());
        s.ReportSuccess("hw");
        s.ReportFailure("hw", Guid.NewGuid());
        Assert.Null(s.Status.Single(p => p.Id == "hw").DisabledUntil);
    }

    [Fact]
    public async Task NothingSuitableReturnsNull()
    {
        var s = await Selector(null, new Fake("hw", hdr: false));
        Assert.Null(await s.AcquireAsync(Job(Hdr, H264), null, None, default));
        Assert.Equal(["h264"], s.EncodableCodecs);
    }
}
