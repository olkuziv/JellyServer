using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.Transcoding.Qsv.Tests;

public class QsvPipelineTests
{
    private static readonly string[] AllFeatures =
    [
        "qsv", "vaapi", "opencl", "h264_qsv", "hevc_qsv", "av1_qsv", "mpeg2_qsv", "vc1_qsv", "vp8_qsv", "vp9_qsv",
        "vpp_qsv", "hwupload", "hwdownload", "hwmap", "scale_vaapi", "procamp_vaapi", "tonemap_vaapi", "tonemap_opencl",
    ];

    /// <summary>A jellyfin-ffmpeg with Quick Sync; test runs fail when their arguments contain any of <c>failing</c>.</summary>
    private sealed class Ffmpeg(string[]? features = null, params string[] failing) : IFfmpegCapabilities
    {
        private readonly string[] _features = features ?? AllFeatures;

        public List<string> Runs { get; } = [];
        public string FfmpegPath => "ffmpeg";
        public bool HasEncoder(string name) => _features.Contains(name);
        public bool HasDecoder(string name) => _features.Contains(name);
        public bool HasFilter(string name) => _features.Contains(name);
        public bool HasHwAccel(string name) => _features.Contains(name);

        public Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            var line = string.Join(' ', args);
            Runs.Add(line);
            return Task.FromResult(!failing.Any(line.Contains));
        }
    }

    private const string Devices = "-init_hw_device vaapi=va:/dev/dri/renderD128,driver=iHD -init_hw_device qsv=qs@va";
    private const string Sdr = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709";
    private const string Hdr = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";
    private const string Ocl = "tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0";

    private static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 23.976);
    private static readonly VideoSource Hevc4k10 = new("hevc", 3840, 2160, 10, null, "Main 10", 24);
    private static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 24);
    private static readonly VideoSource Mpeg4 = new("mpeg4", 720, 480, 8, null, "Advanced Simple Profile", 25);
    private static readonly VideoTarget H264 = new(false, "h264", "ts", null);

    /// <summary>Dashboard defaults with Jellyfin's EncodingOptions values (VPP tone mapping off, native decoder preferred).</summary>
    private static readonly VideoProcessing Defaults = new();
    private static readonly VideoProcessing QsvDecoders = Defaults with { HwDecode = new HardwareDecoding(PreferNativeDecoder: false) };

    private static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int segment = 6) =>
        new(source, target ?? H264, processing ?? Defaults, new SegmentTiming(segment));

    private sealed record Module(IVideoPipeline Pipeline, IVideoDecoder Decoder, IConfigurationRoot Config);

    private static Module Create(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var sp = new ServiceCollection().AddLogging().AddQsvPipeline(config).BuildServiceProvider();
        return new Module(sp.GetServices<IVideoPipeline>().Single(), sp.GetServices<IVideoDecoder>().Single(), config);
    }

    private static async Task<IVideoPipeline> Probed(Ffmpeg? ffmpeg = null, params (string Key, string Value)[] settings)
    {
        var m = Create(settings);
        Assert.True(await m.Pipeline.ProbeAsync(ffmpeg ?? new Ffmpeg(), default));
        return m.Pipeline;
    }

    private static async Task<IVideoDecoder> ProbedDecoder(Ffmpeg? ffmpeg = null)
    {
        var m = Create();
        Assert.True(await m.Decoder.ProbeAsync(ffmpeg ?? new Ffmpeg(), default));
        return m.Decoder;
    }

    private static string Args(IReadOnlyList<string> a) => string.Join(' ', a);
    private static string Vf(VideoArgs a) => a.Output[1];

    // ---------------------------------------------------------------- registration and probing

    [Fact]
    public void PipelineAndDecoderAreOneInstanceWithTheSameId()
    {
        var m = Create();
        Assert.Same(m.Pipeline, m.Decoder);
        Assert.Equal("qsv", m.Pipeline.Id);
        Assert.Equal("qsv", m.Decoder.Id);
        Assert.False(m.Pipeline.AcceptsDecodedInput);
        Assert.Empty(m.Pipeline.OutputCodecs);
    }

    [Fact]
    public async Task NeedsTheHwAccelAndVppFilters()
    {
        foreach (var missing in new[] { "qsv", "vpp_qsv", "hwupload", "hwdownload" })
        {
            var ffmpeg = new Ffmpeg(AllFeatures.Where(f => f != missing).ToArray());
            var m = Create();
            Assert.False(await m.Pipeline.ProbeAsync(ffmpeg, default));
            Assert.False(await m.Decoder.ProbeAsync(ffmpeg, default));
            Assert.Empty(ffmpeg.Runs);
        }
    }

    [Fact]
    public async Task ADeadDeviceFailsAfterOneRun()
    {
        var ffmpeg = new Ffmpeg(null, "vpp_qsv=w=640");
        var m = Create();
        Assert.False(await m.Pipeline.ProbeAsync(ffmpeg, default));
        Assert.False(await m.Decoder.ProbeAsync(ffmpeg, default));
        Assert.Single(ffmpeg.Runs);
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080)));
        Assert.False(m.Decoder.CanDecode(Job(Sdr1080)));
    }

    [Fact]
    public async Task ProbeRunsRealFramesOnTheDevice()
    {
        var ffmpeg = new Ffmpeg();
        await Probed(ffmpeg);
        Assert.Equal($"-hide_banner -loglevel error -nostdin {Devices} -filter_hw_device qs -f lavfi -i testsrc2=size=1280x720:rate=25 " +
            "-frames:v 1 -vf format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=w=640:h=360:format=nv12,hwdownload,format=nv12 -f null -", ffmpeg.Runs[0]);
        Assert.Contains($"-hide_banner -loglevel error -nostdin {Devices} -filter_hw_device qs -f lavfi -i testsrc2=size=1280x720:rate=25 " +
            "-frames:v 1 -vf format=nv12,hwupload=extra_hw_frames=64 -c:v h264_qsv -preset veryfast -global_quality 23 -f null -", ffmpeg.Runs);
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-c:v hevc_qsv"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-c:v av1_qsv"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("vpp_qsv=tonemap=1:format=nv12"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("hwupload=derive_device=vaapi,scale_vaapi=") && r.Contains("hwmap=derive_device=qsv"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("tonemap_vaapi=format=nv12"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-init_hw_device opencl=ocl@va") && r.Contains(Ocl));
    }

    [Fact]
    public async Task OffersOnlyCodecsWhoseTestEncodeWorks()
    {
        var p = await Probed(new Ffmpeg(null, "hevc_qsv", "av1_qsv"));
        Assert.Equal(["h264"], p.OutputCodecs);
        Assert.True(p.CanHandle(Job(Sdr1080)));
        Assert.False(p.CanHandle(Job(Sdr1080, H264 with { Codec = "hevc" })));
        Assert.False(p.CanHandle(Job(Sdr1080, H264 with { Copy = true })));

        var all = await Probed();
        Assert.Equal(["av1", "h264", "hevc"], all.OutputCodecs.Order());
        Assert.False(all.CanHandle(Job(Sdr1080, H264 with { Codec = "av1" }))); // AV1 only in fMP4
        Assert.True(all.CanHandle(Job(Sdr1080, H264 with { Codec = "av1", SegmentContainer = "mp4" })));
        Assert.Contains("-c:v av1_qsv -preset veryfast -profile:v main -adaptive_i 0",
            Args(all.Build(Job(Sdr1080, H264 with { Codec = "av1", SegmentContainer = "mp4" })).Output));
    }

    [Fact]
    public async Task EncoderThatOnlyWorksInLowPowerModeGetsIt()
    {
        // Normal mode fails for hevc only.
        var ffmpeg = new Ffmpeg(null, "hevc_qsv -preset veryfast -global_quality");
        var p = await Probed(ffmpeg);
        Assert.Contains("hevc", p.OutputCodecs);
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-c:v hevc_qsv -preset veryfast -low_power 1 -global_quality 23"));
        Assert.Contains("-low_power 1", Args(p.Build(Job(Sdr1080, H264 with { Codec = "hevc" })).Output));
        Assert.DoesNotContain("-low_power", Args(p.Build(Job(Sdr1080)).Output));
        // An explicit dashboard "off" wins over the probe; "on" forces it.
        var off = Defaults with { HwEncode = new HardwareEncoding(LowPowerH264: true, LowPowerHevc: false) };
        Assert.DoesNotContain("-low_power", Args(p.Build(Job(Sdr1080, H264 with { Codec = "hevc" }, off)).Output));
        Assert.Contains("-profile:v high -low_power 1 -adaptive_i 0", Args(p.Build(Job(Sdr1080, null, off)).Output));
    }

    [Fact]
    public async Task DecoderProbeReusesTheJustMadePipelineProbe()
    {
        var ffmpeg = new Ffmpeg();
        var m = Create();
        Assert.True(await m.Pipeline.ProbeAsync(ffmpeg, default));
        var runs = ffmpeg.Runs.Count;
        Assert.True(await m.Decoder.ProbeAsync(ffmpeg, default));
        Assert.Equal(runs, ffmpeg.Runs.Count);

        // Another ffmpeg (a new detection) probes again.
        var other = new Ffmpeg();
        Assert.True(await m.Decoder.ProbeAsync(other, default));
        Assert.NotEmpty(other.Runs);
    }

    [Fact]
    public async Task DecoderWorksWithoutAnyEncoder()
    {
        var m = Create();
        var ffmpeg = new Ffmpeg(null, "-c:v");
        Assert.False(await m.Pipeline.ProbeAsync(ffmpeg, default));
        Assert.True(await m.Decoder.ProbeAsync(new Ffmpeg(null, "-c:v"), default));
        Assert.True(m.Decoder.CanDecode(Job(Sdr1080)));
    }

    [Fact]
    public async Task DeviceComesFromSettingsAndChangesAtRuntime()
    {
        var m = Create(("Qsv:Device", "/dev/dri/renderD129"));
        var ffmpeg = new Ffmpeg();
        await m.Pipeline.ProbeAsync(ffmpeg, default);
        Assert.All(ffmpeg.Runs, r => Assert.Contains("vaapi=va:/dev/dri/renderD129,driver=iHD", r));
        Assert.StartsWith("-init_hw_device vaapi=va:/dev/dri/renderD129,driver=iHD", Args(m.Pipeline.Build(Job(Sdr1080)).Input));

        m.Config["Qsv:Device"] = "";
        m.Config.Reload();
        var again = new Ffmpeg();
        await m.Pipeline.ProbeAsync(again, default);
        Assert.All(again.Runs, r => Assert.Contains("vaapi=va:/dev/dri/renderD128,driver=iHD", r)); // empty → default node
        Assert.StartsWith(Devices, Args(m.Pipeline.Build(Job(Sdr1080)).Input));
    }

    [Fact]
    public async Task QsvSectionConfiguresPresetQualityAndConcurrency()
    {
        Assert.Equal(4, Create().Pipeline.MaxConcurrent);
        var p = await Probed(null, ("Qsv:Preset", "medium"), ("Qsv:GlobalQuality", "20"), ("Qsv:MaxConcurrent", "2"));
        Assert.Equal(2, p.MaxConcurrent);
        var args = Args(p.Build(Job(Sdr1080)).Output);
        Assert.Contains("-c:v h264_qsv -preset medium", args);
        Assert.Contains("-global_quality 20", args);
    }

    // ---------------------------------------------------------------- decoding

    [Fact]
    public async Task NativeVaapiDecodingIsMappedToQsvByDefault()
    {
        var args = (await Probed()).Build(Job(Sdr1080));
        Assert.Equal($"{Devices} -filter_hw_device qs -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi", Args(args.Input));
        Assert.Equal($"-vf {Sdr},scale_vaapi=format=nv12:extra_hw_frames=24,hwmap=derive_device=qsv,format=qsv " +
            "-c:v h264_qsv -preset veryfast -profile:v high -adaptive_i 0 -adaptive_b 0 -global_quality 23 " +
            "-force_key_frames expr:gte(t,n_forced*6) -forced_idr 1", Args(args.Output));
    }

    [Fact]
    public async Task BaselineH264AllowsAProfileMismatchOnVaapi()
    {
        var args = (await Probed()).Build(Job(Sdr1080 with { Profile = "Baseline" }));
        Assert.EndsWith("-hwaccel_output_format vaapi -hwaccel_flags +allow_profile_mismatch", Args(args.Input));
    }

    [Fact]
    public async Task QsvDecodersWhenTheNativeDecoderIsNotPreferred()
    {
        var args = (await Probed()).Build(Job(Sdr1080, null, QsvDecoders));
        Assert.Equal($"{Devices} -filter_hw_device qs -hwaccel qsv -hwaccel_device qs -hwaccel_output_format qsv -c:v h264_qsv", Args(args.Input));
        Assert.StartsWith($"-vf {Sdr},vpp_qsv=format=nv12 -c:v h264_qsv", Args(args.Output));

        var hevc = (await Probed()).Build(Job(Hevc4k10, null, QsvDecoders));
        Assert.EndsWith("-c:v hevc_qsv", Args(hevc.Input));
        Assert.Equal($"{Sdr},vpp_qsv=w=1920:h=1080:format=nv12", Vf(hevc));
    }

    [Fact]
    public async Task FallsBackToQsvDecodersWhenNativeVaapiDoesNotWork()
    {
        var p = await Probed(new Ffmpeg(null, "scale_vaapi"));
        Assert.Contains("-hwaccel qsv", Args(p.Build(Job(Sdr1080)).Input));
        var noVaapi = await Probed(new Ffmpeg(AllFeatures.Where(f => f != "vaapi").ToArray()));
        Assert.EndsWith("-c:v h264_qsv", Args(noVaapi.Build(Job(Sdr1080)).Input));
        // Without a *_qsv decoder either, decode on the CPU.
        var none = await Probed(new Ffmpeg(AllFeatures.Where(f => f is not "vaapi" and not "vc1_qsv").ToArray()));
        Assert.DoesNotContain("-hwaccel", Args(none.Build(Job(new VideoSource("vc1", 1920, 1080, 8, null, "Advanced"))).Input));
    }

    [Fact]
    public async Task HardwareDecodingFollowsTheDashboardCodecList()
    {
        var p = await Probed();
        var onlyHevc = Defaults with { HwDecode = new HardwareDecoding(new HashSet<string> { "hevc" }) };
        Assert.DoesNotContain("-hwaccel", Args(p.Build(Job(Sdr1080, null, onlyHevc)).Input));
        Assert.Contains("-hwaccel vaapi", Args(p.Build(Job(Hevc4k10, null, onlyHevc)).Input));
        Assert.DoesNotContain("-hwaccel", Args(p.Build(Job(Mpeg4)).Input)); // no QSV decoder for MPEG-4 Part 2
    }

    [Theory]
    [InlineData("hevc", 10, "Main 10", true, true, false, false, true)]
    [InlineData("hevc", 10, "Main 10", false, true, false, false, false)]
    [InlineData("vp9", 10, "Profile 2", true, true, false, false, true)]
    [InlineData("vp9", 10, "Profile 2", true, false, false, false, false)]
    [InlineData("vp9", 8, "Profile 1", true, true, false, false, false)]
    [InlineData("hevc", 8, "Rext", true, true, false, false, false)]
    [InlineData("hevc", 10, "Rext", true, true, true, false, true)]
    [InlineData("hevc", 12, "Main 12", true, true, true, false, false)]
    [InlineData("hevc", 12, "Main 12", true, true, false, true, true)]
    [InlineData("h264", 10, "High 10", true, true, true, true, false)]
    [InlineData("h264", 8, "High 4:4:4 Predictive", true, true, true, true, false)]
    [InlineData("av1", 10, "Main", true, true, true, true, true)]
    [InlineData("av1", 8, "High", true, true, true, true, false)]
    public async Task BitDepthAndRangeExtensionSwitchesGateGpuDecoding(string codec, int depth, string profile,
        bool hevc10, bool vp910, bool rext10, bool rext12, bool gpu)
    {
        var p = await Probed();
        var hw = new HardwareDecoding(null, hevc10, vp910, rext10, rext12);
        foreach (var native in new[] { true, false })
        {
            var job = Job(new VideoSource(codec, 1920, 1080, depth, null, profile), null, Defaults with { HwDecode = hw with { PreferNativeDecoder = native } });
            Assert.Equal(gpu, Args(p.Build(job).Input).Contains("-hwaccel"));
        }
    }

    [Fact]
    public async Task SoftwareDecodedSourcesAreScaledAndUploaded()
    {
        var p = await Probed();
        var args = p.Build(Job(Mpeg4));
        Assert.Equal($"{Devices} -filter_hw_device qs", Args(args.Input));
        Assert.Equal($"{Sdr},format=nv12,hwupload=extra_hw_frames=64", Vf(args));

        var hi10 = p.Build(Job(new VideoSource("h264", 3840, 1600, 10, null, "High 10")));
        Assert.DoesNotContain("-hwaccel", Args(hi10.Input));
        Assert.Equal($"{Sdr},scale=2592:1080,format=nv12,hwupload=extra_hw_frames=64", Vf(hi10));
    }

    // ---------------------------------------------------------------- scaling and deinterlacing

    [Fact]
    public async Task ScalesToTheTargetMaxHeight()
    {
        var p = await Probed();
        Assert.Equal($"{Sdr},scale_vaapi=w=1280:h=720:format=nv12:extra_hw_frames=24,hwmap=derive_device=qsv,format=qsv",
            Vf(p.Build(Job(Sdr1080, H264 with { MaxHeight = 720 }))));
        Assert.Equal($"{Sdr},vpp_qsv=format=nv12",
            Vf(p.Build(Job(Hevc4k10, H264 with { MaxHeight = 0 }, QsvDecoders))));
        Assert.Equal($"{Sdr},vpp_qsv=w=1280:h=720:format=nv12", Vf(p.Build(Job(Hevc4k10, H264 with { MaxHeight = 720 }, QsvDecoders))));
    }

    [Fact]
    public async Task InterlacedSourcesAreDeinterlacedOnTheGpu()
    {
        var p = await Probed();
        var i25 = Sdr1080 with { Interlaced = true, FrameRate = 25 };
        var doubled = Defaults with { Deinterlace = new Deinterlacing("bwdif", true) };

        Assert.Equal($"{Sdr},vpp_qsv=deinterlace=advanced:format=nv12", Vf(p.Build(Job(i25, null, QsvDecoders))));
        Assert.Equal($"{Sdr},vpp_qsv=deinterlace=advanced:rate=field:format=nv12",
            Vf(p.Build(Job(i25, null, doubled with { HwDecode = QsvDecoders.HwDecode }))));
        // Double rate only up to 30 fps (and not for an unknown rate), as in Jellyfin.
        Assert.Equal($"{Sdr},vpp_qsv=deinterlace=advanced:format=nv12",
            Vf(p.Build(Job(i25 with { FrameRate = 50 }, null, doubled with { HwDecode = QsvDecoders.HwDecode }))));
        Assert.Equal($"{Sdr},vpp_qsv=deinterlace=advanced:format=nv12",
            Vf(p.Build(Job(i25 with { FrameRate = 0 }, null, doubled with { HwDecode = QsvDecoders.HwDecode }))));

        Assert.StartsWith($"{Sdr},deinterlace_vaapi=rate=frame,scale_vaapi=format=nv12", Vf(p.Build(Job(i25))));
        Assert.StartsWith($"{Sdr},deinterlace_vaapi=rate=field,scale_vaapi=format=nv12", Vf(p.Build(Job(i25, null, doubled))));

        var mpeg4i = Mpeg4 with { Interlaced = true };
        Assert.Equal($"{Sdr},yadif=0:-1:0,format=nv12,hwupload=extra_hw_frames=64", Vf(p.Build(Job(mpeg4i))));
        Assert.Equal($"{Sdr},bwdif=1:-1:0,format=nv12,hwupload=extra_hw_frames=64", Vf(p.Build(Job(mpeg4i, null, doubled))));
    }

    // ---------------------------------------------------------------- tone mapping

    private static readonly VideoProcessing Vpp = Defaults with { VppToneMap = new VppToneMapping(true, 0, 1) };
    private static readonly VideoProcessing VppProcamp = Defaults with { VppToneMap = new VppToneMapping(true, 16, 1.5) };

    [Fact]
    public async Task VppToneMappingOnQsvSurfacesFollowsJellyfin()
    {
        var p = await Probed();
        Assert.Equal($"{Hdr},vpp_qsv=w=1920:h=1080:format=nv12:tonemap=1,{Sdr}",
            Vf(p.Build(Job(Hdr4k, null, Vpp with { HwDecode = QsvDecoders.HwDecode }))));
        // ProcAmp needs a second pass: brightness/contrast while scaling to p010, then tone mapping.
        Assert.Equal($"{Hdr},vpp_qsv=w=1920:h=1080:format=p010:brightness=16:contrast=1.5:procamp=1:async_depth=2," +
            $"vpp_qsv=tonemap=1:format=nv12:async_depth=2,{Sdr}",
            Vf(p.Build(Job(Hdr4k, null, VppProcamp with { HwDecode = QsvDecoders.HwDecode }))));
        // Brightness 0 and contrast ≤ 1 are left out, like Jellyfin.
        Assert.Equal($"{Hdr},vpp_qsv=w=1920:h=1080:format=p010:contrast=2:procamp=1:async_depth=2,vpp_qsv=tonemap=1:format=nv12:async_depth=2,{Sdr}",
            Vf(p.Build(Job(Hdr4k, null, Vpp with { VppToneMap = new(true, 0, 2), HwDecode = QsvDecoders.HwDecode }))));
    }

    [Fact]
    public async Task VppToneMappingOnNativeVaapiSurfacesUsesTonemapVaapi()
    {
        var p = await Probed();
        Assert.Equal($"{Hdr},scale_vaapi=w=1920:h=1080:extra_hw_frames=24,procamp_vaapi=b=16:c=1.5," +
            "tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32,hwmap=derive_device=qsv,format=qsv",
            Vf(p.Build(Job(Hdr4k, null, VppProcamp))));
        Assert.Equal($"{Hdr},scale_vaapi=w=1920:h=1080:extra_hw_frames=24," +
            "tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32,hwmap=derive_device=qsv,format=qsv",
            Vf(p.Build(Job(Hdr4k, null, Vpp))));
    }

    [Fact]
    public async Task OpenClToneMappingWhenVppIsOff()
    {
        var p = await Probed();
        var vaapi = p.Build(Job(Hdr4k));
        Assert.Equal($"{Devices} -init_hw_device opencl=ocl@va -filter_hw_device qs -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi",
            Args(vaapi.Input));
        Assert.Equal($"{Hdr},scale_vaapi=w=1920:h=1080:extra_hw_frames=24,hwmap=derive_device=opencl:mode=read,{Ocl}," +
            "hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16,format=qsv", Vf(vaapi));

        Assert.Equal($"{Hdr},vpp_qsv=w=1920:h=1080,hwmap=derive_device=opencl:mode=read,{Ocl}," +
            "hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16,format=qsv", Vf(p.Build(Job(Hdr4k, null, QsvDecoders))));

        // Software decoding uploads to OpenCL; the encoder takes the result from system memory.
        var sw = p.Build(Job(Hdr4k, null, Defaults with { HwDecode = new HardwareDecoding(new HashSet<string>()) }));
        Assert.Equal($"{Devices} -init_hw_device opencl=ocl@va -filter_hw_device ocl", Args(sw.Input));
        Assert.Equal($"{Hdr},scale=1920:1080,format=yuv420p10le,hwupload=derive_device=opencl,{Ocl},hwmap=mode=read,format=nv12", Vf(sw));
    }

    [Fact]
    public async Task OpenClToneMappingUsesEveryDashboardSetting()
    {
        var p = await Probed();
        var tm = Defaults with { ToneMap = new ToneMapping(true, "hable", "itp", "tv", 0.5, 400, 0.3) };
        Assert.Contains("tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=hable:peak=400:desat=0.5:tonemap_mode=itp:param=0.3:range=tv",
            Vf(p.Build(Job(Hdr4k, null, tm))));
        Assert.Contains($"{Ocl}:range=pc", Vf(p.Build(Job(Hdr4k, null, Defaults with { ToneMap = new ToneMapping(Range: "pc") }))));
        Assert.DoesNotContain("tonemap_mode", Vf(p.Build(Job(Hdr4k)))); // auto
    }

    [Fact]
    public async Task HlgAndRangeExtensionsTakeTheRightToneMapper()
    {
        var p = await Probed();
        var both = Vpp with { ToneMap = new ToneMapping(true) };
        var hlg = Hdr4k with { HdrType = "HLG" };
        // VPP tone-maps HDR10 only: HLG goes to OpenCL.
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,scale_vaapi=w=1920:h=1080:extra_hw_frames=24," +
            $"hwmap=derive_device=opencl:mode=read,{Ocl},hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16,format=qsv",
            Vf(p.Build(Job(hlg, null, both))));
        // HEVC RExt scales to p010 before tone mapping.
        var rext = new VideoSource("hevc", 3840, 2160, 10, "HDR10", "Rext");
        var hw = new HardwareDecoding(HevcRext10Bit: true, PreferNativeDecoder: false);
        Assert.Equal($"{Hdr},vpp_qsv=w=1920:h=1080:format=p010,vpp_qsv=tonemap=1:format=nv12:async_depth=2,{Sdr}",
            Vf(p.Build(Job(rext, null, Vpp with { HwDecode = hw }))));
        Assert.StartsWith($"{Hdr},vpp_qsv=w=1920:h=1080:format=p010,hwmap=derive_device=opencl", Vf(p.Build(Job(rext, null, Defaults with { HwDecode = hw }))));
    }

    [Fact]
    public async Task HdrPassesUntouchedWhenToneMappingIsOff()
    {
        var p = await Probed(new Ffmpeg(null, "tonemap"));
        var off = Defaults with { ToneMap = new ToneMapping(false) };
        Assert.True(p.CanHandle(Job(Hdr4k, null, off)));
        Assert.Equal($"{Sdr},scale_vaapi=w=1920:h=1080:format=nv12:extra_hw_frames=24,hwmap=derive_device=qsv,format=qsv", Vf(p.Build(Job(Hdr4k, null, off))));
    }

    [Fact]
    public async Task ToneMappingThatIsWantedButImpossibleLeavesTheJobToOthers()
    {
        var noOcl = await Probed(new Ffmpeg(null, "tonemap_opencl"));
        Assert.False(noOcl.CanHandle(Job(Hdr4k)));                                     // ToneMap on, no OpenCL
        Assert.True(noOcl.CanHandle(Job(Hdr4k, null, Vpp with { ToneMap = new(false) })));    // VPP works

        var nothing = await Probed(new Ffmpeg(null, "tonemap"));
        Assert.False(nothing.CanHandle(Job(Hdr4k, null, Vpp with { ToneMap = new(false) }))); // VPP wanted but broken
        Assert.False(nothing.CanHandle(Job(Hdr4k)));

        // VPP alone cannot tone-map HLG or software-decoded HDR.
        var p = await Probed();
        var vppOnly = Vpp with { ToneMap = new(false) };
        Assert.False(p.CanHandle(Job(Hdr4k with { HdrType = "HLG" }, null, vppOnly)));
        Assert.False(p.CanHandle(Job(Hdr4k, null, vppOnly with { HwDecode = new HardwareDecoding(new HashSet<string>()) })));
        Assert.True(p.CanHandle(Job(Hdr4k, null, vppOnly)));
    }

    // ---------------------------------------------------------------- encoder settings

    [Theory]
    [InlineData("auto", "veryfast")]
    [InlineData("veryslow", "veryslow")]
    [InlineData("slower", "slower")]
    [InlineData("slow", "slow")]
    [InlineData("medium", "medium")]
    [InlineData("fast", "fast")]
    [InlineData("faster", "faster")]
    [InlineData("veryfast", "veryfast")]
    [InlineData("superfast", "veryfast")]
    [InlineData("ultrafast", "veryfast")]
    public async Task EncoderPresetMapsLikeJellyfin(string dashboard, string qsv)
    {
        var p = await Probed();
        var args = Args(p.Build(Job(Sdr1080, null, Defaults with { Software = new SoftwareEncoding(dashboard) })).Output);
        Assert.Contains($"-c:v h264_qsv -preset {qsv} ", args);
    }

    [Fact]
    public async Task AutoPresetUsesTheModulePresetAndInvalidOnesBecomeVeryfast()
    {
        Assert.Contains("-preset slow ", Args((await Probed(null, ("Qsv:Preset", "slow"))).Build(Job(Sdr1080)).Output));
        Assert.Contains("-preset veryfast ", Args((await Probed(null, ("Qsv:Preset", "placebo"))).Build(Job(Sdr1080)).Output));
    }

    [Fact]
    public async Task CapsBitrateBelowTheClientLimit()
    {
        var p = await Probed();
        var args = Args(p.Build(Job(Sdr1080, H264 with { MaxBitrate = 100_000_000 })).Output);
        Assert.Contains("-b:v 30000000 -maxrate 40000000 -bufsize 80000000", args);
        Assert.DoesNotContain("-global_quality", args); // with -maxrate it would select QVBR
        Assert.Contains("-b:v 3000000 -maxrate 4000000 -bufsize 8000000", Args(p.Build(Job(Sdr1080, H264 with { MaxBitrate = 4_444_445 })).Output));
    }

    [Fact]
    public async Task KeyframesFollowTheSegmentLength()
    {
        var args = Args((await Probed()).Build(Job(Mpeg4, null, null, 4)).Output);
        Assert.Contains("-adaptive_i 0 -adaptive_b 0", args);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*4) -forced_idr 1", args);
    }

    [Fact]
    public async Task HevcOutputIsTaggedHvc1OnlyInFmp4()
    {
        var p = await Probed();
        var mp4 = Args(p.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "mp4", null))).Output);
        Assert.Contains("-c:v hevc_qsv -preset veryfast -profile:v main", mp4);
        Assert.EndsWith("-tag:v hvc1", mp4);
        Assert.DoesNotContain("hvc1", Args(p.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "ts", null))).Output));
    }

    // ---------------------------------------------------------------- decoder for software encoders

    [Fact]
    public async Task DecoderDownloadsNv12AfterGpuProcessing()
    {
        var d = await ProbedDecoder();
        var native = d.Build(Job(Hevc4k10));
        Assert.Equal("qsv", native.DecoderId);
        Assert.Equal($"{Devices} -filter_hw_device qs -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi", Args(native.Input));
        Assert.Equal([Sdr, "scale_vaapi=w=1920:h=1080:format=nv12:extra_hw_frames=24", "hwmap=mode=read", "format=nv12"], native.Filters);

        var qsv = d.Build(Job(Hevc4k10 with { Interlaced = true, FrameRate = 25 }, null,
            QsvDecoders with { Deinterlace = new Deinterlacing("yadif", true) }));
        Assert.EndsWith("-hwaccel qsv -hwaccel_device qs -hwaccel_output_format qsv -c:v hevc_qsv", Args(qsv.Input));
        Assert.Equal([Sdr, "vpp_qsv=deinterlace=advanced:rate=field:w=1920:h=1080:format=nv12", "hwdownload", "format=nv12"], qsv.Filters);
    }

    [Fact]
    public async Task DecoderToneMapsLikeThePipeline()
    {
        var d = await ProbedDecoder();
        Assert.Equal([Hdr, "scale_vaapi=w=1920:h=1080:extra_hw_frames=24", "hwmap=derive_device=opencl:mode=read", Ocl, "hwmap=mode=read", "format=nv12"],
            d.Build(Job(Hdr4k)).Filters);
        Assert.Equal([Hdr, "vpp_qsv=w=1920:h=1080:format=nv12:tonemap=1", Sdr, "hwdownload", "format=nv12"],
            d.Build(Job(Hdr4k, null, Vpp with { HwDecode = QsvDecoders.HwDecode })).Filters);
        Assert.Equal([Hdr, "vpp_qsv=w=1920:h=1080", "hwmap=derive_device=opencl:mode=read", Ocl, "hwmap=mode=read", "format=nv12"],
            d.Build(Job(Hdr4k, null, QsvDecoders)).Filters);
        Assert.Contains("-init_hw_device opencl=ocl@va -filter_hw_device qs", Args(d.Build(Job(Hdr4k)).Input));
        Assert.Equal([Sdr, "scale_vaapi=w=1920:h=1080:format=nv12:extra_hw_frames=24", "hwmap=mode=read", "format=nv12"],
            d.Build(Job(Hdr4k, null, Defaults with { ToneMap = new(false) })).Filters);
    }

    [Fact]
    public async Task DecoderTakesOnlyWhatTheGpuDecodesAndCanToneMap()
    {
        var d = await ProbedDecoder(new Ffmpeg(null, "tonemap_opencl"));
        Assert.True(d.CanDecode(Job(Sdr1080)));
        Assert.False(d.CanDecode(Job(Mpeg4)));
        Assert.False(d.CanDecode(Job(Sdr1080, null, Defaults with { HwDecode = new HardwareDecoding(new HashSet<string>()) })));
        Assert.False(d.CanDecode(Job(Hdr4k)));                                      // tone mapping wanted, no OpenCL
        Assert.True(d.CanDecode(Job(Hdr4k, null, Defaults with { ToneMap = new(false) })));
        Assert.True(d.CanDecode(Job(Hdr4k, null, Vpp)));
    }
}
