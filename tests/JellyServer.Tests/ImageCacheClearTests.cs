using JellyServer.Configuration;
using JellyServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JellyServer.Tests;

public class ImageCacheClearTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("imgclear").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string File_(string shard, string name, int bytes, TimeSpan unusedFor)
    {
        var dir = Path.Combine(_dir, shard);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - unusedFor);
        return path;
    }

    [Fact]
    public void DeletesOnlyFilesUnusedForTheGivenAgeAndReportsWhatItFreed()
    {
        var old = File_("ab", "old.webp", 1000, TimeSpan.FromDays(40));
        var recent = File_("ab", "recent.jpg", 500, TimeSpan.FromDays(2));

        var freed = ImageCacheJanitor.ClearOlderThan(_dir, TimeSpan.FromDays(30), null, CancellationToken.None);

        Assert.Equal(1000, freed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void ZeroAgeEmptiesTheCacheAndRemovesTheEmptyFolders()
    {
        File_("ab", "a.webp", 100, TimeSpan.FromMinutes(1));
        File_("cd", "b.jpg", 100, TimeSpan.Zero);

        ImageCacheJanitor.ClearOlderThan(_dir, TimeSpan.Zero, null, CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void ATempFileBeingWrittenIsLeftAloneButAStaleOneGoes()
    {
        var fresh = File_("ab", "x.webp.tmp", 10, TimeSpan.FromMinutes(1));
        var stale = File_("ab", "y.webp.tmp", 10, TimeSpan.FromHours(3));

        ImageCacheJanitor.ClearOlderThan(_dir, TimeSpan.Zero, null, CancellationToken.None);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void MissingFolderIsNotAnError()
    {
        Assert.Equal(0, ImageCacheJanitor.ClearOlderThan(Path.Combine(_dir, "nope"), TimeSpan.Zero, null, CancellationToken.None));
    }

    [Fact]
    public async Task TheTaskCleansTheCacheOfTheImageServiceAndReportsProgress()
    {
        var options = new ServerOptions { DataDir = _dir };
        options.Images.CacheMaxAgeDays = 30;
        var images = new ImageService(Options.Create(options), new Factory(), NullLogger<ImageService>.Instance);
        var old = Path.Combine(images.CacheDir, "ab", "old.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllBytes(old, new byte[10]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-45));
        var task = new ClearImageCacheTask(images, Options.Create(options), NullLogger<ClearImageCacheTask>.Instance);
        double last = 0;

        await task.ExecuteAsync(new Progress<double>(p => last = p), CancellationToken.None);

        Assert.False(File.Exists(old));
        Assert.Equal("DeleteCacheFiles", task.Key);
        Assert.Matches("^[0-9a-f]{32}$", task.Id);
        Assert.NotEmpty(task.DefaultTriggers);
        Assert.True(last >= 0);
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network");
    }
}
