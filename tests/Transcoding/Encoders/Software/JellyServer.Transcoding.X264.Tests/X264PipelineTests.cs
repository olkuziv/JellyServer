using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.Transcoding.X264.Tests;

public class X264PipelineTests
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

    // jellyfin-ffmpeg has tonemapx; distribution builds only zscale + tonemap.
    private static readonly Ffmpeg Jellyfin = new("libx264", "tonemapx", "zscale", "tonemap");
    private static readonly Ffmpeg Distro = new("libx264", "zscale", "tonemap");

    private static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 25);
    private static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 24);
    private static readonly VideoSource Interlaced = new("mpeg2video", 1920, 1080, 8, null, "Main", 25, Interlaced: true);
    private static readonly VideoTarget H264 = new(false, "h264", "ts", null);

    private const string Sdr = "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1 -pix_fmt yuv420p "
        + "-force_key_frames expr:gte(t,n_forced*6) -sc_threshold 0";
    private const string Hdr10In = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";

    private static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int seg = 6,
        DecodedVideo? decoded = null) => new(source, target ?? H264, processing ?? VideoProcessing.Default, new SegmentTiming(seg), decoded);

    private static (IVideoPipeline Pipeline, IConfigurationRoot Config) Create(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var p = new ServiceCollection().AddLogging().AddX264Pipeline(config).BuildServiceProvider().GetServices<IVideoPipeline>().Single();
        return (p, config);
    }

    private static IVideoPipeline Pipeline(params (string Key, string Value)[] settings) => Create(settings).Pipeline;

    private static async Task<IVideoPipeline> Probed(Ffmpeg ffmpeg, params (string Key, string Value)[] settings)
    {
        var p = Pipeline(settings);
        Assert.True(await p.ProbeAsync(ffmpeg, default));
        return p;
    }

    private static string Args(VideoArgs a) => string.Join(' ', a.Input.Concat(a.Output));

    /// <summary>The <c>-vf</c> value, or null without one.</summary>
    private static string? Filters(VideoArgs a) => a.Output.ToList().IndexOf("-vf") is var i and >= 0 ? a.Output[i + 1] : null;

    [Fact]
    public async Task NeedsLibx264()
    {
        Assert.False(await Pipeline().ProbeAsync(new Ffmpeg("zscale", "tonemap", "tonemapx"), default));
        Assert.True(await Pipeline().ProbeAsync(new Ffmpeg("libx264"), default));
    }

    [Fact]
    public void EncodesOnlyToH264()
    {
        var p = Pipeline();
        Assert.Equal("x264", p.Id);
        Assert.Equal(["h264"], p.OutputCodecs);
        Assert.True(p.AcceptsDecodedInput);
        Assert.True(p.CanHandle(Job(Hdr4k)));
        Assert.True(p.CanHandle(Job(Hdr4k, decoded: new DecodedVideo("vaapi", [], []))));
        Assert.False(p.CanHandle(Job(Hdr4k, H264 with { Codec = "hevc" })));
        Assert.False(p.CanHandle(Job(Hdr4k, H264 with { Copy = true })));
    }

    [Fact]
    public async Task Sdr1080NeedsNoFilters()
    {
        var args = (await Probed(Jellyfin)).Build(Job(Sdr1080));
        Assert.Empty(args.Input);
        Assert.Equal(Sdr, Args(args));
    }

    [Fact]
    public async Task Hdr4kIsScaledThenToneMappedWithTonemapx()
    {
        var args = (await Probed(Jellyfin)).Build(Job(Hdr4k));
        Assert.Equal($"{Hdr10In},scale=-2:1080,tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p", Filters(args));
        Assert.Contains("-level 4.1 -pix_fmt yuv420p", Args(args));
    }

    [Fact]
    public async Task WithoutTonemapxZscaleToneMaps()
    {
        var args = (await Probed(Distro)).Build(Job(Hdr4k));
        Assert.Equal($"{Hdr10In},scale=-2:1080,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,"
            + "zscale=t=bt709:m=bt709:r=tv", Filters(args));
    }

    [Fact]
    public async Task HlgIsTaggedAsHlg()
    {
        var args = (await Probed(Jellyfin)).Build(Job(Hdr4k with { HdrType = "HLG", Height = 1080, Width = 1920 }));
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,"
            + "tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p", Filters(args));
    }

    [Fact]
    public async Task ToneMappingFollowsTheDashboard()
    {
        var processing = new VideoProcessing { ToneMap = new ToneMapping(false, "mobius", "rgb", "pc", 0.5, 400, 0.3) };
        // Software encoders tone-map even when hardware tone mapping is off; the mode has no tonemapx option.
        Assert.EndsWith("tonemapx=tonemap=mobius:desat=0.5:peak=400:t=bt709:m=bt709:p=bt709:format=yuv420p:param=0.3:range=pc",
            Filters((await Probed(Jellyfin)).Build(Job(Hdr4k, processing: processing))));
        Assert.EndsWith("zscale=t=linear:npl=400,format=gbrpf32le,zscale=p=bt709,tonemap=mobius:desat=0.5:param=0.3,zscale=t=bt709:m=bt709:r=pc",
            Filters((await Probed(Distro)).Build(Job(Hdr4k, processing: processing))));
    }

    [Fact]
    public async Task WithoutToneMappingFiltersHdrIsOnlyScaled()
    {
        Assert.Equal("scale=-2:1080", Filters((await Probed(new Ffmpeg("libx264"))).Build(Job(Hdr4k))));
    }

    [Fact]
    public async Task InterlacedSourcesAreDeinterlacedFirst()
    {
        var p = await Probed(Jellyfin);
        Assert.Equal("yadif=0:-1:0", Filters(p.Build(Job(Interlaced))));

        var bwdif = new VideoProcessing { Deinterlace = new Deinterlacing("bwdif", DoubleRate: true) };
        Assert.Equal("bwdif=1:-1:0", Filters(p.Build(Job(Interlaced, processing: bwdif))));
        // Like Jellyfin: field rate only up to 30 fps, and not when the rate is unknown.
        Assert.Equal("bwdif=0:-1:0", Filters(p.Build(Job(Interlaced with { FrameRate = 50 }, processing: bwdif))));
        Assert.Equal("bwdif=0:-1:0", Filters(p.Build(Job(Interlaced with { FrameRate = 0 }, processing: bwdif))));

        var hdr = Filters(p.Build(Job(Hdr4k with { Interlaced = true })));
        Assert.StartsWith($"{Hdr10In},yadif=0:-1:0,scale=-2:1080,tonemapx=", hdr);
    }

    [Fact]
    public async Task DecodedFramesAreOnlyEncoded()
    {
        var decoded = new DecodedVideo("vaapi", ["-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi"],
            ["scale_vaapi=w=-2:h=1080:format=nv12", "hwdownload", "format=nv12"]);
        var args = (await Probed(Jellyfin)).Build(Job(Hdr4k with { Interlaced = true }, decoded: decoded));
        Assert.Equal(decoded.Input, args.Input);
        Assert.Equal("scale_vaapi=w=-2:h=1080:format=nv12,hwdownload,format=nv12,format=yuv420p", Filters(args));
    }

    [Fact]
    public async Task MaxHeightComesFromTheTarget()
    {
        var p = await Probed(Jellyfin);
        Assert.Equal("scale=-2:720", Filters(p.Build(Job(Sdr1080, H264 with { MaxHeight = 720 }))));
        var keep = Args(p.Build(Job(Hdr4k with { HdrType = null }, H264 with { MaxHeight = 0 })));
        Assert.DoesNotContain("-vf", keep);
        Assert.Contains("-level 5.1", keep);
        Assert.Contains("-level 5.2", Args(p.Build(Job(Hdr4k with { HdrType = null, FrameRate = 60 }, H264 with { MaxHeight = 0 }))));
        Assert.Null(Filters(p.Build(Job(Sdr1080 with { Height = 720, Width = 1280 }))));
    }

    [Fact]
    public async Task DashboardPresetAndCrfWin()
    {
        var p = await Probed(Jellyfin, ("X264:Preset", "fast"), ("X264:Crf", "21"));
        Assert.Contains("-preset fast -crf 21", Args(p.Build(Job(Sdr1080))));
        var dashboard = new VideoProcessing { Software = new SoftwareEncoding("slow", H264Crf: 18, H265Crf: 30) };
        Assert.Contains("-preset slow -crf 18", Args(p.Build(Job(Sdr1080, processing: dashboard))));
    }

    [Fact]
    public async Task ThreadsFollowJellyfin()
    {
        var p = await Probed(Jellyfin);
        Assert.DoesNotContain("-threads", Args(p.Build(Job(Sdr1080))));
        Assert.Contains("-threads 0", Args(p.Build(Job(Sdr1080, processing: new VideoProcessing { Software = new SoftwareEncoding(Threads: 0) }))));
        Assert.Contains("-threads 1", Args(p.Build(Job(Sdr1080, processing: new VideoProcessing { Software = new SoftwareEncoding(Threads: 1) }))));
        var many = Args(p.Build(Job(Sdr1080, processing: new VideoProcessing { Software = new SoftwareEncoding(Threads: 4096) })));
        Assert.Contains($"-threads {Environment.ProcessorCount}", many);
    }

    [Fact]
    public void KeyframesFollowTheSegmentLength()
    {
        var args = Args(Pipeline().Build(Job(Hdr4k, seg: 4)));
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*4) -sc_threshold 0", args);
    }

    [Fact]
    public void LegacyFlatSettingsStillApply()
    {
        var p = Pipeline(("H264Preset", "faster"), ("H264Crf", "20"), ("MaxConcurrentEncodes", "0"));
        Assert.Contains("-preset faster -crf 20", Args(p.Build(Job(Hdr4k))));
        Assert.Equal(1, p.MaxConcurrent); // the old setting never allowed "unlimited"
    }

    [Fact]
    public void X264SectionOverridesLegacySettings()
    {
        Assert.Equal(2, Pipeline().MaxConcurrent);
        var p = Pipeline(("H264Preset", "faster"), ("X264:Preset", "medium"), ("X264:Crf", "18"), ("X264:MaxConcurrent", "4"));
        Assert.Contains("-preset medium -crf 18", Args(p.Build(Job(Hdr4k))));
        Assert.Equal(4, p.MaxConcurrent);
    }

    [Fact]
    public void SettingsChangeAtRuntime()
    {
        var (p, config) = Create(("H264Crf", "20"));
        config["H264Crf"] = "25";
        config["X264:MaxConcurrent"] = "3";
        config.Reload();
        Assert.Contains("-crf 25", Args(p.Build(Job(Sdr1080))));
        Assert.Equal(3, p.MaxConcurrent);
    }

    [Fact]
    public void CapsBitrateBelowTheClientLimit()
    {
        Assert.Contains("-maxrate 40000000 -bufsize 80000000", Args(Pipeline().Build(Job(Hdr4k, H264 with { MaxBitrate = 100_000_000 }))));
        Assert.Contains("-maxrate 7200000 -bufsize 14400000", Args(Pipeline().Build(Job(Hdr4k, H264 with { MaxBitrate = 8_000_000 }))));
    }
}
