using System.Threading.Channels;
using JellyServer.Catalog.Contracts;
using JellyServer.Scanners.Movies;
using JellyServer.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyServer.Tests;

public class VideoVersionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jsv").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static FileEntry F(string name) => new(name, 1, 1);

    private static string[] Names(List<List<FileEntry>> groups, int i) => groups[i].Select(f => f.Name).ToArray();

    [Fact]
    public void MovieFolder_FilesNamedAfterTheFolder_AreOneMovieWithVersions()
    {
        var groups = VideoVersions.ForMovieFolder("Movie (2020)",
            [F("Movie (2020) - 720p.mkv"), F("Movie (2020).mkv"), F("Movie (2020) - 1080p.mkv"), F("Movie (2020) [Director's cut].mkv")]);
        Assert.Single(groups);
        // The file named exactly like the folder is the main one, then resolutions from high to low, then the rest.
        Assert.Equal(["Movie (2020).mkv", "Movie (2020) - 1080p.mkv", "Movie (2020) - 720p.mkv", "Movie (2020) [Director's cut].mkv"], Names(groups, 0));
    }

    [Fact]
    public void MovieFolder_ABrokenFile_KeepsEveryFileAMovieOfItsOwn()
    {
        var groups = VideoVersions.ForMovieFolder("Collection", [F("Collection - Part 1.mkv"), F("Another Film.mkv")]);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void MovieFolder_NamesThatOnlyShareAPrefixWord_AreNotVersions()
    {
        // "Movie 2" continues the folder name with a letter-or-digit: a different movie.
        Assert.Equal(2, VideoVersions.ForMovieFolder("Movie", [F("Movie.mkv"), F("Movie 2.mkv")]).Count);
    }

    [Fact]
    public void ByKey_GroupsTheSameEpisode_AndKeepsOtherFilesAlone()
    {
        var files = new[] { "a - S01E01 - 720p.mkv", "a - S01E02.mkv", "a - S01E01 - 1080p.mkv", "extra.mkv" };
        var groups = VideoVersions.ByKey(files, f => f.Contains("S01E01") ? "1x1" : f.Contains("S01E02") ? "1x2" : null, f => f);
        Assert.Equal(3, groups.Count);
        Assert.Equal(["a - S01E01 - 1080p.mkv", "a - S01E01 - 720p.mkv"], groups[0]);
    }

    private List<Item> ScanMovieFolder(string folder)
    {
        var channel = Channel.CreateUnbounded<Item>();
        var library = new Library { Id = Guid.NewGuid(), Name = "Test", CollectionType = "movies" };
        var ctx = new ScanContext(library, new Dictionary<Guid, string>(), channel.Writer, 1, NullLogger.Instance);
        ctx.SetRoot(_root);
        new MovieFolderScanner(ctx).ScanDir(folder, CancellationToken.None);
        channel.Writer.Complete();
        return channel.Reader.ReadAllAsync().ToBlockingEnumerable().ToList();
    }

    [Fact]
    public void Scanner_TwoFilesOfOneMovie_BecomeOneItemWithAVersion()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Bâan Межі дорослості (2025) [tmdbid-1508427]")).FullName;
        File.WriteAllText(Path.Combine(dir, "Bâan Межі дорослості (2025) [tmdbid-1508427] - субтитри UaAniSub.strm"), "http://example.test/a.m3u8");
        File.WriteAllText(Path.Combine(dir, "Bâan Межі дорослості (2025) [tmdbid-1508427] - субтитри Ustian.strm"), "http://example.test/b.m3u8");

        var items = ScanMovieFolder(dir);

        var movie = Assert.Single(items);
        Assert.Equal("http://example.test/a.m3u8", movie.StreamUrl);
        var version = Assert.Single(movie.Versions!);
        Assert.Equal("http://example.test/b.m3u8", version.StreamUrl);
        Assert.NotEqual(movie.Id, version.Id);
        Assert.Equal(movie.Fingerprint.Length == 0 ? version.Fingerprint : movie.Fingerprint, version.Fingerprint);

        // The view used for playback carries the version's file and id, and points back to the movie.
        var view = movie.AsVersion(version);
        Assert.Equal((version.Id, movie.Id, version.StreamUrl), (view.Id, view.PrimaryId, view.StreamUrl));
        Assert.Equal(movie.Name, view.Name);
        Assert.Null(view.Versions);
    }

    [Fact]
    public void Scanner_UnrelatedFilesInOneFolder_StayTwoMovies()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Films")).FullName;
        File.WriteAllText(Path.Combine(dir, "One (2001).strm"), "http://example.test/1");
        File.WriteAllText(Path.Combine(dir, "Two (2002).strm"), "http://example.test/2");
        Assert.Equal(2, ScanMovieFolder(dir).Count);
    }
}