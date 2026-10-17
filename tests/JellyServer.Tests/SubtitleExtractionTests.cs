using System.Diagnostics;
using System.Text.Json;
using JellyServer.Api;
using JellyServer.Configuration;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Transcoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Microsoft.Extensions.Options;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Media.Contracts;
using JellyServer.Catalog;

namespace JellyServer.Tests;

internal sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public sealed class SubtitleExtractionTests : IDisposable
{
    /// <summary>ffprobe (jellyfin-ffmpeg 7.1.4) of an mkv with subrip, ass, PGS, dvd_subtitle and webvtt streams.</summary>
    private const string Probe = """
        {"streams":[
        {"index":0,"codec_name":"h264","codec_type":"video","width":320,"height":240,"disposition":{"default":0,"forced":0,"hearing_impaired":0,"attached_pic":0},"tags":{"ENCODER":"Lavc61.19.101 libx264"}},
        {"index":1,"codec_name":"aac","codec_type":"audio","channels":1,"disposition":{"default":0,"forced":0,"hearing_impaired":0,"attached_pic":0}},
        {"index":2,"codec_name":"subrip","codec_type":"subtitle","disposition":{"default":1,"forced":0,"hearing_impaired":0,"attached_pic":0},"tags":{"language":"ukr","title":"Ukr full"}},
        {"index":3,"codec_name":"ass","codec_type":"subtitle","disposition":{"default":0,"forced":1,"hearing_impaired":0,"attached_pic":0},"tags":{"language":"eng"}},
        {"index":4,"codec_name":"hdmv_pgs_subtitle","codec_type":"subtitle","width":320,"height":240,"disposition":{"default":0,"forced":0,"hearing_impaired":0,"attached_pic":0},"tags":{"language":"eng"}},
        {"index":5,"codec_name":"dvd_subtitle","codec_type":"subtitle","width":320,"height":240,"disposition":{"default":0,"forced":0,"hearing_impaired":0,"attached_pic":0},"tags":{"language":"fre"}},
        {"index":6,"codec_name":"webvtt","codec_type":"subtitle","disposition":{"default":0,"forced":0,"hearing_impaired":1,"attached_pic":0},"tags":{"language":"ger"}}
        ],"format":{"duration":"10.0"}}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jelly-subs-" + Guid.NewGuid().ToString("N"));

    public SubtitleExtractionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Item ItemWith(MediaInfo? mi, string path = "/media/movie.mkv", List<SubtitleFile>? sidecars = null) => new()
    {
        Id = Guid.Parse("0123456789abcdef0123456789abcdef"), Type = ItemTypes.Movie, Path = path, Container = "mkv",
        MediaInfo = mi, Subtitles = sidecars,
    };

    // ---- probe parsing / storage ----

    [Fact]
    public void Parse_RecordsEveryEmbeddedSubtitleStream()
    {
        var mi = MediaProbe.Parse(Probe);
        Assert.Equal(["ukr", "eng", "eng", "fre", "ger"], mi.Subtitles);
        var s = mi.SubtitleStreams!;
        Assert.Equal([2, 3, 4, 5, 6], s.Select(x => x.Index));
        Assert.Equal(["subrip", "ass", "pgssub", "dvdsub", "webvtt"], s.Select(x => x.Codec));
        Assert.Equal("Ukr full", s[0].Title);
        Assert.True(s[0].IsDefault);
        Assert.True(s[1].IsForced);
        Assert.True(s[4].IsHearingImpaired);
        Assert.Null(s[1].Title);
    }

    [Fact]
    public void Parse_FileWithoutSubtitles_LeavesStreamsNull()
    {
        var mi = MediaProbe.Parse("""{"streams":[{"index":0,"codec_name":"h264","codec_type":"video","width":1920,"height":1080}]}""");
        Assert.Null(mi.SubtitleStreams);
        Assert.Empty(mi.EmbeddedSubtitles);
    }

    [Fact]
    public void LegacyCachedJson_StillLoads_AsLanguageOnlyStreams()
    {
        // Items cached in Redis/PostgreSQL before stream details existed.
        var mi = JsonSerializer.Deserialize("""{"Probed":true,"Video":[{"Codec":"h264","Width":1,"Height":1,"Aspect":0}],"Subtitles":["eng",""]}""", CatalogJson.Default.MediaInfo)!;
        Assert.Null(mi.SubtitleStreams);
        Assert.Equal([new EmbeddedSubtitle(-1, "", "eng"), new EmbeddedSubtitle(-1, "", null)], mi.EmbeddedSubtitles);
    }

    [Fact]
    public void MediaInfo_RoundTripsSubtitleStreams()
    {
        var json = JsonSerializer.Serialize(MediaProbe.Parse(Probe), CatalogJson.Default.MediaInfo);
        Assert.DoesNotContain("EmbeddedSubtitles", json);
        var back = JsonSerializer.Deserialize(json, CatalogJson.Default.MediaInfo)!;
        Assert.Equal(MediaProbe.Parse(Probe).SubtitleStreams, back.SubtitleStreams);
    }

    // ---- MediaStreams layout and delivery ----

    [Fact]
    public void Tracks_FollowVideoAndAudio_ThenSidecars()
    {
        var it = ItemWith(MediaProbe.Parse(Probe), sidecars: [new SubtitleFile("/media/movie.uk.srt", "srt", "ukr", false)]);
        var tracks = SubtitleTrack.List(it);
        Assert.Equal([2, 3, 4, 5, 6, 7], tracks.Select(t => t.Index));
        Assert.Equal([0, 1, 2, 3, 4, 0], tracks.Select(t => t.Ordinal));
        Assert.Equal([true, true, false, false, true, true], tracks.Select(t => t.IsText));
        Assert.True(tracks[^1].IsExternal);
        Assert.Equal("subrip", tracks[^1].Codec);
    }

    private static readonly SubtitleProfileDto[] Web =
    [
        new() { Format = "vtt", Method = "External" }, new() { Format = "ass", Method = "External" }, new() { Format = "ssa", Method = "External" },
    ];

    private static readonly SubtitleProfileDto[] WebWithBitmaps =
    [
        .. Web, new() { Format = "pgssub", Method = "External" }, new() { Format = "vobsub", Container = "mks", Method = "External" },
    ];

    private static SubtitleTrack Embedded(string codec) => new(2, codec, "eng", new EmbeddedSubtitle(2, codec, "eng"), 0, null);
    private static SubtitleTrack Sidecar(string codec) => new(2, codec, "eng", null, 0, new SubtitleFile("/x." + codec, codec, "eng", false));

    // Jellyfin's StreamBuilderTests.GetSubtitleProfile_RespectsExtractionSetting.
    [Theory]
    [InlineData("srt", "srt", false, false, true, "Encode")]
    [InlineData("srt", "srt", false, false, false, "External")]
    [InlineData("pgssub", "pgssub", false, false, true, "External")]
    [InlineData("pgssub", "pgssub", false, false, false, "External")]
    [InlineData("pgssub", "srt", false, false, true, "Encode")]
    [InlineData("srt", "srt", false, true, true, "External")]
    [InlineData("srt", "srt", true, false, true, "External")]
    [InlineData("pgssub", "pgssub", true, false, true, "External")]
    [InlineData("pgssub", "pgssub", true, false, false, "External")]
    [InlineData("pgssub", "srt", true, false, true, "Encode")]
    [InlineData("srt", "srt", true, true, true, "External")]
    public void Jellyfin_RespectsExtractionSetting(string codec, string profileFormat, bool extraction, bool external, bool transcode, string expected)
    {
        var track = external ? Sidecar(codec) : Embedded(codec);
        var (method, _) = SubtitleDelivery.Jellyfin(track, [new() { Format = profileFormat, Method = "External" }], transcode, "ts", hls: true, extraction);
        Assert.Equal(expected, method);
    }

    [Theory]
    // Direct play with jellyfin-web's profiles: text is always delivered as a file, styled ASS stays ASS.
    [InlineData("subrip", false, true, false, "External", "vtt")]
    [InlineData("webvtt", false, true, false, "External", "vtt")]
    [InlineData("mov_text", false, true, false, "External", "vtt")]
    [InlineData("ass", false, true, false, "External", "ass")]
    [InlineData("ssa", false, true, false, "External", "ssa")]
    // Extraction off only matters when transcoding: Jellyfin burns in, we leave it embedded.
    [InlineData("subrip", true, false, false, "Embed", "subrip")]
    [InlineData("ass", true, false, false, "Embed", "ass")]
    [InlineData("subrip", true, true, false, "External", "vtt")]
    // Image subtitles: a file only for clients that render them, otherwise embedded (no burn-in).
    [InlineData("pgssub", false, true, false, "Embed", "pgssub")]
    [InlineData("pgssub", false, true, true, "External", "pgssub")]
    [InlineData("pgssub", true, false, true, "External", "pgssub")]
    [InlineData("dvdsub", false, true, true, "External", "vobsub")]
    [InlineData("dvdsub", false, true, false, "Embed", "dvdsub")]
    // Unknown codec (NFO only): nothing can be extracted.
    [InlineData("", false, true, false, "Embed", "")]
    public void Decide_EmbeddedStreams(string codec, bool transcode, bool extraction, bool bitmaps, string method, string format)
    {
        var track = Embedded(codec);
        var result = SubtitleDelivery.Decide(track, bitmaps ? WebWithBitmaps : Web, transcode, transcode ? "ts" : "mkv", hls: transcode, extraction);
        Assert.Equal((method, format), result);
    }

    [Fact]
    public void Decide_EmbedProfile_WinsForDirectPlay_NotForHls()
    {
        SubtitleProfileDto[] profiles = [new() { Format = "srt", Method = "Embed" }, new() { Format = "vtt", Method = "External" }];
        Assert.Equal(("Embed", "srt"), SubtitleDelivery.Decide(Embedded("subrip"), profiles, false, "mkv", false, true));
        Assert.Equal(("External", "vtt"), SubtitleDelivery.Decide(Embedded("subrip"), profiles, true, "ts", true, true));
    }

    [Fact]
    public void Decide_SidecarStaysExternal_EvenWhenTranscodingWithoutExtraction()
    {
        Assert.Equal(("External", "vtt"), SubtitleDelivery.Decide(Sidecar("srt"), Web, true, "ts", true, false));
        Assert.Equal(("External", "ass"), SubtitleDelivery.Decide(Sidecar("ass"), Web, false, "mkv", false, false));
    }

    [Fact]
    public void Decide_RespectsProfileLanguage()
    {
        SubtitleProfileDto[] profiles = [new() { Format = "vtt", Method = "External", Language = "ukr" }];
        Assert.Equal(("Embed", "subrip"), SubtitleDelivery.Decide(Embedded("subrip"), profiles, false, "mkv", false, true));
    }

    private static DtoBuilder Dtos(bool extraction)
    {
        var o = new ServerOptions();
        o.Transcoding.Subtitles.Extraction = extraction;
        return new DtoBuilder(null!, null!, null!, null!, Options.Create(o), new TestMonitor<ServerOptions>(o));
    }

    [Fact]
    public void MediaSource_SetsDeliveryUrls_ForEmbeddedAndSidecarSubtitles()
    {
        var it = ItemWith(MediaProbe.Parse(Probe), sidecars: [new SubtitleFile("/media/movie.uk.srt", "srt", "ukr", true)]);
        var subs = Dtos(true).MediaSource(it).MediaStreams.Where(s => s.Type == "Subtitle").ToList();
        var id = it.Id.N();
        Assert.Equal(["External", "External", "Embed", "Embed", "External", "External"], subs.Select(s => s.DeliveryMethod));
        Assert.Equal($"/Videos/{id}/{id}/Subtitles/2/0/Stream.vtt", subs[0].DeliveryUrl);
        Assert.Equal($"/Videos/{id}/{id}/Subtitles/3/0/Stream.ass", subs[1].DeliveryUrl);
        Assert.Null(subs[2].DeliveryUrl);
        Assert.Equal($"/Videos/{id}/{id}/Subtitles/7/0/Stream.vtt", subs[5].DeliveryUrl);
        Assert.Equal("Ukr full - Ukrainian - Default - SUBRIP", subs[0].DisplayTitle);
        Assert.Equal("English - Forced - ASS", subs[1].DisplayTitle);
        Assert.Equal("Ukrainian - Forced - SUBRIP - External", subs[5].DisplayTitle);
        Assert.Equal([true, true, false, false, true, true], subs.Select(s => s.IsTextSubtitleStream));
        Assert.Equal([true, true, true, true, true, true], subs.Select(s => s.SupportsExternalStream));
    }

    [Fact]
    public void ApplySubtitleDelivery_Transcode_WithoutExtraction_KeepsEmbeddedTextEmbedded()
    {
        var it = ItemWith(MediaProbe.Parse(Probe), sidecars: [new SubtitleFile("/media/movie.uk.srt", "srt", "ukr", false)]);
        var dto = Dtos(false);
        var ms = dto.MediaSource(it);
        dto.ApplySubtitleDelivery(ms, it, [.. WebWithBitmaps], transcode: true, "ts");
        var subs = ms.MediaStreams.Where(s => s.Type == "Subtitle").ToList();
        Assert.Equal(["Embed", "Embed", "External", "External", "Embed", "External"], subs.Select(s => s.DeliveryMethod));
        Assert.EndsWith("/Subtitles/4/0/Stream.pgssub", subs[2].DeliveryUrl);
        Assert.EndsWith("/Subtitles/5/0/Stream.vobsub", subs[3].DeliveryUrl);
    }

    // ---- ffmpeg command lines and cache keys ----

    [Fact]
    public void ExtractArgs_OnePassForAllStreams_LikeJellyfin()
    {
        var tracks = SubtitleTrack.List(ItemWith(MediaProbe.Parse(Probe))).ToList();
        var args = SubtitleExtractor.ExtractArgs("/media/movie.mkv", tracks.Select(t => (t, $"/c/s{t.Ordinal}.{SubtitleExtractor.NativeExtension(t.Codec)}")));
        Assert.Equal(
            "-nostdin -y -i /media/movie.mkv " +
            "-map 0:2 -an -vn -c:s copy -flush_packets 1 /c/s0.srt " +
            "-map 0:3 -an -vn -c:s copy -flush_packets 1 /c/s1.ass " +
            "-map 0:4 -an -vn -c:s copy -flush_packets 1 /c/s2.sup " +
            "-map 0:5 -an -vn -c:s copy -f matroska -flush_packets 1 /c/s3.mks " +
            "-map 0:6 -an -vn -c:s srt -flush_packets 1 /c/s4.srt", string.Join(' ', args));
    }

    [Fact]
    public void ExtractArgs_WithoutProbedIndex_MapsNthSubtitleStream()
    {
        var t = new SubtitleTrack(5, "subrip", null, new EmbeddedSubtitle(-1, "subrip", null), 3, null);
        Assert.Contains("0:s:3", SubtitleExtractor.ExtractArgs("in.mkv", [(t, "o.srt")]));
    }

    [Theory]
    [InlineData("vtt", "webvtt")]
    [InlineData("srt", "srt")]
    [InlineData("ass", "ass")]
    public void ConvertArgs_UsesTextEncoder(string ext, string encoder) =>
        Assert.Equal(["-nostdin", "-y", "-i", "s1.ass", "-map", "0:s:0", "-c:s", encoder, "out." + ext],
            SubtitleExtractor.ConvertArgs("s1.ass", ext, "out." + ext));

    [Fact]
    public void CacheKey_IsItemStreamAndSourceRevision()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");
        var dir = SubtitleExtractor.CacheDir("/data", id);
        Assert.Equal(Path.Combine("/data", "subtitles", "0123456789abcdef0123456789abcdef"), dir);
        var t = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var stamp = SubtitleExtractor.Stamp(t, 1000);
        Assert.Equal($"{t.Ticks:x}-3e8", stamp);
        Assert.NotEqual(stamp, SubtitleExtractor.Stamp(t.AddSeconds(1), 1000));
        Assert.NotEqual(stamp, SubtitleExtractor.Stamp(t, 1001));
        Assert.Equal(Path.Combine(dir, $"s2-{stamp}.vtt"), SubtitleExtractor.CachePath(dir, 2, stamp, "vtt"));
    }

    // ---- extractor behaviour (fake ffmpeg) ----

    private sealed class FakeRunner
    {
        public int Calls;
        public List<IReadOnlyList<string>> Args = [];
        public TimeSpan Timeout;
        public int ExitCode;
        public TaskCompletionSource? Gate;

        public async Task<(int, string)> Run(IReadOnlyList<string> args, TimeSpan timeout)
        {
            Interlocked.Increment(ref Calls);
            lock (Args) Args.Add(args);
            Timeout = timeout;
            if (Gate is not null) await Gate.Task;
            if (ExitCode != 0) return (ExitCode, "boom");
            var outputs = args.Select((a, i) => (a, i)).Where(x => x.i > 0 && args[x.i - 1] == "1" && args[x.i - 2] == "-flush_packets").Select(x => x.a).ToList();
            if (outputs.Count == 0) outputs.Add(args[^1]);
            foreach (var o in outputs)
                await File.WriteAllTextAsync(o, o.EndsWith(".srt") ? "1\n00:00:01,000 --> 00:00:02,000\nHi\n\n" : "Style: Default,Arial,20\n");
            return (0, "");
        }
    }

    private (SubtitleExtractor Extractor, FakeRunner Runner, Item Item) Setup(int timeoutMinutes = 30)
    {
        var media = Path.Combine(_dir, "movie.mkv");
        File.WriteAllText(media, "not really a video");
        var o = new ServerOptions { DataDir = Path.Combine(_dir, "data") };
        o.Transcoding.Subtitles.ExtractionTimeoutMinutes = timeoutMinutes;
        var runner = new FakeRunner();
        var x = new SubtitleExtractor(new TestMonitor<ServerOptions>(o), NullLogger<SubtitleExtractor>.Instance) { Runner = runner.Run };
        return (x, runner, ItemWith(MediaProbe.Parse(Probe), media));
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneExtraction_ThenServeFromCache()
    {
        var (x, runner, item) = Setup(timeoutMinutes: 7);
        runner.Gate = new TaskCompletionSource();
        var tracks = SubtitleTrack.List(item);
        var a = x.GetAsync(item, tracks[0], "srt", CancellationToken.None);
        var b = x.GetAsync(item, tracks[1], "ass", CancellationToken.None);
        var c = x.GetAsync(item, tracks[0], "srt", CancellationToken.None);
        runner.Gate.SetResult();
        var paths = await Task.WhenAll(a, b, c);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(TimeSpan.FromMinutes(7), runner.Timeout);
        Assert.EndsWith(".srt", paths[0]);
        Assert.Equal(paths[0], paths[2]);
        // Jellyfin's SetAssFont.
        Assert.Contains(",Arial Unicode MS,", File.ReadAllText(paths[1]));
        // Every extractable stream was written in that one pass; no temp files are left.
        Assert.Equal(5, Directory.GetFiles(Path.GetDirectoryName(paths[0])!).Length);

        Assert.Equal(paths[0], await x.GetAsync(item, tracks[0], "subrip", CancellationToken.None));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task OtherFormats_AreConvertedFromTheExtractedFile_Once()
    {
        var (x, runner, item) = Setup();
        var t = SubtitleTrack.List(item)[1]; // ass
        var vtt = await x.GetAsync(item, t, "vtt", CancellationToken.None);
        Assert.EndsWith(".vtt", vtt);
        Assert.Equal(2, runner.Calls);
        var convert = runner.Args[1];
        Assert.Equal("webvtt", convert[convert.Count - 2]);
        Assert.EndsWith(".ass", convert[3]);
        await x.GetAsync(item, t, "vtt", CancellationToken.None);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task ImageSubtitles_OnlyAsTheirOwnFormat()
    {
        var (x, _, item) = Setup();
        var tracks = SubtitleTrack.List(item);
        Assert.EndsWith(".sup", await x.GetAsync(item, tracks[2], "pgssub", CancellationToken.None));
        Assert.EndsWith(".mks", await x.GetAsync(item, tracks[3], "vobsub", CancellationToken.None));
        Assert.Null(await x.GetAsync(item, tracks[2], "vtt", CancellationToken.None));
        Assert.Null(await x.GetAsync(item, tracks[0], "sup", CancellationToken.None));
    }

    [Fact]
    public async Task FailedExtraction_Throws_AndLeavesNoFiles()
    {
        var (x, runner, item) = Setup();
        runner.ExitCode = 1;
        await Assert.ThrowsAsync<SubtitleExtractionException>(() => x.GetAsync(item, SubtitleTrack.List(item)[0], "srt", CancellationToken.None));
        var dir = SubtitleExtractor.CacheDir(Path.Combine(_dir, "data"), item.Id);
        Assert.Empty(Directory.GetFiles(dir));
        runner.ExitCode = 0;
        Assert.NotNull(await x.GetAsync(item, SubtitleTrack.List(item)[0], "srt", CancellationToken.None));
    }

    [Fact]
    public async Task ChangedSource_ReExtracts_AndDropsTheOldRevision()
    {
        var (x, runner, item) = Setup();
        var t = SubtitleTrack.List(item)[0];
        var first = await x.GetAsync(item, t, "srt", CancellationToken.None);
        File.SetLastWriteTimeUtc(item.Path, DateTime.UtcNow.AddMinutes(5));
        var second = await x.GetAsync(item, t, "srt", CancellationToken.None);
        Assert.NotEqual(first, second);
        Assert.Equal(2, runner.Calls);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public async Task ClientCancellation_DoesNotCancelTheSharedExtraction()
    {
        var (x, runner, item) = Setup();
        runner.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var t = SubtitleTrack.List(item)[0];
        var aborted = x.GetAsync(item, t, "srt", cts.Token);
        var other = x.GetAsync(item, t, "srt", CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        runner.Gate.SetResult();
        Assert.NotNull(await other);
        Assert.Equal(1, runner.Calls);
    }

    /// <summary>
    /// Opt-in end-to-end check against jellyfin-ffmpeg in Docker: set JELLY_SUBS_DOCKER to a folder holding test.mkv
    /// (built by the verification script); host paths under it are mounted at /work.
    /// </summary>
    [Fact]
    public async Task RealFfmpeg_InDocker()
    {
        var root = Environment.GetEnvironmentVariable("JELLY_SUBS_DOCKER");
        if (string.IsNullOrEmpty(root)) return;
        var o = new ServerOptions { DataDir = Path.Combine(root, "data") };
        string Map(string a) => a.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "/work" + a[root.Length..].Replace('\\', '/') : a;
        async Task<(int, string)> Docker(IReadOnlyList<string> args, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo("docker") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in (string[])["run", "--rm", "-v", $"{root}:/work", "jellyserver:jf-ffmpeg-check"]) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(Map(a));
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await err);
        }
        var x = new SubtitleExtractor(new TestMonitor<ServerOptions>(o), NullLogger<SubtitleExtractor>.Instance) { Runner = Docker };
        var item = ItemWith(MediaProbe.Parse(await File.ReadAllTextAsync(Path.Combine(root, "probe.json"))), Path.Combine(root, "test.mkv"));
        var tracks = SubtitleTrack.List(item);
        var report = new List<string>();
        foreach (var (t, format) in new[] { (0, "srt"), (0, "vtt"), (0, "ass"), (1, "ass"), (1, "vtt"), (1, "srt"), (4, "vtt"), (4, "srt"), (2, "pgssub"), (3, "vobsub") })
        {
            var path = await x.GetAsync(item, tracks[t], format, CancellationToken.None);
            Assert.NotNull(path);
            report.Add($"== stream {tracks[t].Index} ({tracks[t].Codec}) as {format}: {Path.GetFileName(path)} {new FileInfo(path!).Length} bytes");
            if (format is not ("pgssub" or "vobsub")) report.Add(await File.ReadAllTextAsync(path!));
        }
        report.Add(JsonSerializer.Serialize(Subtitles.ToTrackEvents(Subtitles.ReadText((await x.GetAsync(item, tracks[1], "srt", CancellationToken.None))!)), AppJson.Default.SubtitleTrackEvents));
        await File.WriteAllLinesAsync(Path.Combine(root, "report.txt"), report);
    }
}
