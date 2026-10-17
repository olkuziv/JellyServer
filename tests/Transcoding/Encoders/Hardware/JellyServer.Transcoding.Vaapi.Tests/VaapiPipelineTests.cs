using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.Transcoding.Vaapi.Tests;

public class VaapiPipelineTests
{
    internal static readonly string[] Everything =
    [
        "vaapi", "opencl", "hwupload", "scale_vaapi", "hwdownload", "tonemap_vaapi", "procamp_vaapi", "tonemap_opencl", "hwmap",
        "h264_vaapi", "hevc_vaapi", "av1_vaapi", "libx264", "libx265", "libvpx-vp9", "libsvtav1", "mpeg2video", "libvpx",
    ];

    internal sealed class Ffmpeg(string[] features, Func<string, bool>? runs = null) : IFfmpegCapabilities
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
            return Task.FromResult(runs?.Invoke(line) ?? true);
        }
    }

    internal sealed record Module(IVideoPipeline Pipeline, IVideoDecoder Decoder, IConfigurationRoot Config);

    internal static Module Create(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var sp = new ServiceCollection().AddLogging().AddVaapiPipeline(config).BuildServiceProvider();
        return new Module(sp.GetServices<IVideoPipeline>().Single(), sp.GetServices<IVideoDecoder>().Single(), config);
    }

    internal static async Task<Module> Probed(Ffmpeg? ffmpeg = null, params (string Key, string Value)[] settings)
    {
        var m = Create(settings);
        ffmpeg ??= new Ffmpeg(Everything);
        Assert.True(await m.Pipeline.ProbeAsync(ffmpeg, default));
        await m.Decoder.ProbeAsync(ffmpeg, default);
        return m;
    }

    // An AMD card: radeonsi, no VPP tone mapping, OpenCL only through a copy (ROCm).
    internal static Ffmpeg Amd() => new(Everything, r => !r.Contains("driver=iHD") && !r.Contains("tonemap_vaapi") && !r.Contains("derive_device=opencl"));

    internal static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 23.976);
    internal static readonly VideoSource Hevc4k10 = new("hevc", 3840, 2160, 10, null, "Main 10", 24);
    internal static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 24);
    internal static readonly VideoSource Xvid = new("mpeg4", 720, 400, 8, null, "Advanced Simple Profile", 25);
    internal static readonly VideoTarget H264 = new(false, "h264", "ts", null);

    internal static readonly VideoProcessing ToneMapOn = VideoProcessing.Default with { ToneMap = new(Enabled: true) };
    internal static readonly VideoProcessing NoToneMap = VideoProcessing.Default with { ToneMap = new(Enabled: false) };
    internal static readonly VideoProcessing Vpp = VideoProcessing.Default with { VppToneMap = new(Enabled: true), ToneMap = new(Enabled: false) };

    internal static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int segment = 6) =>
        new(source, target ?? H264, processing ?? VideoProcessing.Default, new SegmentTiming(segment));

    internal static string Args(VideoArgs a) => string.Join(' ', a.Input.Concat(a.Output));

    private const string Device = "-init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va";
    private const string HwAccel = "-hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi";
    private const string Encode = "-c:v h264_vaapi -profile:v high -rc_mode CQP -qp 24 -sei -a53_cc -force_key_frames expr:gte(t,n_forced*6)";

    [Fact]
    public async Task NeedsTheVaapiHwAccel()
    {
        var ffmpeg = new Ffmpeg(Everything.Except(["vaapi"]).ToArray());
        var m = Create();
        Assert.False(await m.Pipeline.ProbeAsync(ffmpeg, default));
        Assert.False(await m.Decoder.ProbeAsync(ffmpeg, default));
        Assert.Empty(ffmpeg.Runs);
    }

    [Fact]
    public async Task FailsFastWithoutTheDevice()
    {
        var ffmpeg = new Ffmpeg(Everything, _ => false); // listed encoders, but /dev/dri is missing
        var m = Create();
        Assert.False(await m.Pipeline.ProbeAsync(ffmpeg, default));
        Assert.False(await m.Decoder.ProbeAsync(ffmpeg, default));
        Assert.Empty(m.Pipeline.OutputCodecs);
        Assert.Equal(
            $"-hide_banner -loglevel error {Device} -f lavfi -i testsrc2=size=640x360:rate=25 -frames:v 1 " +
            "-vf format=nv12,hwupload,scale_vaapi=format=nv12,hwdownload,format=nv12 -f null -",
            Assert.Single(ffmpeg.Runs)); // one device check, shared by pipeline and decoder
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080)));
        Assert.False(m.Decoder.CanDecode(Job(Sdr1080)));
    }

    [Fact]
    public async Task PipelineAndDecoderShareOneProbe()
    {
        var ffmpeg = new Ffmpeg(Everything);
        await Probed(ffmpeg);
        var runs = ffmpeg.Runs.Count;
        Assert.Equal(1, ffmpeg.Runs.Count(r => r.Contains("scale_vaapi=format=nv12,hwdownload") && r.Contains("testsrc2")));
        Assert.True(runs > 10);
    }

    [Fact]
    public async Task ProbeEncodesOneRealFramePerCodecInBothModes()
    {
        var ffmpeg = new Ffmpeg(Everything);
        await Probed(ffmpeg, ("Vaapi:Device", "/dev/dri/renderD129"));
        foreach (var encoder in new[] { "h264_vaapi", "hevc_vaapi", "av1_vaapi" })
        {
            Assert.Contains($"-hide_banner -loglevel error -init_hw_device vaapi=va:/dev/dri/renderD129 -filter_hw_device va " +
                $"-f lavfi -i testsrc2=size=640x360:rate=25 -frames:v 1 -vf format=nv12,hwupload -c:v {encoder} -f null -", ffmpeg.Runs);
            Assert.Contains(ffmpeg.Runs, r => r.Contains($"-c:v {encoder} -low_power 1 -f null -"));
        }
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-init_hw_device vaapi=va:/dev/dri/renderD129,driver=iHD "));
    }

    [Fact]
    public async Task OutputsOnlyCodecsThatPassTheTestEncode()
    {
        var m = await Probed(new Ffmpeg(Everything, r => !r.Contains("hevc_vaapi")));
        Assert.Equal("vaapi", m.Pipeline.Id);
        Assert.Equal(["av1", "h264"], m.Pipeline.OutputCodecs.Order());
        Assert.True(m.Pipeline.CanHandle(Job(Sdr1080)));
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080, H264 with { Codec = "hevc" })));
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080, H264 with { Copy = true })));

        var noAv1 = await Probed(new Ffmpeg(Everything.Except(["av1_vaapi"]).ToArray()));
        Assert.Equal(["h264", "hevc"], noAv1.Pipeline.OutputCodecs.Order());
    }

    [Fact]
    public async Task Av1OnlyForFmp4()
    {
        var m = await Probed();
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080, new VideoTarget(false, "av1", "ts", null))));
        var mp4 = new VideoTarget(false, "av1", "mp4", null);
        Assert.True(m.Pipeline.CanHandle(Job(Sdr1080, mp4)));
        var args = Args(m.Pipeline.Build(Job(Sdr1080, mp4)));
        Assert.Contains("-c:v av1_vaapi -profile:v main -rc_mode CQP -global_quality:v 120 -force_key_frames", args);
        Assert.DoesNotContain("-sei", args);
        Assert.DoesNotContain("hvc1", args);
    }

    [Fact]
    public async Task LowPowerFollowsTheProbeUnlessTheDashboardForcesIt()
    {
        var both = await Probed();
        Assert.DoesNotContain("-low_power", Args(both.Pipeline.Build(Job(Sdr1080))));

        // Only the low-power entrypoint works: auto-detected.
        var lpOnly = await Probed(new Ffmpeg(Everything, r => !r.Contains("-c:v h264_vaapi -f null") && !r.Contains("-c:v hevc_vaapi -f null")));
        Assert.Contains("-c:v h264_vaapi -low_power 1 -profile:v high", Args(lpOnly.Pipeline.Build(Job(Sdr1080))));
        Assert.Contains("-c:v hevc_vaapi -low_power 1 -profile:v main", Args(lpOnly.Pipeline.Build(Job(Sdr1080, H264 with { Codec = "hevc" }))));

        // Forced on: always low power, per codec.
        var forceH264 = VideoProcessing.Default with { HwEncode = new(LowPowerH264: true) };
        Assert.Contains("-c:v h264_vaapi -low_power 1", Args(both.Pipeline.Build(Job(Sdr1080, null, forceH264))));
        Assert.DoesNotContain("-low_power", Args(both.Pipeline.Build(Job(Sdr1080, H264 with { Codec = "hevc" }, forceH264))));
        var forceHevc = VideoProcessing.Default with { HwEncode = new(LowPowerHevc: true) };
        Assert.Contains("-c:v hevc_vaapi -low_power 1", Args(both.Pipeline.Build(Job(Sdr1080, H264 with { Codec = "hevc" }, forceHevc))));

        // Forced on but the GPU has no low-power entrypoint: the next pipeline takes the job.
        var noLp = await Probed(new Ffmpeg(Everything, r => !r.Contains("-low_power 1")));
        Assert.False(noLp.Pipeline.CanHandle(Job(Sdr1080, null, forceH264)));
        Assert.True(noLp.Pipeline.CanHandle(Job(Sdr1080, H264 with { Codec = "hevc" }, forceH264)));

        // Forced off on a low-power-only GPU.
        Assert.False(lpOnly.Pipeline.CanHandle(Job(Sdr1080, null, VideoProcessing.Default with { HwEncode = new(LowPowerH264: false) })));
    }

    [Fact]
    public async Task H264SourceStaysOnTheGpu()
    {
        var m = await Probed();
        Assert.Equal($"{Device} {HwAccel} -vf scale_vaapi=format=nv12:extra_hw_frames=24 {Encode}", Args(m.Pipeline.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task ScalesToTheTargetMaxHeight()
    {
        var m = await Probed();
        Assert.Contains("-vf scale_vaapi=w=-2:h=1080:format=nv12:extra_hw_frames=24 -c:v h264_vaapi", Args(m.Pipeline.Build(Job(Hevc4k10))));
        Assert.Contains("-vf scale_vaapi=w=-2:h=720:format=nv12:extra_hw_frames=24 ", Args(m.Pipeline.Build(Job(Sdr1080, H264 with { MaxHeight = 720 }))));
        Assert.Contains("-vf scale_vaapi=format=nv12:extra_hw_frames=24 ", Args(m.Pipeline.Build(Job(Hevc4k10, H264 with { MaxHeight = 0 }))));
        Assert.Contains("-vf scale=-2:720,format=nv12,hwupload ", Args(m.Pipeline.Build(Job(Xvid with { Height = 1080 }, H264 with { MaxHeight = 720 }))));
    }

    [Fact]
    public async Task DecodesUnsupportedSourcesOnTheCpuAndUploads()
    {
        var m = await Probed();
        Assert.True(m.Pipeline.CanHandle(Job(Xvid)));
        Assert.Equal($"{Device} -vf format=nv12,hwupload {Encode}", Args(m.Pipeline.Build(Job(Xvid))));
        var hi10p = Args(m.Pipeline.Build(Job(new VideoSource("h264", 1920, 1080, 10, null, "High 10"))));
        Assert.DoesNotContain("-hwaccel", hi10p);
        Assert.Contains("-vf format=nv12,hwupload", hi10p);
    }

    [Fact]
    public async Task DeinterlacesOnTheGpuOrTheCpu()
    {
        var m = await Probed();
        var i25 = Sdr1080 with { Interlaced = true, FrameRate = 25 };
        Assert.Contains("-vf deinterlace_vaapi=rate=frame,scale_vaapi=format=nv12", Args(m.Pipeline.Build(Job(i25))));
        var doubleRate = VideoProcessing.Default with { Deinterlace = new("bwdif", DoubleRate: true) };
        Assert.Contains("-vf deinterlace_vaapi=rate=field,", Args(m.Pipeline.Build(Job(i25, null, doubleRate))));
        Assert.Contains("-vf deinterlace_vaapi=rate=frame,", Args(m.Pipeline.Build(Job(i25 with { FrameRate = 50 }, null, doubleRate)))); // already field rate
        Assert.Contains("-vf deinterlace_vaapi=rate=frame,", Args(m.Pipeline.Build(Job(i25 with { FrameRate = 0 }, null, doubleRate))));

        var cpu = Xvid with { Interlaced = true };
        Assert.Contains("-vf yadif=0:-1:0,format=nv12,hwupload", Args(m.Pipeline.Build(Job(cpu))));
        Assert.Contains("-vf bwdif=1:-1:0,format=nv12,hwupload", Args(m.Pipeline.Build(Job(cpu, null, doubleRate))));
        Assert.DoesNotContain("deinterlace", Args(m.Pipeline.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task HwDecodeSettingsGateGpuDecoding()
    {
        var m = await Probed();
        bool Gpu(VideoSource s, HardwareDecoding? hw = null) =>
            Args(m.Pipeline.Build(Job(s, null, VideoProcessing.Default with { HwDecode = hw ?? new() }))).Contains("-hwaccel vaapi");

        Assert.True(Gpu(Sdr1080));
        Assert.False(Gpu(Sdr1080, new(Codecs: new HashSet<string> { "hevc" })));
        Assert.True(Gpu(Hevc4k10, new(Codecs: new HashSet<string> { "hevc" })));
        Assert.False(Gpu(Hevc4k10, new(Hevc10Bit: false)));
        Assert.True(Gpu(Hevc4k10 with { BitDepth = 8, Profile = "Main" }, new(Hevc10Bit: false)));

        var vp9 = new VideoSource("vp9", 1920, 1080, 10, null, "Profile 2");
        Assert.True(Gpu(vp9));
        Assert.False(Gpu(vp9, new(Vp9_10Bit: false)));
        Assert.False(Gpu(vp9 with { Profile = "Profile 1", BitDepth = 8 }));
        Assert.True(Gpu(new VideoSource("av1", 1920, 1080, 10, null, "Main")));
        Assert.False(Gpu(new VideoSource("av1", 1920, 1080, 8, null, "High")));
        Assert.True(Gpu(new VideoSource("mpeg2video", 720, 576, 8, null, "Main")));
        Assert.True(Gpu(new VideoSource("vc1", 1920, 1080, 8, null, "Advanced")));
        Assert.False(Gpu(Xvid));
        Assert.False(Gpu(new VideoSource("h264", 1920, 1080, 8, null, "High 4:4:4 Predictive")));

        // HEVC Rext: off by default, per bit depth, and only on Intel iHD.
        var rext10 = Hevc4k10 with { Profile = "Rext" };
        var rext12 = rext10 with { BitDepth = 12 };
        Assert.False(Gpu(rext10));
        Assert.True(Gpu(rext10, new(HevcRext10Bit: true)));
        Assert.False(Gpu(rext12, new(HevcRext10Bit: true)));
        Assert.True(Gpu(rext12, new(HevcRext12Bit: true)));
        Assert.False(Gpu(Hevc4k10 with { BitDepth = 12 }, new(HevcRext12Bit: true))); // 12-bit non-Rext doesn't exist
        var amd = await Probed(Amd());
        Assert.DoesNotContain("-hwaccel", Args(amd.Pipeline.Build(Job(rext10, null, VideoProcessing.Default with { HwDecode = new(HevcRext10Bit: true) }))));

        // A codec the GPU failed to decode in the probe stays on the CPU.
        var noVp9 = await Probed(new Ffmpeg(Everything, r => !(r.Contains("-hwaccel vaapi") && r.Contains("vp9.mkv"))));
        Assert.DoesNotContain("-hwaccel", Args(noVp9.Pipeline.Build(Job(vp9))));
        Assert.Contains("-hwaccel", Args(noVp9.Pipeline.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task BaselineH264AllowsProfileMismatch()
    {
        var m = await Probed();
        Assert.Contains($"{HwAccel} -hwaccel_flags +allow_profile_mismatch -vf", Args(m.Pipeline.Build(Job(Sdr1080 with { Profile = "Baseline" }))));
        Assert.DoesNotContain("allow_profile_mismatch", Args(m.Pipeline.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task VppToneMapsHdr10WithProcamp()
    {
        var m = await Probed();
        var job = Job(Hdr4k, null, Vpp);
        Assert.True(m.Pipeline.CanHandle(job));
        Assert.Equal($"{Device} {HwAccel} -vf setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc," +
            "scale_vaapi=w=-2:h=1080:extra_hw_frames=24,procamp_vaapi=b=16," +
            $"tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32 {Encode}", Args(m.Pipeline.Build(job)));

        var custom = Vpp with { VppToneMap = new(true, Brightness: 0, Contrast: 1.5) };
        Assert.Contains(",procamp_vaapi=c=1.5,tonemap_vaapi", Args(m.Pipeline.Build(Job(Hdr4k, null, custom))));
        var both = Vpp with { VppToneMap = new(true, Brightness: -20.5, Contrast: 2) };
        Assert.Contains(",procamp_vaapi=b=-20.5:c=2,tonemap_vaapi", Args(m.Pipeline.Build(Job(Hdr4k, null, both))));
        var neutral = Vpp with { VppToneMap = new(true, Brightness: 0, Contrast: 1) };
        Assert.Contains("extra_hw_frames=24,tonemap_vaapi=", Args(m.Pipeline.Build(Job(Hdr4k, null, neutral))));
        Assert.Contains("scale_vaapi=w=-2:h=1080:format=p010:extra_hw_frames=24,procamp", Args(m.Pipeline.Build(Job(Hdr4k with { Profile = "Rext" }, null,
            Vpp with { HwDecode = new(HevcRext10Bit: true) }))));
        // No scaling needed, 4:2:0: the decoder's surfaces go straight to VPP.
        Assert.Contains("colorspace=bt2020nc,procamp_vaapi=b=16,", Args(m.Pipeline.Build(Job(Hdr4k with { Width = 1920, Height = 1080 }, null, Vpp))));
    }

    [Fact]
    public async Task VppIsPreferredOverOpenClAndFallsBackToIt()
    {
        var m = await Probed();
        var vppAndOcl = Vpp with { ToneMap = new(Enabled: true) };
        Assert.Contains("tonemap_vaapi", Args(m.Pipeline.Build(Job(Hdr4k, null, vppAndOcl))));
        // HLG is not for VPP: OpenCL takes it when tone mapping is on, otherwise the next pipeline does.
        var hlg = Hdr4k with { HdrType = "HLG" };
        var args = Args(m.Pipeline.Build(Job(hlg, null, vppAndOcl)));
        Assert.Contains("setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,", args);
        Assert.Contains("tonemap_opencl", args);
        Assert.False(m.Pipeline.CanHandle(Job(hlg, null, Vpp)));
        // VPP needs GPU decoding.
        var cpuDecode = vppAndOcl with { HwDecode = new(Hevc10Bit: false) };
        Assert.Contains("format=p010le,hwupload,hwmap=derive_device=opencl", Args(m.Pipeline.Build(Job(Hdr4k, null, cpuDecode))));
        Assert.False(m.Pipeline.CanHandle(Job(Hdr4k, null, Vpp with { HwDecode = new(Hevc10Bit: false) })));
    }

    [Fact]
    public async Task OpenClToneMapsThroughInterop()
    {
        var m = await Probed(new Ffmpeg(Everything, r => !r.Contains("tonemap_vaapi")));
        var job = Job(Hdr4k, null, ToneMapOn);
        Assert.True(m.Pipeline.CanHandle(job));
        Assert.Equal($"{Device} {HwAccel} -vf setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc," +
            "scale_vaapi=w=-2:h=1080:extra_hw_frames=24,hwmap=derive_device=opencl:mode=read," +
            "tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0," +
            $"hwmap=derive_device=vaapi:mode=write:reverse=1,format=vaapi {Encode}", Args(m.Pipeline.Build(job)));

        var custom = ToneMapOn with { ToneMap = new(true, "mobius", "itp", "pc", 0.5, 400, 0.3) };
        Assert.Contains("tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=mobius:peak=400:desat=0.5:tonemap_mode=itp:param=0.3:range=pc,",
            Args(m.Pipeline.Build(Job(Hdr4k, null, custom))));
        Assert.Contains("tonemap=reinhard:peak=100:desat=0:range=tv,",
            Args(m.Pipeline.Build(Job(Hdr4k, null, ToneMapOn with { ToneMap = new(true, "reinhard", "auto", "tv") }))));

        // VPP asked for but VPP doesn't work: OpenCL when tone mapping is on.
        Assert.Contains("tonemap_opencl", Args(m.Pipeline.Build(Job(Hdr4k, null, Vpp with { ToneMap = new(Enabled: true) }))));
        Assert.False(m.Pipeline.CanHandle(Job(Hdr4k, null, Vpp)));

        // Decoded on the CPU: uploaded as p010 and mapped the same way.
        Assert.Contains("-vf setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=-2:1080,format=p010le,hwupload," +
            "hwmap=derive_device=opencl:mode=read,tonemap_opencl=", Args(m.Pipeline.Build(Job(Hdr4k, null, ToneMapOn with { HwDecode = new(Hevc10Bit: false) }))));
    }

    [Fact]
    public async Task HdrIsEncodedUntouchedWhenToneMappingIsOff()
    {
        var m = await Probed();
        var job = Job(Hdr4k, null, NoToneMap);
        Assert.True(m.Pipeline.CanHandle(job));
        Assert.Contains("-vf setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale_vaapi=w=-2:h=1080:format=nv12:extra_hw_frames=24 -c:v",
            Args(m.Pipeline.Build(job)));
        Assert.Contains("-vf setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale=-2:1080,format=nv12,hwupload -c:v",
            Args(m.Pipeline.Build(Job(Hdr4k, null, NoToneMap with { HwDecode = new(Hevc10Bit: false) }))));
    }

    [Fact]
    public async Task LeavesHdrToTheNextPipelineWhenToneMappingIsImpossible()
    {
        // Neither VPP nor OpenCL works (e.g. no OpenCL runtime in the container).
        var none = await Probed(new Ffmpeg(Everything, r => !r.Contains("tonemap_")));
        Assert.False(none.Pipeline.CanHandle(Job(Hdr4k, null, ToneMapOn)));
        Assert.False(none.Pipeline.CanHandle(Job(Hdr4k, null, Vpp)));
        Assert.True(none.Pipeline.CanHandle(Job(Hdr4k, null, NoToneMap)));
        Assert.True(none.Pipeline.CanHandle(Job(Sdr1080, null, ToneMapOn)));

        var noFilters = await Probed(new Ffmpeg(Everything.Except(["tonemap_vaapi", "tonemap_opencl"]).ToArray()));
        Assert.False(noFilters.Pipeline.CanHandle(Job(Hdr4k, null, ToneMapOn)));
        var noX265 = await Probed(new Ffmpeg(Everything.Except(["libx265"]).ToArray())); // no HDR sample to prove anything with
        Assert.False(noX265.Pipeline.CanHandle(Job(Hdr4k, null, ToneMapOn)));
    }

    [Fact]
    public async Task AmdUsesOpenClOnlyForTheDecoder()
    {
        var ffmpeg = Amd();
        var m = await Probed(ffmpeg);
        Assert.Contains(ffmpeg.Runs, r => r.Contains("driver=radeonsi"));
        Assert.DoesNotContain(ffmpeg.Runs, r => r.Contains("tonemap_vaapi")); // VPP tone mapping is only tried on iHD
        // Without interop the frames can't get back into VAAPI surfaces for the encoder.
        Assert.False(m.Pipeline.CanHandle(Job(Hdr4k, null, ToneMapOn)));
        Assert.True(m.Decoder.CanDecode(Job(Hdr4k, null, ToneMapOn)));
        var decoded = m.Decoder.Build(Job(Hdr4k, null, ToneMapOn));
        Assert.Equal("-init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device opencl=ocl -filter_hw_device ocl " + HwAccel,
            string.Join(' ', decoded.Input));
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale_vaapi=w=-2:h=1080:extra_hw_frames=24," +
            "hwdownload,format=p010le,hwupload,tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0," +
            "hwdownload,format=nv12", string.Join(',', decoded.Filters));

        // Mesa HEVC: extradata rebuilt from the stream; no compression_level on AMD.
        var hevc = Args(m.Pipeline.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "mp4", null))));
        Assert.Contains("-c:v hevc_vaapi -profile:v main -rc_mode CQP -qp 24 -sei -a53_cc -force_key_frames expr:gte(t,n_forced*6) " +
            "-flags:v -global_header -bsf:v extract_extradata=remove=0 -tag:v hvc1", hevc);
        Assert.DoesNotContain("extract_extradata", Args(m.Pipeline.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task CompressionLevelFollowsThePresetOnIntel()
    {
        var m = await Probed();
        string Level(string preset) => Args(m.Pipeline.Build(Job(Sdr1080, null, VideoProcessing.Default with { Software = new(Preset: preset) })));
        Assert.Contains("-compression_level 1 ", Level("veryslow"));
        Assert.Contains("-compression_level 4 ", Level("medium"));
        Assert.Contains("-compression_level 6 ", Level("faster"));
        Assert.Contains("-compression_level 7 ", Level("ultrafast"));
        Assert.DoesNotContain("-compression_level", Level("auto"));
    }

    [Fact]
    public async Task CapsBitrateBelowTheClientLimit()
    {
        var m = await Probed();
        Assert.Contains("-rc_mode VBR -b:v 20000000 -maxrate 40000000 -bufsize 80000000",
            Args(m.Pipeline.Build(Job(Sdr1080, H264 with { MaxBitrate = 100_000_000 }))));
        var slow = Args(m.Pipeline.Build(Job(Sdr1080, H264 with { MaxBitrate = 4_000_000 })));
        Assert.Contains("-rc_mode VBR -b:v 1800000 -maxrate 3600000 -bufsize 7200000", slow);
        Assert.DoesNotContain("-qp", slow);
    }

    [Fact]
    public async Task KeyframesFollowTheSegmentLength()
    {
        var m = await Probed();
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*4)", Args(m.Pipeline.Build(Job(Sdr1080, segment: 4))));
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*3)", Args(m.Pipeline.Build(Job(Xvid, segment: 3))));
    }

    [Fact]
    public async Task HevcInFmp4IsTaggedForApple()
    {
        var m = await Probed();
        var mp4 = Args(m.Pipeline.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "mp4", null))));
        Assert.Contains("-c:v hevc_vaapi -profile:v main -rc_mode CQP", mp4);
        Assert.EndsWith("-tag:v hvc1", mp4);
        Assert.DoesNotContain("hvc1", Args(m.Pipeline.Build(Job(Sdr1080, new VideoTarget(false, "hevc", "ts", null)))));
    }

    [Fact]
    public async Task VaapiSectionBindsOptions()
    {
        var m = await Probed(null, ("Vaapi:Device", "/dev/dri/renderD129"), ("Vaapi:Qp", "20"), ("Vaapi:MaxConcurrent", "2"));
        Assert.Equal(2, m.Pipeline.MaxConcurrent);
        var args = Args(m.Pipeline.Build(Job(Sdr1080)));
        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD129 ", args);
        Assert.Contains("-rc_mode CQP -qp 20", args);
        Assert.Equal(4, Create().Pipeline.MaxConcurrent);
    }

    [Fact]
    public async Task ReprobesTheNewDeviceAfterASettingsChange()
    {
        var m = await Probed();
        Assert.Contains("vaapi=va:/dev/dri/renderD128", Args(m.Pipeline.Build(Job(Sdr1080))));

        // The dashboard saves a new device; the selector probes again with a freshly detected ffmpeg.
        m.Config["Vaapi:Device"] = "/dev/dri/renderD129";
        m.Config.Reload();
        var broken = new Ffmpeg(Everything, _ => false);
        Assert.False(await m.Pipeline.ProbeAsync(broken, default));
        Assert.Contains(broken.Runs, r => r.Contains("vaapi=va:/dev/dri/renderD129"));
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080)));
        Assert.False(m.Decoder.CanDecode(Job(Sdr1080)));

        var works = new Ffmpeg(Everything);
        Assert.True(await m.Pipeline.ProbeAsync(works, default));
        Assert.Contains("vaapi=va:/dev/dri/renderD129", Args(m.Pipeline.Build(Job(Sdr1080))));
    }

    [Fact]
    public async Task PipelineDoesNotTakeDecodedInput()
    {
        var m = await Probed();
        Assert.False(m.Pipeline.AcceptsDecodedInput);
        Assert.False(m.Pipeline.CanHandle(Job(Sdr1080) with { Decoded = new DecodedVideo("nvenc", [], []) }));
    }
}
