using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.Transcoding.SvtAv1.Tests;

public class SvtAv1PipelineTests
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

    private static readonly Ffmpeg Jellyfin = new("libsvtav1", "tonemapx", "zscale", "tonemap");

    private static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 25);
    private static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 24);
    private static readonly VideoTarget Av1 = new(false, "av1", "mp4", null);

    private static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int seg = 6,
        DecodedVideo? decoded = null) => new(source, target ?? Av1, processing ?? VideoProcessing.Default, new SegmentTiming(seg), decoded);

    private static VideoProcessing Preset(string preset) => new() { Software = new SoftwareEncoding(preset) };

    private static IVideoPipeline Pipeline(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ServiceCollection().AddLogging().AddSvtAv1Pipeline(config).BuildServiceProvider().GetServices<IVideoPipeline>().Single();
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
    public async Task NeedsLibsvtav1()
    {
        Assert.False(await Pipeline().ProbeAsync(new Ffmpeg("libx264", "libaom-av1"), default));
        var p = await Probed();
        Assert.Equal("svtav1", p.Id);
        Assert.Equal(["av1"], p.OutputCodecs);
        Assert.Equal(1, p.MaxConcurrent);
        Assert.True(p.AcceptsDecodedInput);
    }

    [Fact]
    public async Task EncodesAv1IntoFmp4Only()
    {
        var p = await Probed();
        Assert.True(p.CanHandle(Job(Hdr4k)));
        Assert.False(p.CanHandle(Job(Hdr4k, Av1 with { SegmentContainer = "ts" })));
        Assert.False(p.CanHandle(Job(Hdr4k, Av1 with { Codec = "hevc" })));
        Assert.False(p.CanHandle(Job(Hdr4k, Av1 with { Copy = true })));
    }

    [Fact]
    public async Task Sdr1080WithDefaults()
    {
        var args = (await Probed()).Build(Job(Sdr1080));
        Assert.Empty(args.Input);
        Assert.Equal("-c:v libsvtav1 -preset 10 -crf 28 -pix_fmt yuv420p -svtav1-params tune=0:film-grain=0:enable-overlays=1:enable-tf=0 "
            + "-force_key_frames expr:gte(t,n_forced*6)", Args(args));
    }

    [Theory]
    [InlineData("auto", "10")]
    [InlineData("veryslow", "5")]
    [InlineData("slower", "6")]
    [InlineData("slow", "7")]
    [InlineData("medium", "8")]
    [InlineData("fast", "9")]
    [InlineData("faster", "10")]
    [InlineData("veryfast", "11")]
    [InlineData("superfast", "12")]
    [InlineData("ultrafast", "13")]
    public async Task PresetsFollowJellyfin(string preset, string svt)
    {
        Assert.Contains($"-preset {svt} ", Args((await Probed()).Build(Job(Sdr1080, processing: Preset(preset)))));
    }

    [Fact]
    public async Task CrfComesFromTheH265Setting()
    {
        var p = await Probed(("SvtAv1:Preset", "8"), ("SvtAv1:Crf", "35"), ("SvtAv1:MaxConcurrent", "2"));
        Assert.Equal(2, p.MaxConcurrent);
        Assert.Contains("-preset 8 -crf 35", Args(p.Build(Job(Sdr1080))));
        var dashboard = new VideoProcessing { Software = new SoftwareEncoding(H264Crf: 20, H265Crf: 30, Threads: 2) };
        var args = Args(p.Build(Job(Sdr1080, processing: dashboard)));
        Assert.Contains("-preset 8 -crf 30", args);
        Assert.Contains($"-threads {Math.Min(2, Environment.ProcessorCount)}", args);
    }

    [Fact]
    public async Task Hdr4kInterlacedIsDeinterlacedScaledAndToneMapped()
    {
        var args = (await Probed()).Build(Job(Hdr4k with { Interlaced = true }));
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,yadif=0:-1:0,scale=-2:1080,"
            + "tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p", Filters(args));
    }

    [Fact]
    public async Task DecodedFramesAreOnlyEncoded()
    {
        var decoded = new DecodedVideo("qsv", ["-hwaccel", "qsv"], ["vpp_qsv=w=1920:h=1080:format=nv12", "hwdownload", "format=nv12"]);
        var args = (await Probed()).Build(Job(Hdr4k, decoded: decoded));
        Assert.Equal(["-hwaccel", "qsv"], args.Input);
        Assert.Equal("vpp_qsv=w=1920:h=1080:format=nv12,hwdownload,format=nv12,format=yuv420p", Filters(args));
    }

    [Fact]
    public async Task KeyframesAndCappedCrf()
    {
        var args = Args((await Probed()).Build(Job(Sdr1080, Av1 with { MaxBitrate = 100_000_000 }, seg: 4)));
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*4)", args);
        Assert.EndsWith("-maxrate 40000000 -bufsize 80000000", args);
    }
}
