using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.Transcoding.Nvenc.Tests;

public class NvencPipelineTests
{
    /// <summary>Lists <paramref name="features"/>; test runs succeed unless <paramref name="fails"/> says otherwise.</summary>
    private sealed class Ffmpeg(string[] features, Func<string, bool>? fails = null) : IFfmpegCapabilities
    {
        public List<string> Runs { get; } = [];
        public string FfmpegPath => "ffmpeg";
        public bool HasEncoder(string name) => features.Contains(name);
        public bool HasDecoder(string name) => features.Contains(name);
        public bool HasFilter(string name) => features.Contains(name);
        public bool HasHwAccel(string name) => features.Contains(name);

        public Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            var line = string.Join(' ', args);
            lock (Runs) Runs.Add(line);
            return Task.FromResult(fails?.Invoke(line) != true);
        }
    }

    private static readonly string[] Cuvid =
        ["h264_cuvid", "hevc_cuvid", "vp9_cuvid", "av1_cuvid", "mpeg2_cuvid", "mpeg4_cuvid", "vp8_cuvid", "vc1_cuvid"];

    // jellyfin-ffmpeg has tonemap_cuda and bwdif_cuda; distribution builds (Debian, Ubuntu) do not.
    private static readonly string[] Jellyfin =
    [
        "cuda", "h264_nvenc", "hevc_nvenc", "av1_nvenc", "libx264", "libx265", "libvpx-vp9", "libsvtav1", "mpeg2video", "mpeg4", "libvpx",
        "scale_cuda", "hwupload_cuda", "hwdownload", "tonemap_cuda", "yadif_cuda", "bwdif_cuda", .. Cuvid,
    ];
    private static readonly string[] Distro = Jellyfin.Except(["tonemap_cuda", "bwdif_cuda"]).ToArray();

    /// <summary>An Ampere card (RTX 30xx): no AV1 encoding, no HEVC 4:2:2 decoding.</summary>
    private static Ffmpeg Ampere() => new(Jellyfin, l => l.Contains("-c:v av1_nvenc") || (l.Contains("-hwaccel cuda") && l.Contains("hevc-422.mkv")));

    private static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 23.976);
    private static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 23.976);
    private static readonly VideoSource Interlaced = new("h264", 1920, 1080, 8, null, "High", 25, Interlaced: true);
    private static readonly VideoSource Mpeg4 = new("mpeg4", 1280, 720, 8, null, "Advanced Simple Profile", 25);
    private static readonly VideoTarget H264 = new(false, "h264", "ts", null);

    private const string Nvdec = "-hwaccel cuda -hwaccel_output_format cuda -hwaccel_device 0 -noautorotate -threads 1";
    private const string Sdr = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709";
    private const string Hdr10 = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";
    private const string ToneMap = "tonemap_cuda=format=yuv420p:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0";

    private static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int seg = 6) =>
        new(source, target ?? H264, processing ?? VideoProcessing.Default, new SegmentTiming(seg));

    private static ServiceProvider Services(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ServiceCollection().AddLogging().AddNvencPipeline(config).BuildServiceProvider();
    }

    private static IVideoPipeline Pipeline(params (string Key, string Value)[] settings) =>
        Services(settings).GetServices<IVideoPipeline>().Single();

    private static async Task<IVideoPipeline> Probed(Ffmpeg ffmpeg, params (string Key, string Value)[] settings)
    {
        var p = Pipeline(settings);
        Assert.True(await p.ProbeAsync(ffmpeg, default));
        return p;
    }

    private static async Task<IVideoDecoder> ProbedDecoder(Ffmpeg ffmpeg)
    {
        var d = Services().GetServices<IVideoDecoder>().Single();
        Assert.True(await d.ProbeAsync(ffmpeg, default));
        return d;
    }

    private static string Join(IEnumerable<string> args) => string.Join(' ', args);
    private static string Args(VideoArgs a) => Join(a.Input.Concat(a.Output));

    /// <summary>The <c>-vf</c> value.</summary>
    private static string Filters(VideoArgs a) => a.Output[a.Output.ToList().IndexOf("-vf") + 1];

    private static VideoProcessing With(HardwareDecoding hw) => new() { HwDecode = hw };

    [Fact]
    public async Task RegistersOnePipelineAndDecoderUnderOneId()
    {
        using var sp = Services();
        var p = sp.GetServices<IVideoPipeline>().Single();
        var d = sp.GetServices<IVideoDecoder>().Single();
        Assert.Same(p, d);
        Assert.Equal("nvenc", d.Id);
        Assert.False(p.AcceptsDecodedInput);
        Assert.Equal(3, p.MaxConcurrent);

        // The selector probes both: the second call reuses the first run.
        var ffmpeg = new Ffmpeg(Jellyfin);
        Assert.True(await p.ProbeAsync(ffmpeg, default));
        var runs = ffmpeg.Runs.Count;
        Assert.True(await d.ProbeAsync(ffmpeg, default));
        Assert.Equal(runs, ffmpeg.Runs.Count);
    }

    [Fact]
    public async Task NeedsCudaAndAWorkingTestEncode()
    {
        Assert.False(await Pipeline().ProbeAsync(new Ffmpeg(Jellyfin.Except(["cuda"]).ToArray()), default));
        Assert.False(await Pipeline().ProbeAsync(new Ffmpeg(["cuda", "libx264"]), default));
        // Listed encoders whose test encode fails (no GPU, driver too old, …).
        Assert.False(await Pipeline().ProbeAsync(new Ffmpeg(Jellyfin, l => l.Contains("-gpu 0 -f null")), default));

        var ffmpeg = new Ffmpeg(Jellyfin);
        var p = await Probed(ffmpeg);
        Assert.Equal(["av1", "h264", "hevc"], p.OutputCodecs.Order());
        Assert.Contains("-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x240:rate=1 -frames:v 1 -c:v h264_nvenc -gpu 0 -f null -", ffmpeg.Runs);
    }

    [Fact]
    public async Task AmpereHasNoAv1EncodeAndAv1NeedsFmp4()
    {
        var ampere = await Probed(Ampere());
        Assert.Equal(["h264", "hevc"], ampere.OutputCodecs.Order());
        Assert.False(ampere.CanHandle(Job(Sdr1080, new VideoTarget(false, "av1", "mp4", null))));

        var ada = await Probed(new Ffmpeg(Jellyfin));
        Assert.True(ada.CanHandle(Job(Sdr1080, new VideoTarget(false, "av1", "mp4", null))));
        Assert.False(ada.CanHandle(Job(Sdr1080, new VideoTarget(false, "av1", "ts", null))));
        var args = Args(ada.Build(Job(Sdr1080, new VideoTarget(false, "av1", "mp4", null))));
        Assert.Contains("-c:v av1_nvenc -gpu 0", args);
        Assert.DoesNotContain("-profile:v", args);
        Assert.DoesNotContain("hvc1", args);
    }

    [Fact]
    public async Task OffersHevcOnlyWhenItsTestEncodeWorks()
    {
        var p = await Probed(new Ffmpeg(Jellyfin, l => l.Contains("-c:v hevc_nvenc -gpu")));
        Assert.DoesNotContain("hevc", p.OutputCodecs);
        Assert.True(p.CanHandle(Job(Sdr1080)));
        Assert.False(p.CanHandle(Job(Sdr1080, H264 with { Codec = "hevc" })));
        Assert.False(p.CanHandle(Job(Sdr1080, H264 with { Copy = true })));
    }

    [Fact]
    public async Task Sdr1080pH264StaysOnTheGpu()
    {
        var args = (await Probed(Ampere())).Build(Job(Sdr1080));
        Assert.Equal(Nvdec, Join(args.Input));
        Assert.Equal($"-vf {Sdr},scale_cuda=format=yuv420p -c:v h264_nvenc -gpu 0 -preset p4 -rc vbr -cq 23 -b:v 0 -profile:v high "
            + "-force_key_frames expr:gte(t,n_forced*6) -forced-idr 1 -no-scenecut 1", Join(args.Output));
    }

    [Fact]
    public async Task Hdr4kIsScaledThenToneMappedOnTheGpu()
    {
        var p = await Probed(Ampere());
        Assert.True(p.CanHandle(Job(Hdr4k)));
        var args = p.Build(Job(Hdr4k));
        Assert.Equal(Nvdec, Join(args.Input));
        Assert.Equal($"{Hdr10},scale_cuda=w=1920:h=1080,{ToneMap}", Filters(args));

        // HLG is tagged as such; no scaling and no RExt: tone mapping alone.
        var hlg = p.Build(Job(Hdr4k with { HdrType = "HLG" }, H264 with { MaxHeight = 0 }));
        Assert.Equal($"setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,{ToneMap}", Filters(hlg));
    }

    [Fact]
    public async Task ToneMappingFollowsTheDashboard()
    {
        var p = await Probed(Ampere());
        var tm = new ToneMapping(Algorithm: "hable", Mode: "itp", Range: "tv", Desat: 0.5, Peak: 400, Param: 0.3);
        Assert.EndsWith("tonemap_cuda=format=yuv420p:p=bt709:t=bt709:m=bt709:tonemap=hable:peak=400:desat=0.5:tonemap_mode=itp:param=0.3:range=tv",
            Filters(p.Build(Job(Hdr4k, processing: new VideoProcessing { ToneMap = tm }))));
        Assert.EndsWith("tonemap=reinhard:peak=100:desat=0:tonemap_mode=max:range=pc",
            Filters(p.Build(Job(Hdr4k, processing: new VideoProcessing { ToneMap = new(Algorithm: "reinhard", Mode: "max", Range: "pc") }))));
    }

    [Fact]
    public async Task DisabledToneMappingEncodesHdrAsSdrTaggedEightBit()
    {
        var off = new VideoProcessing { ToneMap = new ToneMapping(Enabled: false) };
        var distro = await Probed(new Ffmpeg(Distro)); // no tonemap_cuda at all
        Assert.False(distro.CanHandle(Job(Hdr4k)));
        Assert.True(distro.CanHandle(Job(Hdr4k, processing: off)));
        var args = distro.Build(Job(Hdr4k, processing: off));
        Assert.Equal(Nvdec, Join(args.Input));
        Assert.Equal($"{Sdr},scale_cuda=w=1920:h=1080:format=yuv420p", Filters(args));

        var broken = await Probed(new Ffmpeg(Jellyfin, l => l.Contains("tonemap_cuda")));
        Assert.False(broken.CanHandle(Job(Hdr4k)));
    }

    [Fact]
    public async Task DeinterlacesOnTheGpu()
    {
        var p = await Probed(Ampere());
        Assert.Equal($"{Sdr},yadif_cuda=0:-1:0,scale_cuda=format=yuv420p", Filters(p.Build(Job(Interlaced))));
        var bwdifDouble = new VideoProcessing { Deinterlace = new Deinterlacing("bwdif", DoubleRate: true) };
        Assert.Equal($"{Sdr},bwdif_cuda=1:-1:0,scale_cuda=format=yuv420p", Filters(p.Build(Job(Interlaced, processing: bwdifDouble))));
        // Double rate only up to 30 fps (unknown counts as 60), as Jellyfin.
        Assert.StartsWith($"{Sdr},bwdif_cuda=0", Filters(p.Build(Job(Interlaced with { FrameRate = 50 }, processing: bwdifDouble))));
        Assert.StartsWith($"{Sdr},bwdif_cuda=0", Filters(p.Build(Job(Interlaced with { FrameRate = 0 }, processing: bwdifDouble))));

        // No bwdif_cuda: yadif_cuda, as Jellyfin.
        var distro = await Probed(new Ffmpeg(Distro));
        Assert.StartsWith($"{Sdr},yadif_cuda=1:-1:0", Filters(distro.Build(Job(Interlaced, processing: bwdifDouble))));
    }

    [Fact]
    public async Task CpuDecodedSourcesAreDeinterlacedAndScaledInSoftware()
    {
        var p = await Probed(Ampere());
        var mpeg4 = p.Build(Job(Mpeg4 with { Codec = "mpeg1video" }));
        Assert.Empty(mpeg4.Input);
        Assert.Equal($"{Sdr},format=yuv420p", Filters(mpeg4));

        var none = With(new HardwareDecoding(Codecs: new HashSet<string>()));
        var interlaced = p.Build(Job(Interlaced with { Height = 2160, Width = 3840 }, processing: none with { Deinterlace = new("bwdif", true) }));
        Assert.Empty(interlaced.Input);
        Assert.Equal($"{Sdr},bwdif=1:-1:0,scale=1920:1080,format=yuv420p", Filters(interlaced));

        // HDR decoded on the CPU is uploaded for tonemap_cuda.
        var hdr = p.Build(Job(Hdr4k, processing: none));
        Assert.Empty(hdr.Input);
        Assert.Equal($"{Hdr10},scale=1920:1080,format=p010le,hwupload_cuda=device=0,{ToneMap}", Filters(hdr));
    }

    [Fact]
    public async Task HardwareDecodingSettingsGateNvdec()
    {
        var p = await Probed(Ampere());
        Assert.Empty(p.Build(Job(Sdr1080, processing: With(new HardwareDecoding(Codecs: new HashSet<string> { "hevc" })))).Input);
        Assert.Equal(Nvdec, Join(p.Build(Job(Hdr4k, processing: With(new HardwareDecoding(Codecs: new HashSet<string> { "hevc" })))).Input));
        Assert.Empty(p.Build(Job(Hdr4k, processing: With(new HardwareDecoding(Hevc10Bit: false)))).Input);
        Assert.Equal(Nvdec, Join(p.Build(Job(Hdr4k with { BitDepth = 8, HdrType = null, Profile = "Main" }, processing: With(new HardwareDecoding(Hevc10Bit: false)))).Input));

        var vp9 = new VideoSource("vp9", 1920, 1080, 10, null, "Profile 2", 24);
        Assert.NotEmpty(p.Build(Job(vp9)).Input);
        Assert.Empty(p.Build(Job(vp9, processing: With(new HardwareDecoding(Vp9_10Bit: false)))).Input);
        Assert.Empty(p.Build(Job(vp9 with { Profile = "Profile 1", BitDepth = 8 })).Input);
    }

    [Fact]
    public async Task HevcRextDecodesOnlyWhenEnabledAndProven()
    {
        var p = await Probed(Ampere());
        var rext10 = new VideoSource("hevc", 1920, 1080, 10, null, "Rext", 24);
        var rext12 = rext10 with { BitDepth = 12 };
        Assert.Empty(p.Build(Job(rext10)).Input); // off by default, as Jellyfin
        var on = With(new HardwareDecoding(HevcRext10Bit: true, HevcRext12Bit: true));
        Assert.Equal(Nvdec, Join(p.Build(Job(rext10, processing: on)).Input));
        Assert.Equal(Nvdec, Join(p.Build(Job(rext12, processing: on)).Input));
        Assert.Empty(p.Build(Job(rext12, processing: With(new HardwareDecoding(HevcRext10Bit: true)))).Input);
        // 4:2:2 failed the probe on this GPU.
        Assert.Empty(p.Build(Job(rext10 with { Profile = "Rext 4:2:2" }, processing: on)).Input);

        // RExt HDR is converted to p010 before tone mapping.
        var hdr = p.Build(Job(rext10 with { HdrType = "HDR10" }, processing: on));
        Assert.Equal($"{Hdr10},scale_cuda=format=p010,{ToneMap}", Filters(hdr));
    }

    [Fact]
    public async Task UnusualProfilesAreDecodedOnTheCpu()
    {
        var p = await Probed(Ampere());
        Assert.Empty(p.Build(Job(Sdr1080 with { BitDepth = 10, Profile = "High 10" })).Input);
        Assert.Empty(p.Build(Job(Sdr1080 with { Profile = "High 4:4:4 Predictive" })).Input);
        Assert.Empty(p.Build(Job(Sdr1080 with { Codec = "mpeg2video", Profile = "4:2:2" })).Input);
        Assert.NotEmpty(p.Build(Job(Mpeg4 with { Codec = "mpeg2video", Profile = "Main" })).Input);
        Assert.NotEmpty(p.Build(Job(Mpeg4)).Input);
        Assert.NotEmpty(p.Build(Job(Mpeg4 with { Codec = "vc1", Profile = "Advanced" })).Input);
    }

    [Fact]
    public async Task CodecsNvdecFailedOnAreDecodedOnTheCpu()
    {
        var p = await Probed(new Ffmpeg(Jellyfin, l => l.Contains("-hwaccel cuda") && l.Contains("av1.mkv")));
        var av1 = new VideoSource("av1", 3840, 2160, 10, "HDR10", "Main", 24);
        var args = p.Build(Job(av1));
        Assert.Empty(args.Input);
        Assert.Equal($"{Hdr10},scale=1920:1080,format=p010le,hwupload_cuda=device=0,{ToneMap}", Filters(args));
        Assert.Equal(Nvdec, Join(p.Build(Job(Sdr1080)).Input));
    }

    [Fact]
    public async Task CuvidDecodersReplaceNvdecWhenEnhancedNvdecIsOff()
    {
        var p = await Probed(Ampere());
        var cuvid = With(new HardwareDecoding(EnhancedNvdec: false));
        Assert.Equal("-hwaccel cuda -hwaccel_output_format cuda -hwaccel_device 0 -noautorotate -c:v h264_cuvid", Join(p.Build(Job(Sdr1080, processing: cuvid)).Input));
        Assert.EndsWith("-c:v hevc_cuvid", Join(p.Build(Job(Hdr4k, processing: cuvid)).Input));
        Assert.EndsWith("-c:v mpeg2_cuvid", Join(p.Build(Job(Mpeg4 with { Codec = "mpeg2video", Profile = "Main" }, processing: cuvid)).Input));

        // A cuvid decoder that failed its probe falls back to NVDEC.
        var noCuvid = await Probed(new Ffmpeg(Jellyfin, l => l.Contains("-c:v h264_cuvid")));
        Assert.Equal(Nvdec, Join(noCuvid.Build(Job(Sdr1080, processing: cuvid)).Input));
    }

    [Fact]
    public async Task WithoutCudaFiltersEverythingRunsOnTheCpu()
    {
        var ffmpeg = new Ffmpeg(Jellyfin, l => l.Contains("scale_cuda"));
        var p = await Probed(ffmpeg);
        var args = p.Build(Job(Hdr4k with { HdrType = null }));
        Assert.Empty(args.Input);
        Assert.Equal($"{Sdr},scale=1920:1080,format=yuv420p", Filters(args));
        Assert.False(p.CanHandle(Job(Hdr4k)));
        Assert.DoesNotContain(ffmpeg.Runs, r => r.Contains("-hwaccel"));
        Assert.False(await ((IVideoDecoder)p).ProbeAsync(ffmpeg, default));
    }

    [Fact]
    public async Task EncoderPresetMapsLikeJellyfin()
    {
        var p = await Probed(Ampere(), ("Nvenc:Preset", "p5"));
        string Preset(string preset) =>
            Args(p.Build(Job(Sdr1080, processing: new VideoProcessing { Software = new SoftwareEncoding(preset) }))).Split("-preset ")[1].Split(' ')[0];
        Assert.Equal("p5", Preset("auto"));
        Assert.Equal("p7", Preset("veryslow"));
        Assert.Equal("p6", Preset("slower"));
        Assert.Equal("p5", Preset("slow"));
        Assert.Equal("p4", Preset("medium"));
        Assert.Equal("p3", Preset("fast"));
        Assert.Equal("p2", Preset("faster"));
        Assert.Equal("p1", Preset("veryfast"));
        Assert.Equal("p1", Preset("ultrafast"));
    }

    [Fact]
    public async Task HevcInFmp4IsTaggedForApple()
    {
        var p = await Probed(Ampere());
        var mp4 = Args(p.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "mp4", null))));
        Assert.Contains("-c:v hevc_nvenc -gpu 0", mp4);
        Assert.Contains("-profile:v main ", mp4);
        Assert.Contains("-tag:v hvc1", mp4);
        Assert.DoesNotContain("hvc1", Args(p.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "ts", null)))));
    }

    [Fact]
    public async Task CapsBitrateBelowTheClientLimit()
    {
        var p = await Probed(Ampere());
        Assert.Contains("-maxrate 40000000 -bufsize 80000000", Args(p.Build(Job(Sdr1080, H264 with { MaxBitrate = 100_000_000 }))));
        Assert.Contains("-maxrate 7200000 -bufsize 14400000", Args(p.Build(Job(Sdr1080, H264 with { MaxBitrate = 8_000_000 }))));
        Assert.DoesNotContain("-maxrate", Args(p.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task KeyframesFollowTheSegmentLength()
    {
        var args = Args((await Probed(Ampere())).Build(Job(Sdr1080, seg: 4)));
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*4) -forced-idr 1 -no-scenecut 1", args);
    }

    [Fact]
    public async Task TargetMaxHeightDrivesScaling()
    {
        var p = await Probed(Ampere());
        Assert.Equal($"{Sdr},scale_cuda=w=1280:h=720:format=yuv420p", Filters(p.Build(Job(Sdr1080, H264 with { MaxHeight = 720 }))));
        Assert.Equal($"{Sdr},scale_cuda=format=yuv420p", Filters(p.Build(Job(Hdr4k with { HdrType = null }, H264 with { MaxHeight = 0 }))));
        // Odd aspect ratios keep an even width.
        Assert.Equal($"{Sdr},scale_cuda=w=1286:h=720:format=yuv420p", Filters(p.Build(Job(Sdr1080 with { Width = 1928 }, H264 with { MaxHeight = 720 }))));
    }

    [Fact]
    public async Task NvencSectionConfiguresThePipeline()
    {
        var ffmpeg = Ampere();
        var p = await Probed(ffmpeg, ("Nvenc:Preset", "p6"), ("Nvenc:Cq", "19"), ("Nvenc:MaxConcurrent", "5"), ("Nvenc:Gpu", "1"));
        Assert.Equal(5, p.MaxConcurrent);
        var args = p.Build(Job(Sdr1080));
        Assert.Equal("-hwaccel cuda -hwaccel_output_format cuda -hwaccel_device 1 -noautorotate -threads 1", Join(args.Input));
        Assert.Contains("-gpu 1 -preset p6 -rc vbr -cq 19 -b:v 0", Join(args.Output));
        // The probe tests the configured GPU too.
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-c:v h264_nvenc -gpu 1"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("hwupload_cuda=device=1"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-hwaccel_device 1"));
    }

    [Fact]
    public async Task DecoderEndsInSystemMemory()
    {
        var d = await ProbedDecoder(Ampere());
        Assert.True(d.CanDecode(Job(Sdr1080)));
        var sdr = d.Build(Job(Sdr1080));
        Assert.Equal("nvenc", sdr.DecoderId);
        Assert.Equal(Nvdec, Join(sdr.Input));
        Assert.Equal([Sdr, "scale_cuda=format=yuv420p", "hwdownload", "format=yuv420p"], sdr.Filters);

        var hdr = d.Build(Job(Hdr4k));
        Assert.Equal([Hdr10, "scale_cuda=w=1920:h=1080", ToneMap, "hwdownload", "format=yuv420p"], hdr.Filters);

        var deint = d.Build(Job(Interlaced, processing: new VideoProcessing { Deinterlace = new("yadif", true) }));
        Assert.Equal([Sdr, "yadif_cuda=1:-1:0", "scale_cuda=format=yuv420p", "hwdownload", "format=yuv420p"], deint.Filters);

        var cuvid = d.Build(Job(Sdr1080, processing: With(new HardwareDecoding(EnhancedNvdec: false))));
        Assert.EndsWith("-c:v h264_cuvid", Join(cuvid.Input));
    }

    [Fact]
    public async Task DecoderDeclinesWhatItCannotDecode()
    {
        var d = await ProbedDecoder(Ampere());
        Assert.False(d.CanDecode(Job(Mpeg4 with { Codec = "mpeg1video" })));
        Assert.False(d.CanDecode(Job(Sdr1080, processing: With(new HardwareDecoding(Codecs: new HashSet<string> { "hevc" })))));
        Assert.False(d.CanDecode(Job(Sdr1080 with { BitDepth = 10, Profile = "High 10" })));

        // HDR needs working tone mapping, unless the dashboard turned it off.
        var distro = await ProbedDecoder(new Ffmpeg(Distro));
        Assert.False(distro.CanDecode(Job(Hdr4k)));
        Assert.True(distro.CanDecode(Job(Hdr4k, processing: new VideoProcessing { ToneMap = new(Enabled: false) })));

        // An encoder-less GPU (or broken NVENC) still decodes.
        var noEncode = Services().GetServices<IVideoDecoder>().Single();
        Assert.True(await noEncode.ProbeAsync(new Ffmpeg(Jellyfin, l => l.Contains("_nvenc -gpu 0 -f null")), default));
    }
}
