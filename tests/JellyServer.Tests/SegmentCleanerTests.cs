using JellyServer.Transcoding;
using Xunit;

namespace JellyServer.Tests;

public class SegmentCleanerTests
{
    private static long S(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    [Theory]
    [InlineData(0, 720, 6, -1)]
    [InlineData(720, 720, 6, -1)]     // not past the keep window yet
    [InlineData(726, 720, 6, 1)]      // (726-720)/6 = 1 → segments 0..1
    [InlineData(1000, 720, 6, 46)]    // 280 s behind → 46 full segments
    [InlineData(100, 5, 6, 13)]       // keep below 20 s is raised to 20: (100-20)/6
    [InlineData(25, 20, 6, -1)]       // index 0 alone is never deleted (Jellyfin: idxMax > 0)
    public void DeletesOnlySegmentsBehindTheKeepWindow(double position, int keep, int segment, int expected) =>
        Assert.Equal(expected, SegmentCleaner.MaxIndexToDelete(S(position), keep, segment));

    [Fact]
    public void DeletesOldSegmentsOnly()
    {
        var dir = Directory.CreateTempSubdirectory("segclean").FullName;
        try
        {
            foreach (var name in (string[])["-1.mp4", "0.mp4", "1.mp4", "2.mp4", "3.mp4", "4.mp4", "5.mp4.tmp", "ffmpeg.m3u8"])
                File.WriteAllText(Path.Combine(dir, name), "x");

            var deleted = SegmentCleaner.Delete(dir, "mp4", 2, _ => true);

            Assert.Equal([0, 1, 2], deleted);
            Assert.Equal(["-1.mp4", "3.mp4", "4.mp4", "5.mp4.tmp", "ffmpeg.m3u8"],
                Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SeekBackDuringDeletionWins()
    {
        var dir = Directory.CreateTempSubdirectory("segclean").FullName;
        try
        {
            for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(dir, $"{i}.ts"), "x");
            // The player jumped back to segment 2 while the pass was running: from then on only < 2 may go.
            var limit = 4;
            var deleted = SegmentCleaner.Delete(dir, "ts", 4, i => { if (i == 1) limit = 1; return i <= limit; });

            Assert.Equal([0, 1], deleted);
            Assert.True(File.Exists(Path.Combine(dir, "2.ts")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingDirectoryIsFine() =>
        Assert.Empty(SegmentCleaner.Delete(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "ts", 10, _ => true));
}
