using JellyServer.Configuration;
using JellyServer.Transcoding;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

/// <summary>Audio part of the ffmpeg command: Jellyfin's VBR, stereo downmix, boost and HLS audio seek strategy.</summary>
public class AudioArgsTests
{
    private const string Ac4_51 = "pan=stereo|c0=c0+0.707*c2+0.707*c4|c1=c1+0.707*c2+0.707*c5";
    private const string Side71 = "pan=5.1(side)|c0=c0|c1=c1|c2=c2|c3=c3|c4=0.707*c4+0.707*c6|c5=0.707*c5+0.707*c7,";
    private static readonly FakeFfmpeg Fdk = new("libfdk_aac", "aac");

    private static Item Source(int channels, string codec = "ac3") => new()
    {
        Name = "x", Path = "/media/in.mkv",
        MediaInfo = new MediaInfo { Video = [new VideoStream("h264", 1920, 1080, 0, null)], Audio = [new AudioStream(codec, "eng", channels)] },
    };

    private static TranscodePlan Encode(string codec, int channels, bool copyVideo = false) =>
        new(copyVideo, "h264", false, codec, channels, 0, "ts", null);

    private static TranscodePlan Copy(string codec, int channels, bool copyVideo = false) =>
        new(copyVideo, "h264", true, codec, channels, 0, "ts", null);

    private static string Args(Item item, TranscodePlan plan, AudioOptions? options = null, IFfmpegCapabilities? ffmpeg = null) =>
        string.Join(' ', FfmpegCommand.AudioArgs(item, plan, options ?? new AudioOptions(), ffmpeg));

    [Fact]
    public void DefaultsKeepChannelsWhenNotDownmixing()
    {
        Assert.Equal("-c:a aac -ac 6 -b:a 576k", Args(Source(6), Encode("aac", 6)));
        Assert.Equal("-c:a aac -ac 2 -b:a 192k", Args(Source(2), Encode("aac", 2)));
        // 7.1 to 5.1 is a plain -ac: Jellyfin's algorithms and boost apply to stereo output only.
        Assert.Equal("-c:a ac3 -ac 6 -b:a 576k", Args(Source(8), Encode("ac3", 6)));
    }

    [Fact]
    public void DefaultStereoDownmixBoostsLikeJellyfin()
    {
        // Algorithm None: ffmpeg's -ac 2 matrix, then DownMixAudioBoost (default 2) as a volume filter.
        Assert.Equal("-c:a aac -ac 2 -b:a 192k -af volume=2", Args(Source(6), Encode("aac", 2)));
        Assert.Equal("-c:a aac -ac 2 -b:a 192k -af volume=1.5", Args(Source(8), Encode("aac", 2), new() { DownMixBoost = 1.5 }));
        Assert.Equal("-c:a aac -ac 2 -b:a 192k", Args(Source(6), Encode("aac", 2), new() { DownMixBoost = 1 }));
        // Unknown source channels: nothing to downmix.
        Assert.Equal("-c:a aac -ac 2 -b:a 192k", Args(Source(0), Encode("aac", 2)));
    }

    [Theory]
    [InlineData("Dave750", 6, "pan=stereo|c0=0.5*c2+0.707*c0+0.707*c4+0.5*c3|c1=0.5*c2+0.707*c1+0.707*c5+0.5*c3")]
    [InlineData("Dave750", 8, Side71 + "pan=stereo|c0=0.5*c2+0.707*c0+0.707*c4+0.5*c3|c1=0.5*c2+0.707*c1+0.707*c5+0.5*c3")]
    [InlineData("NightmodeDialogue", 6, "pan=stereo|c0=c2+0.30*c0+0.30*c4|c1=c2+0.30*c1+0.30*c5")]
    [InlineData("NightmodeDialogue", 8, Side71 + "pan=stereo|c0=c2+0.30*c0+0.30*c4|c1=c2+0.30*c1+0.30*c5")]
    [InlineData("Rfc7845", 5, "pan=stereo|c0=0.460186*c2+0.650802*c0+0.563611*c3+0.325401*c4|c1=0.460186*c2+0.650802*c1+0.563611*c4+0.325401*c3")]
    [InlineData("Rfc7845", 6, "pan=stereo|c0=0.374107*c2+0.529067*c0+0.458186*c4+0.264534*c5+0.374107*c3|c1=0.374107*c2+0.529067*c1+0.458186*c5+0.264534*c4+0.374107*c3")]
    [InlineData("Rfc7845", 7, "pan=stereo|c0=0.321953*c2+0.455310*c0+0.394310*c5+0.227655*c6+0.278819*c4+0.321953*c3|c1=0.321953*c2+0.455310*c1+0.394310*c6+0.227655*c5+0.278819*c4+0.321953*c3")]
    [InlineData("Rfc7845", 8, "pan=stereo|c0=0.274804*c2+0.388631*c0+0.336565*c6+0.194316*c7+0.336565*c4+0.194316*c5+0.274804*c3|c1=0.274804*c2+0.388631*c1+0.336565*c7+0.194316*c6+0.336565*c5+0.194316*c4+0.274804*c3")]
    [InlineData("Ac4", 5, "pan=stereo|c0=c0+0.707*c2+0.707*c3|c1=c1+0.707*c2+0.707*c4")]
    [InlineData("ac4", 6, Ac4_51)]
    [InlineData("Ac4", 8, Side71 + "pan=stereo|c0=c0+0.707*c2+0.707*c4|c1=c1+0.707*c2+0.707*c5")]
    public void AlgorithmReplacesAcAndAddsBoost(string algorithm, int sourceChannels, string pan) =>
        Assert.Equal($"-c:a aac -b:a 192k -af {pan},volume=2",
            Args(Source(sourceChannels), Encode("aac", 2), new() { StereoDownmix = algorithm }));

    [Fact]
    public void AlgorithmWithoutFilterForLayoutFallsBackToAc()
    {
        // Jellyfin has no Dave750 filter for 4.0 (4 channels): plain -ac 2 plus boost.
        Assert.Equal("-c:a aac -ac 2 -b:a 192k -af volume=2", Args(Source(4), Encode("aac", 2), new() { StereoDownmix = "Dave750" }));
        Assert.Equal($"-c:a aac -b:a 192k -af {Ac4_51}", Args(Source(6), Encode("aac", 2), new() { StereoDownmix = "Ac4", DownMixBoost = 1 }));
    }

    [Fact]
    public void PicksAacEncoderLikeJellyfin()
    {
        Assert.Equal("-c:a libfdk_aac -ac 2 -b:a 192k", Args(Source(2), Encode("aac", 2), ffmpeg: Fdk));
        Assert.Equal("-c:a aac_at -ac 2 -b:a 192k", Args(Source(2), Encode("aac", 2), ffmpeg: new FakeFfmpeg("aac_at", "libfdk_aac")));
        Assert.Equal("-c:a aac -ac 2 -b:a 192k", Args(Source(2), Encode("aac", 2), ffmpeg: new FakeFfmpeg("aac")));
    }

    [Fact]
    public void VbrPerEncoder()
    {
        var vbr = new AudioOptions { Vbr = true };
        Assert.Equal("-c:a libfdk_aac -ac 2 -vbr:a 5", Args(Source(2), Encode("aac", 2), vbr, Fdk));
        Assert.Equal("-c:a libfdk_aac -ac 6 -vbr:a 5", Args(Source(6), Encode("aac", 6), vbr, Fdk));
        Assert.Equal("-c:a libfdk_aac -ac 1 -vbr:a 5", Args(Source(1), Encode("aac", 1), vbr, Fdk));
        Assert.Equal("-c:a libmp3lame -ac 2 -qscale:a 2", Args(Source(2), Encode("mp3", 2), vbr));
        Assert.Equal("-c:a libmp3lame -ac 1 -abr:a 1 -b:a 128000", Args(Source(1), Encode("mp3", 1), vbr));
        Assert.Equal("-c:a aac_at -ac 2 -aac_at_mode:a 2 -b:a 192000", Args(Source(2), Encode("aac", 2), vbr, new FakeFfmpeg("aac_at")));
        // Jellyfin has no VBR mode for the native aac, ac3 and eac3 encoders.
        Assert.Equal("-c:a aac -ac 2 -b:a 192k", Args(Source(2), Encode("aac", 2), vbr));
        Assert.Equal("-c:a ac3 -ac 6 -b:a 576k", Args(Source(6), Encode("ac3", 6), vbr));
        Assert.Equal("-c:a eac3 -ac 6 -b:a 576k", Args(Source(6), Encode("eac3", 6), vbr));
        Assert.Equal($"-c:a libfdk_aac -vbr:a 5 -af {Ac4_51},volume=2",
            Args(Source(6), Encode("aac", 2), new() { Vbr = true, StereoDownmix = "Ac4" }, Fdk));
    }

    [Theory]
    [InlineData(20_000, 1, "1")]
    [InlineData(60_000, 2, "1")]
    [InlineData(80_000, 2, "2")]
    [InlineData(100_000, 2, "3")]
    [InlineData(150_000, 2, "4")]
    [InlineData(192_000, 2, "5")]
    public void FdkVbrModeFollowsBitratePerChannel(int bitrate, int channels, string mode) =>
        Assert.Equal(["-vbr:a", mode], FfmpegCommand.VbrArgs("libfdk_aac", bitrate, channels)!);

    [Theory]
    [InlineData(96_000, "-abr:a 1 -b:a 96000")]
    [InlineData(100_000, "-qscale:a 6")]
    [InlineData(150_000, "-qscale:a 4")]
    [InlineData(200_000, "-qscale:a 2")]
    [InlineData(240_000, "-qscale:a 0")]
    [InlineData(256_000, "-abr:a 1 -b:a 256000")]
    public void LameVbrOnlyInItsGoodRange(int bitrate, string expected) =>
        Assert.Equal(expected, string.Join(' ', FfmpegCommand.VbrArgs("libmp3lame", bitrate, 2)!));

    [Fact]
    public void MpThreeIsLimitedToStereo()
    {
        Assert.Equal("-c:a libmp3lame -ac 2 -b:a 192k -af volume=2", Args(Source(6), Encode("mp3", 6)));
    }

    [Fact]
    public void TrimCopiedAudioKeepsCopy()
    {
        Assert.Equal("-c:a copy", Args(Source(6), Copy("ac3", 6)));
        Assert.Equal("-c:a copy", Args(Source(6), Copy("ac3", 6), new() { SeekStrategy = "TrimCopiedAudio" }));
    }

    [Fact]
    public void TranscodeAudioReencodesCopiedAudioWhenVideoIsEncoded()
    {
        var transcode = new AudioOptions { SeekStrategy = "TranscodeAudio" };
        Assert.Equal("-c:a ac3 -ac 6 -b:a 576k", Args(Source(6), Copy("ac3", 6), transcode));
        Assert.Equal("-c:a libfdk_aac -ac 2 -b:a 192k", Args(Source(2, "aac"), Copy("aac", 2), transcode, Fdk));
        // eac3 7.1 copy becomes 5.1 eac3 (encoder limit).
        Assert.Equal("-c:a eac3 -ac 6 -b:a 576k", Args(Source(8, "eac3"), Copy("eac3", 8), transcode));
        // Remuxed video starts on a keyframe before -ss anyway: Jellyfin keeps copying (as it does for codecs it cannot encode).
        Assert.Equal("-c:a copy", Args(Source(6), Copy("ac3", 6, copyVideo: true), transcode));
        Assert.Equal("-c:a copy", Args(Source(8, "truehd"), Copy("truehd", 8), transcode));
    }

    [Fact]
    public void FullCommandWithTranscodeAudioFromSegment1()
    {
        var plan = Copy("ac3", 6);
        var video = new VideoArgs([], ["-c:v", "libx264"]);
        var options = new TranscodingOptions { Audio = new AudioOptions { SeekStrategy = "TranscodeAudio" } };
        var cmd = string.Join(' ', FfmpegCommand.Build(Source(6), plan, video, options, 1, "/tmp/job"));
        Assert.Contains("-ss 6 -fflags +genpts -i /media/in.mkv", cmd);
        Assert.Contains("-c:v libx264 -c:a ac3 -ac 6 -b:a 576k -copyts", cmd);
    }
}
