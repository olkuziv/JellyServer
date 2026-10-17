using JellyServer.Api;
using JellyServer.Transcoding;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

public class PlaybackDeciderTests
{
    private static readonly HashSet<string> H264 = ["h264"];

    // Shapes taken from jellyfin-web's browserDeviceProfile.js.
    private static DeviceProfileDto IosSafari() => new()
    {
        DirectPlayProfiles =
        [
            new() { Container = "mp4,m4v", Type = "Video", VideoCodec = "h264,hevc", AudioCodec = "aac,mp3,ac3,eac3" },
            new() { Container = "mov", Type = "Video", VideoCodec = "h264", AudioCodec = "aac" },
        ],
        TranscodingProfiles =
        [
            new() { Container = "mp4", Type = "Video", Protocol = "hls", VideoCodec = "hevc,h264", AudioCodec = "aac,ac3,eac3", MaxAudioChannels = "6" },
            new() { Container = "ts", Type = "Video", Protocol = "hls", VideoCodec = "h264", AudioCodec = "aac,mp3,ac3,eac3", MaxAudioChannels = "6" },
        ],
    };

    private static DeviceProfileDto Tizen() => new()
    {
        DirectPlayProfiles = [new() { Container = "mkv,mp4,ts", Type = "Video", VideoCodec = "h264,hevc", AudioCodec = "aac,ac3,eac3,mp3" }],
        TranscodingProfiles = [new() { Container = "ts", Type = "Video", Protocol = "hls", VideoCodec = "h264,hevc", AudioCodec = "aac,ac3,eac3", MaxAudioChannels = "6" }],
    };

    private static (Item, MediaInfo) Mkv(string video, string audio, int channels = 6)
    {
        var mi = new MediaInfo { Video = [new VideoStream(video, 3840, 2160, 0, null)], Audio = [new AudioStream(audio, "ukr", channels)] };
        return (new Item { Name = "x", Container = "mkv", MediaInfo = mi }, mi);
    }

    [Fact]
    public void IosRemuxesHevcMkvIntoFmp4AndCopiesAc3()
    {
        var (item, mi) = Mkv("hevc", "ac3");
        var d = PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = IosSafari() }, true, H264);
        Assert.False(d.DirectPlay);
        Assert.Equal(new TranscodePlan(true, "hevc", true, "ac3", 6, 0, "mp4", null), d.Plan);
    }

    [Fact]
    public void IosConvertsDtsAudioOnly()
    {
        var (item, mi) = Mkv("h264", "dts");
        var plan = PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = IosSafari() }, true, H264).Plan!;
        Assert.True(plan.CopyVideo);
        Assert.False(plan.CopyAudio);
        Assert.Equal("aac", plan.AudioCodec);
    }

    [Fact]
    public void IosTranscodesUnsupportedVideo()
    {
        var (item, mi) = Mkv("mpeg4", "mp3", 2);
        var plan = PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = IosSafari() }, true, H264).Plan!;
        Assert.False(plan.CopyVideo);
        Assert.Equal("h264", plan.VideoCodec);
    }

    [Fact]
    public void TizenDirectPlaysMkv()
    {
        var (item, mi) = Mkv("hevc", "eac3");
        Assert.True(PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = Tizen() }, true, H264).DirectPlay);
    }

    [Fact]
    public void TizenRemuxesWhenDtsIsUnsupported()
    {
        var (item, mi) = Mkv("hevc", "dts");
        var plan = PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = Tizen() }, true, H264).Plan!;
        Assert.True(plan.CopyVideo);
        Assert.Equal("aac", plan.AudioCodec); // AAC is preferred whenever the client accepts it
    }

    [Fact]
    public void ForcedTranscodeAfterDirectPlayFailure()
    {
        var (item, mi) = Mkv("hevc", "eac3");
        var d = PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = Tizen(), EnableDirectPlay = false, EnableDirectStream = false }, true, H264);
        Assert.False(d.DirectPlay);
        Assert.True(d.Plan!.CopyVideo);
    }

    [Fact]
    public void HevcOutputOnlyWhenAllowedAndEncodable()
    {
        var (item, mi) = Mkv("mpeg4", "aac", 2);
        var req = new PlaybackInfoRequest { DeviceProfile = IosSafari() }; // its fMP4 profile lists "hevc,h264"
        HashSet<string> both = ["h264", "hevc"];
        Assert.Equal("h264", PlaybackDecider.Decide(item, mi, req, true, both).Plan!.VideoCodec);
        Assert.Equal("hevc", PlaybackDecider.Decide(item, mi, req, true, both, allowHevc: true).Plan!.VideoCodec);
        Assert.Equal("h264", PlaybackDecider.Decide(item, mi, req, true, H264, allowHevc: true).Plan!.VideoCodec);
    }

    [Fact]
    public void WithoutAnEncoderOnlyRemuxIsOffered()
    {
        var (item, mi) = Mkv("mpeg4", "mp3", 2);
        var d = PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest { DeviceProfile = IosSafari() }, true, new HashSet<string>());
        Assert.True(d.DirectPlay);
        Assert.Contains("no video pipeline", d.Reason);

        var (hevc, hmi) = Mkv("hevc", "dts");
        Assert.True(PlaybackDecider.Decide(hevc, hmi, new PlaybackInfoRequest { DeviceProfile = IosSafari() }, true, new HashSet<string>()).Plan!.CopyVideo);
    }

    [Fact]
    public void NoProfileMeansDirectPlay()
    {
        var (item, mi) = Mkv("hevc", "dts");
        Assert.True(PlaybackDecider.Decide(item, mi, new PlaybackInfoRequest(), true, H264).DirectPlay);
    }

    [Fact]
    public void ParsesFfprobeOutput()
    {
        var mi = MediaProbe.Parse("""
            {"streams":[
              {"codec_type":"video","codec_name":"hevc","width":3840,"height":1600,"color_transfer":"smpte2084","pix_fmt":"yuv420p10le"},
              {"codec_type":"audio","codec_name":"eac3","channels":6,"tags":{"language":"ukr"}},
              {"codec_type":"audio","codec_name":"dts","profile":"DTS-HD MA","channels":8,"tags":{"language":"eng"}},
              {"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"ukr"}},
              {"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}}],
             "format":{"duration":"7856.12","bit_rate":"24000000"}}
            """);
        Assert.True(mi.Probed);
        Assert.Single(mi.Video);
        Assert.Equal("HDR10", mi.Video[0].HdrType);
        Assert.Equal(10, mi.Video[0].BitDepth);
        Assert.Equal(["eac3", "dts"], mi.Audio.Select(a => a.Codec));
        Assert.Equal(7856.12, mi.DurationSec, 2);
    }
}
