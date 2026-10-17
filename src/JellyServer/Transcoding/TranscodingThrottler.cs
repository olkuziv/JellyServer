using System.Collections.Concurrent;
using System.Diagnostics;
using JellyServer.Configuration;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Transcoding;

/// <summary>
/// "Allow FFmpeg throttling", as Jellyfin does it: ffmpeg is paused while its output is more than
/// <see cref="ThrottlingOptions.DelaySeconds"/> (at least 60 s) ahead of what the player downloaded, and resumed
/// when the gap shrinks. jellyfin-ffmpeg pauses on the <c>p</c> key and resumes on <c>u</c>, read from stdin
/// (so the run must not get <c>-nostdin</c>). Builds without those keys (vanilla ffmpeg ≥ 7) are never throttled.
/// </summary>
public static class TranscodingThrottler
{
    /// <summary>Jellyfin never lets ffmpeg throttle closer than this to the player.</summary>
    public const int MinDelaySeconds = 60;

    /// <summary>Jellyfin throttles only file-based videos of at least this length.</summary>
    public static readonly TimeSpan MinRuntime = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Support = new(StringComparer.Ordinal);

    /// <summary>Same eligibility as Jellyfin's EnableThrottling(state): a local video file of 5 minutes or more.</summary>
    public static bool IsEligible(Item item) =>
        item.RuntimeTicks >= MinRuntime.Ticks && !item.Path.Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// Jellyfin's HLS rule: pause when ffmpeg's position is at least the delay ahead of the download position.
    /// No data yet (either position 0) means no throttling.
    /// </summary>
    public static bool ShouldPause(long transcodedTicks, long downloadedTicks, int delaySeconds)
    {
        if (transcodedTicks <= 0 || downloadedTicks <= 0) return false;
        var gap = transcodedTicks - downloadedTicks;
        return gap >= TimeSpan.FromSeconds(Math.Max(delaySeconds, MinDelaySeconds)).Ticks;
    }

    /// <summary>Writes the pause / resume key to ffmpeg's stdin. False when the process is gone.</summary>
    public static bool Send(Process process, bool pause)
    {
        try
        {
            if (process.HasExited) return false;
            process.StandardInput.Write(pause ? 'p' : 'u');
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this ffmpeg lists "p pause transcoding" in its runtime key help (asked with <c>?</c> on stdin,
    /// like Jellyfin's EncoderValidator). Checked once per ffmpeg path.
    /// </summary>
    public static Task<bool> SupportsPauseAsync(string ffmpegPath) =>
        Support.GetOrAdd(ffmpegPath, p => new Lazy<Task<bool>>(() => DetectAsync(p))).Value;

    private static async Task<bool> DetectAsync(string ffmpegPath)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        foreach (var a in (string[])["-hide_banner", "-f", "lavfi", "-i", "nullsrc=s=1x1:d=10000", "-f", "null", "-"])
            psi.ArgumentList.Add(a);
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { return false; }

        using (proc)
        {
            var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () =>
            {
                // Progress lines end with \r, the key help with \n: read raw chunks.
                var buffer = new char[4096];
                var seen = new System.Text.StringBuilder();
                try
                {
                    int n;
                    while ((n = await proc.StandardError.ReadAsync(buffer)) > 0)
                    {
                        seen.Append(buffer, 0, n);
                        if (seen.ToString().Contains("pause transcoding", StringComparison.Ordinal)) { found.TrySetResult(true); break; }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                found.TrySetResult(false);
            });
            _ = proc.StandardOutput.ReadToEndAsync();
            try
            {
                proc.StandardInput.Write('?');
                proc.StandardInput.Flush();
            }
            catch (IOException) { }
            var result = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(10))) == found.Task && found.Task.Result;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return result;
        }
    }
}
