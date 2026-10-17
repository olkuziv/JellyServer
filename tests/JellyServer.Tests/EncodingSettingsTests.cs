using System.Text.Json;
using JellyServer.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JellyServer.Tests;

public class EncodingSettingsTests
{
    private static EncodingOptionsDto Post(string json) => EncodingSettings.Overlay(new EncodingOptionsDto(), JsonDocument.Parse(json).RootElement);

    /// <summary>What the server's options look like after the dashboard settings were applied on top of appsettings.</summary>
    private static TranscodingOptions Bind(EncodingOptionsDto dto, params (string Key, string Value)[] appsettings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(appsettings.Select(a => new KeyValuePair<string, string?>(a.Key, a.Value)))
            .AddInMemoryCollection(EncodingSettings.ToConfiguration(dto))
            .Build();
        return config.GetSection(ServerOptions.Section).Get<ServerOptions>()!.Transcoding;
    }

    [Fact]
    public void AcceptsTheDashboardFormsStringlyTypedValues()
    {
        // The React form posts TextField values as strings and a cleared number field as "".
        var dto = Post("""{"H264Crf":"20","EncodingThreadCount":"4","TonemappingParam":"","DownMixAudioBoost":"1.5","HardwareAccelerationType":"nvenc","Unknown":1}""");
        Assert.Equal(20, dto.H264Crf);
        Assert.Equal(4, dto.EncodingThreadCount);
        Assert.Equal(0, dto.TonemappingParam);
        Assert.Equal(1.5, dto.DownMixAudioBoost);
        Assert.Equal("nvenc", dto.HardwareAccelerationType);
        Assert.Empty(EncodingSettings.Validate(dto));
    }

    [Fact]
    public void RejectsUnknownValues()
    {
        var errors = EncodingSettings.Validate(Post("""{"HardwareAccelerationType":"cuda","H264Crf":60,"HardwareDecodingCodecs":["h264","h266"]}"""));
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void HardwareTypeDrivesPipelinesAndDecoder()
    {
        var nvenc = Bind(Post("""{"HardwareAccelerationType":"nvenc"}"""), ("JellyServer:Transcoding:Pipelines:0", "vaapi"),
            ("JellyServer:Transcoding:Pipelines:4", "leftover"));
        Assert.Equal(["nvenc", "x264", "x265", "svtav1"], nvenc.Pipelines!.Where(p => p.Length > 0));
        Assert.Equal("nvenc", nvenc.Decoder);

        var decodeOnly = Bind(Post("""{"HardwareAccelerationType":"vaapi","EnableHardwareEncoding":false}"""));
        Assert.Equal(["x264", "x265", "svtav1"], decodeOnly.Pipelines!.Where(p => p.Length > 0));
        Assert.Equal("vaapi", decodeOnly.Decoder);

        var none = Bind(Post("""{"HardwareAccelerationType":"none"}"""));
        Assert.Equal("", none.Decoder);
    }

    [Fact]
    public void MapsProcessingSettings()
    {
        var t = Bind(Post("""
            {"HardwareDecodingCodecs":[],"EnableTonemapping":true,"TonemappingAlgorithm":"hable","EncoderPreset":"fast",
             "H264Crf":21,"EnableIntelLowPowerH264HwEncoder":true,"DeinterlaceMethod":"bwdif","AllowHevcEncoding":true,
             "EnableThrottling":true,"ThrottleDelaySeconds":120,"MaxMuxingQueueSize":"8192"}
            """), ("JellyServer:Transcoding:HwEncode:LowPowerHevc", "true"));
        var p = t.ToProcessing();
        Assert.False(p.HwDecode.Allows("h264")); // an empty list means no codec, not every codec
        Assert.True(p.ToneMap.Enabled);
        Assert.Equal("hable", p.ToneMap.Algorithm);
        Assert.Equal(("fast", 21), (p.Software.Preset, p.Software.H264Crf));
        Assert.True(p.HwEncode.LowPowerH264);
        Assert.Null(p.HwEncode.LowPowerHevc); // unticked on the dashboard overrides appsettings: the module decides
        Assert.Equal("bwdif", p.Deinterlace.Method);
        Assert.True(t.AllowHevcEncoding);
        Assert.Equal((true, 120), (t.Throttling.Enabled, t.Throttling.DelaySeconds));
        Assert.Equal(8192, t.MaxMuxingQueueSize);
    }

    [Fact]
    public void FirstPageLoadShowsTheActualSetup()
    {
        var dto = EncodingSettings.FromOptions(new TranscodingOptions(), "nvenc", "/data/transcodes");
        Assert.Equal("nvenc", dto.HardwareAccelerationType);
        Assert.Equal(EncodingSettings.AllDecodeCodecs, dto.HardwareDecodingCodecs);
        Assert.True(dto.EnableTonemapping);
        // Saving that page unchanged keeps the server doing what it did.
        var t = Bind(dto);
        Assert.Equal("nvenc", t.Pipelines![0]);
        Assert.True(t.ToProcessing().HwDecode.Allows("av1"));
    }

    [Fact]
    public void SerializationRoundTrips()
    {
        var dto = Post("""{"HardwareAccelerationType":"qsv","QsvDevice":"/dev/dri/renderD129","HardwareDecodingCodecs":["hevc"]}""");
        var back = EncodingSettings.Deserialize(EncodingSettings.Serialize(dto));
        Assert.Equal(("qsv", "/dev/dri/renderD129"), (back.HardwareAccelerationType, back.QsvDevice));
        Assert.Equal(["hevc"], back.HardwareDecodingCodecs);
    }
}
