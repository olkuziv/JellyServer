using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.Transcoding.X265.Tests;

public class X265PipelineTests
{
    private sealed class Ffmpeg(params string[] features) : IFfmpegCapabilities
    {
        public string FfmpegPath => "ffmpeg";
        public bool HasEncoder(string name) => features.Contains(name);
        public bool HasDecoder(string name) => features.Contains(name);
        public bool HasFilter(string name) => features.Contains(name);
        public bool HasHwAccel(string name) => false;
        public Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(true);
    }

    private static readonly Ffmpeg Jellyfin = new("libx265", "tonemapx", "zscale", "tonemap");

    private static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 25);
    private static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 24);
    private static readonly VideoTarget Hevc = new(false, "hevc", "mp4", null);

    private const string Params = "no-scenecut=1:no-open-gop=1:no-info=1";
    private const string FastParams = ":subme=3:merange=25:rc-lookahead=10:me=star:ctu=32:max-tu-size=32:min-cu-size=16"
        + ":rskip=2:rskip-edge-threshold=2:no-sao=1:no-strong-intra-smoothing=1";

    private static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int seg = 6,
        DecodedVideo? decoded = null) => new(source, target ?? Hevc, processing ?? VideoProcessing.Default, new SegmentTiming(seg), decoded);

    private static IVideoPipeline Pipeline(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ServiceCollection().AddLogging().AddX265Pipeline(config).BuildServiceProvider().GetServices<IVideoPipeline>().Single();
    }

    private static async Task<IVideoPipeline> Probed(params (string Key, string Value)[] settings)
    {
        var p = Pipeline(settings);
        Assert.True(await p.ProbeAsync(Jellyfin, default));
        return p;
    }

    private static string Args(VideoArgs a) => string.Join(' ', a.Input.Concat(a.Output));
    private static string? Filters(VideoArgs a) => a.Output.ToList().IndexOf("-vf") is var i and >= 0 ? a.Output[i + 1] : null;

    [Fact]
    public async Task NeedsLibx265()
    {
        Assert.False(await Pipeline().ProbeAsync(new Ffmpeg("libx264", "tonemapx"), default));
        var p = await Probed();
        Assert.Equal("x265", p.Id);
        Assert.Equal(["hevc"], p.OutputCodecs);
        Assert.Equal(1, p.MaxConcurrent);
        Assert.True(p.AcceptsDecodedInput);
    }

    [Fact]
    public async Task EncodesOnlyToHevc()
    {
        var p = await Probed();
        Assert.True(p.CanHandle(Job(Hdr4k)));
        Assert.True(p.CanHandle(Job(Hdr4k, Hevc with { SegmentContainer = "ts" })));
        Assert.False(p.CanHandle(Job(Hdr4k, Hevc with { Codec = "h264" })));
        Assert.False(p.CanHandle(Job(Hdr4k, Hevc with { Copy = true })));
    }

    [Fact]
    public async Task Sdr1080InFmp4()
    {
        var args = (await Probed()).Build(Job(Sdr1080));
        Assert.Empty(args.Input);
        Assert.Equal($"-c:v libx265 -preset veryfast -crf 28 -profile:v main -pix_fmt yuv420p -x265-params {Params}{FastParams} "
            + "-force_key_frames expr:gte(t,n_forced*6) -forced-idr 1 -tag:v hvc1", Args(args));
    }

    [Fact]
    public async Task HvcTagOnlyForFmp4()
    {
        Assert.DoesNotContain("hvc1", Args((await Probed()).Build(Job(Sdr1080, Hevc with { SegmentContainer = "ts" }))));
    }

    [Fact]
    public async Task Hdr4kIsScaledAndToneMapped()
    {
        var args = (await Probed()).Build(Job(Hdr4k, processing: new VideoProcessing { ToneMap = new ToneMapping(Enabled: false) }));
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=-2:1080,"
            + "tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p", Filters(args));
    }

    [Fact]
    public async Task InterlacedAndDecodedInputs()
    {
        var p = await Probed();
        var interlaced = Sdr1080 with { Interlaced = true };
        Assert.Equal("bwdif=1:-1:0", Filters(p.Build(Job(interlaced,
            processing: new VideoProcessing { Deinterlace = new Deinterlacing("bwdif", true) }))));

        var decoded = new DecodedVideo("nvenc", ["-hwaccel", "cuda"], ["scale_cuda=-2:1080:format=nv12", "hwdownload", "format=nv12"]);
        var args = p.Build(Job(Hdr4k with { Interlaced = true }, decoded: decoded));
        Assert.Equal(["-hwaccel", "cuda"], args.Input);
        Assert.Equal("scale_cuda=-2:1080:format=nv12,hwdownload,format=nv12,format=yuv420p", Filters(args));
    }

    [Fact]
    public async Task DashboardSettingsWin()
    {
        var p = await Probed(("X265:Preset", "faster"), ("X265:Crf", "26"), ("X265:MaxConcurrent", "2"));
        Assert.Equal(2, p.MaxConcurrent);
        Assert.Contains("-preset faster -crf 26", Args(p.Build(Job(Sdr1080))));

        var dashboard = new VideoProcessing { Software = new SoftwareEncoding("ultrafast", H264Crf: 18, H265Crf: 30, Threads: 0) };
        var args = Args(p.Build(Job(Sdr1080, processing: dashboard)));
        Assert.Contains("-preset ultrafast -crf 30", args);
        Assert.Contains($"-x265-params {Params} ", args); // Jellyfin keeps ultrafast as it is
        Assert.Contains("-threads 0", args);
    }

    [Fact]
    public async Task KeyframesAndBitrateCap()
    {
        var args = Args((await Probed()).Build(Job(Sdr1080, Hevc with { MaxBitrate = 10_000_000 }, seg: 3)));
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*3) -forced-idr 1", args);
        Assert.EndsWith("-maxrate 9000000 -bufsize 18000000", args);
    }
}
