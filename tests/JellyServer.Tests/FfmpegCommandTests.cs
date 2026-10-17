using JellyServer.Configuration;
using JellyServer.Transcoding;
using JellyServer.Transcoding.Pipelines;
using JellyServer.Transcoding.X264;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

/// <summary>
/// Full ffmpeg command lines for the built-in pipelines. The expected strings were captured from
/// the implementation before encoders became modules, so they pin the behaviour of that split.
/// </summary>
public class FfmpegCommandTests
{
    private const string Dir = "/tmp/job";
    private static readonly string Ts = Path.Combine(Dir, "%d.ts");
    private static readonly string Mp4 = Path.Combine(Dir, "%d.mp4");
    private static readonly string Playlist = Path.Combine(Dir, "ffmpeg.m3u8");
    private const string Head = "-hide_banner -loglevel error -nostdin -y";
    private const string Tail = "-copyts -avoid_negative_ts disabled -start_at_zero -max_muxing_queue_size 4096 -max_delay 5000000 -f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event";
    private const string X264 = "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1 -pix_fmt yuv420p -force_key_frames expr:gte(t,n_forced*6) -sc_threshold 0";

    private static async Task<string> Command(Item item, TranscodePlan plan, int start)
    {
        var sp = new ServiceCollection().AddLogging().AddX264Pipeline(new ConfigurationBuilder().Build()).BuildServiceProvider();
        IVideoPipeline pipeline = plan.CopyVideo ? new CopyPipeline() : sp.GetServices<IVideoPipeline>().Single(p => p.Id == "x264");
        Assert.True(await pipeline.ProbeAsync(FakeFfmpeg.Full, default));
        var opt = new TranscodingOptions();
        var video = pipeline.Build(new VideoJob(FfmpegCommand.Source(item), FfmpegCommand.Target(plan, opt.MaxHeight), opt.ToProcessing(), new SegmentTiming(6)));
        return string.Join(' ', FfmpegCommand.Build(item, plan, video, opt, start, Dir));
    }

    private static Item Make(string codec, int width, int height, string? hdr) => new()
    {
        Name = "x", Path = "/media/in.mkv",
        MediaInfo = new MediaInfo { Video = [new VideoStream(codec, width, height, 0, hdr)], Audio = [new AudioStream("ac3", "ukr", 6)] },
    };

    [Fact]
    public async Task RemuxHevcIntoFmp4()
    {
        var cmd = await Command(Make("hevc", 3840, 2160, null), new TranscodePlan(true, "hevc", true, "ac3", 6, 0, "mp4", null), 0);
        Assert.Equal($"{Head} -noaccurate_seek -fflags +genpts -i /media/in.mkv -map 0:v:0 -map 0:a:0? -sn -dn -map_metadata -1 -map_chapters -1 " +
                     $"-c:v copy -tag:v hvc1 -c:a copy {Tail} -start_number 0 -hls_flags temp_file+independent_segments " +
                     $"-hls_segment_filename {Mp4} -hls_segment_type fmp4 -hls_fmp4_init_filename -1.mp4 {Playlist}", cmd);
    }

    [Fact]
    public async Task RemuxH264IntoTsFromSegment5()
    {
        var cmd = await Command(Make("h264", 1920, 1080, null), new TranscodePlan(true, "h264", false, "aac", 2, 1, "ts", null), 5);
        Assert.Equal($"{Head} -ss 30 -noaccurate_seek -fflags +genpts -i /media/in.mkv -map 0:v:0 -map 0:a:1? -sn -dn -map_metadata -1 -map_chapters -1 " +
                     $"-c:v copy -bsf:v h264_mp4toannexb -c:a aac -ac 2 -b:a 192k {Tail} -start_number 5 -hls_flags temp_file+independent_segments " +
                     $"-hls_segment_filename {Ts} -hls_segment_type mpegts {Playlist}", cmd);
    }

    [Fact]
    public async Task X264ToneMapsAndScales4kHdr()
    {
        var cmd = await Command(Make("hevc", 3840, 2160, "HDR10"), new TranscodePlan(false, "h264", false, "aac", 6, 0, "mp4", 20_000_000), 0);
        Assert.Equal($"{Head} -fflags +genpts -i /media/in.mkv -map 0:v:0 -map 0:a:0? -sn -dn -map_metadata -1 -map_chapters -1 {X264} " +
                     "-vf setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=-2:1080," +
                     "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv " +
                     $"-maxrate 18000000 -bufsize 36000000 -c:a aac -ac 6 -b:a 576k {Tail} -start_number 0 -hls_flags temp_file+independent_segments " +
                     $"-hls_segment_filename {Mp4} -hls_segment_type fmp4 -hls_fmp4_init_filename -1.mp4 {Playlist}", cmd);
    }

    [Fact]
    public async Task X264Sdr1080FromSegment3()
    {
        var cmd = await Command(Make("mpeg4", 1920, 1080, null), new TranscodePlan(false, "h264", false, "mp3", 2, 0, "ts", null), 3);
        Assert.Equal($"{Head} -ss 18 -fflags +genpts -i /media/in.mkv -map 0:v:0 -map 0:a:0? -sn -dn -map_metadata -1 -map_chapters -1 {X264} " +
                     $"-c:a libmp3lame -ac 2 -b:a 192k -af volume=2 {Tail} -start_number 3 -hls_flags temp_file+independent_segments " +
                     $"-hls_segment_filename {Ts} -hls_segment_type mpegts {Playlist}", cmd);
    }

    [Fact]
    public void ParsesFfmpegListings()
    {
        const string encoders = """
            Encoders:
             V..... = Video
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
             V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
            """;
        const string filters = """
            Filters:
              T.. = Timeline support
             ... tonemap           V->V       Conversion to/from different dynamic ranges.
             ..C zscale            V->V       Apply resizing, colorspace and bit depth conversion.
            """;
        Assert.Equal(["libx264", "h264_vaapi", "aac"], FfmpegCapabilities.ParseEncoders(encoders.ReplaceLineEndings("\n")));
        Assert.Equal(["tonemap", "zscale"], FfmpegCapabilities.ParseFilters(filters.ReplaceLineEndings("\n")));
        Assert.Equal(["vaapi", "cuda"], FfmpegCapabilities.ParseHwAccels("Hardware acceleration methods:\nvaapi\ncuda\n\n"));
    }
}
