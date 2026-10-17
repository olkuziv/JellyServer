using JellyServer.Transcoding;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

public class ThrottlingTests
{
    private static long S(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    [Theory]
    [InlineData(400, 200, 180, true)]   // 200 s ahead, delay 180
    [InlineData(380, 200, 180, true)]   // exactly the delay: Jellyfin pauses (gap >= target)
    [InlineData(379, 200, 180, false)]
    [InlineData(270, 200, 10, true)]    // delay below 60 s is raised to 60
    [InlineData(259, 200, 10, false)]
    [InlineData(100, 200, 180, false)]  // player ahead of ffmpeg (seek, catching up)
    public void PausesOnlyWhenFarEnoughAhead(double transcoded, double downloaded, int delay, bool pause) =>
        Assert.Equal(pause, TranscodingThrottler.ShouldPause(S(transcoded), S(downloaded), delay));

    [Fact]
    public void NoDataMeansNoThrottling()
    {
        Assert.False(TranscodingThrottler.ShouldPause(0, S(100), 60));
        Assert.False(TranscodingThrottler.ShouldPause(S(1000), 0, 60));
    }

    [Theory]
    [InlineData(300, "/media/a.mkv", true)]
    [InlineData(299, "/media/a.mkv", false)]          // shorter than 5 minutes
    [InlineData(3600, "http://host/stream.ts", false)] // remote input
    public void EligibleLikeJellyfin(int runtimeSeconds, string path, bool eligible) =>
        Assert.Equal(eligible, TranscodingThrottler.IsEligible(new Item { RuntimeTicks = S(runtimeSeconds), Path = path }));

    [Fact]
    public async Task MissingFfmpegCannotPause() =>
        Assert.False(await TranscodingThrottler.SupportsPauseAsync(Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid())));

    [Theory]
    // running, target, start, deletedBelow, produced, restart
    [InlineData(true, 12, 0, 0, 10, false)]   // next segments: wait for the running ffmpeg
    [InlineData(true, 14, 0, 0, 10, false)]   // up to 4 ahead
    [InlineData(true, 15, 0, 0, 10, true)]    // far ahead: seek
    [InlineData(true, 3, 5, 0, 10, true)]     // before the run's start
    [InlineData(true, 3, 0, 8, 20, true)]     // deleted by segment deletion: regenerate, never 404
    [InlineData(true, 9, 0, 0, 10, true)]     // should exist but is gone: regenerate
    [InlineData(false, 11, 0, 0, 10, true)]   // ffmpeg idle-killed / finished
    [InlineData(true, 8, 8, 8, 7, false)]     // first segment of a run after deletion of earlier ones
    public void RestartDecision(bool running, int target, int start, int deletedBelow, int produced, bool restart) =>
        Assert.Equal(restart, TranscodeManager.NeedsRestart(running, target, start, deletedBelow, produced));
}
