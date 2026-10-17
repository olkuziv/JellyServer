using Xunit;
using static JellyServer.Transcoding.Vaapi.Tests.VaapiPipelineTests;

namespace JellyServer.Transcoding.Vaapi.Tests;

public class VaapiDecoderTests
{
    private const string Input = "-init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi";

    private static string Filters(DecodedVideo d) => string.Join(',', d.Filters);

    [Fact]
    public async Task ProbesDecodingOnItsOwn()
    {
        // Only the decoder is configured (hardware encoding off): it probes the device itself.
        var ffmpeg = new Ffmpeg(Everything.Except(["h264_vaapi", "hevc_vaapi", "av1_vaapi"]).ToArray());
        var m = Create();
        Assert.True(await m.Decoder.ProbeAsync(ffmpeg, default));
        Assert.Equal("vaapi", m.Decoder.Id);
        Assert.Contains(ffmpeg.Runs, r => r.Contains("-c:v libx264 -y ") && r.EndsWith("h264.mkv"));
        Assert.Contains(ffmpeg.Runs, r => r.Contains($"{Input} -i ") && r.EndsWith("-vf scale_vaapi=format=nv12,hwdownload,format=nv12 -f null -"));
        Assert.Empty(m.Pipeline.OutputCodecs);

        // Nothing decodes on this GPU.
        var none = Create();
        Assert.False(await none.Decoder.ProbeAsync(new Ffmpeg(Everything, r => !r.Contains("-hwaccel vaapi")), default));
    }

    [Fact]
    public async Task DownloadsNv12AfterGpuScaling()
    {
        var m = await Probed();
        var d = m.Decoder.Build(Job(Hevc4k10));
        Assert.Equal("vaapi", d.DecoderId);
        Assert.Equal(Input, string.Join(' ', d.Input));
        Assert.Equal("scale_vaapi=w=-2:h=1080:format=nv12:extra_hw_frames=24,hwdownload,format=nv12", Filters(d));
        Assert.Equal("scale_vaapi=format=nv12:extra_hw_frames=24,hwdownload,format=nv12", Filters(m.Decoder.Build(Job(Sdr1080))));
        Assert.Equal("scale_vaapi=w=-2:h=480:format=nv12:extra_hw_frames=24,hwdownload,format=nv12",
            Filters(m.Decoder.Build(Job(Sdr1080, H264 with { MaxHeight = 480 }))));
    }

    [Fact]
    public async Task OnlyTakesSourcesTheGpuDecodes()
    {
        var m = await Probed();
        Assert.True(m.Decoder.CanDecode(Job(Sdr1080)));
        Assert.False(m.Decoder.CanDecode(Job(Xvid)));
        Assert.False(m.Decoder.CanDecode(Job(Sdr1080, null, VideoProcessing.Default with { HwDecode = new(Codecs: new HashSet<string> { "hevc" }) })));
        Assert.False(m.Decoder.CanDecode(Job(Hevc4k10, null, VideoProcessing.Default with { HwDecode = new(Hevc10Bit: false) })));
        Assert.False(m.Decoder.CanDecode(Job(Hevc4k10 with { Profile = "Rext" })));
        Assert.True(m.Decoder.CanDecode(Job(Hevc4k10 with { Profile = "Rext" }, null, VideoProcessing.Default with { HwDecode = new(HevcRext10Bit: true) })));
        Assert.Equal("scale_vaapi=w=-2:h=1080:format=nv12:extra_hw_frames=24,hwdownload,format=nv12",
            Filters(m.Decoder.Build(Job(Hevc4k10 with { Profile = "Rext" }, null, VideoProcessing.Default with { HwDecode = new(HevcRext10Bit: true) }))));
    }

    [Fact]
    public async Task Deinterlaces()
    {
        var m = await Probed();
        var i30 = Sdr1080 with { Interlaced = true, FrameRate = 29.97 };
        Assert.Equal("deinterlace_vaapi=rate=frame,scale_vaapi=format=nv12:extra_hw_frames=24,hwdownload,format=nv12", Filters(m.Decoder.Build(Job(i30))));
        Assert.StartsWith("deinterlace_vaapi=rate=field,",
            Filters(m.Decoder.Build(Job(i30, null, VideoProcessing.Default with { Deinterlace = new("yadif", DoubleRate: true) }))));
    }

    [Fact]
    public async Task ToneMapsWithVppOrOpenClOrNotAtAll()
    {
        var m = await Probed();
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale_vaapi=w=-2:h=1080:extra_hw_frames=24," +
            "procamp_vaapi=b=16,tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709:extra_hw_frames=32,hwdownload,format=nv12",
            Filters(m.Decoder.Build(Job(Hdr4k, null, Vpp))));
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale_vaapi=w=-2:h=1080:extra_hw_frames=24," +
            "hwmap=derive_device=opencl:mode=read,tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=hable:peak=100:desat=0," +
            "hwdownload,format=nv12", Filters(m.Decoder.Build(Job(Hdr4k, null, ToneMapOn with { ToneMap = new(true, "hable") }))));
        Assert.Equal(Input, string.Join(' ', m.Decoder.Build(Job(Hdr4k, null, ToneMapOn)).Input)); // interop needs no extra device
        Assert.Equal("setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale_vaapi=w=-2:h=1080:format=nv12:extra_hw_frames=24," +
            "hwdownload,format=nv12", Filters(m.Decoder.Build(Job(Hdr4k, null, NoToneMap))));

        // Requested but impossible: the software encoder decodes and tone-maps itself.
        var none = await Probed(new Ffmpeg(Everything, r => !r.Contains("tonemap_")));
        Assert.False(none.Decoder.CanDecode(Job(Hdr4k, null, ToneMapOn)));
        Assert.False(none.Decoder.CanDecode(Job(Hdr4k, null, Vpp)));
        Assert.True(none.Decoder.CanDecode(Job(Hdr4k, null, NoToneMap)));
    }
}
