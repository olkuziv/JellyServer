using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using JellyServer.Configuration;
using Microsoft.Extensions.Options;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Transcoding;

/// <summary>
/// Runs ffmpeg HLS jobs. The client gets a complete VOD playlist up front (fixed-length
/// segments for the whole runtime), so it can seek anywhere. When it asks for a segment
/// that is not produced yet and is far from ffmpeg's position, the job is restarted at
/// that segment (<c>-ss</c> + <c>-start_number</c>): seeking costs one restart, not a re-encode
/// of everything before it. Idle jobs are killed and their files cleaned up.
/// Dashboard throttling pauses ffmpeg while it is far ahead of the player (<see cref="TranscodingThrottler"/>) and
/// segment deletion drops segments far behind it (<see cref="SegmentCleaner"/>); a deleted segment asked for again
/// restarts ffmpeg there like any seek.
/// The video part of each run comes from a pipeline chosen by <see cref="PipelineSelector"/>;
/// a job keeps its pipeline across seeks and falls back to the next one when ffmpeg fails.
/// </summary>
public sealed class TranscodeManager : BackgroundService
{
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly PipelineSelector _pipelines;
    private readonly ILogger<TranscodeManager> _log;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    /// <summary>A requested segment this far beyond the produced one triggers a restart instead of waiting.</summary>
    private const int MaxSegmentsAhead = 4;

    public TranscodeManager(IOptionsMonitor<ServerOptions> options, PipelineSelector pipelines, ILogger<TranscodeManager> log)
    {
        _options = options;
        _pipelines = pipelines;
        _log = log;
        // Segments from a previous run are useless: start clean.
        var root = TempRoot;
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        Directory.CreateDirectory(root);
    }

    /// <summary>Settings can change at runtime (dashboard): read them when needed, never cache them.</summary>
    private TranscodingOptions Opt => _options.CurrentValue.Transcoding;

    /// <summary>Where new jobs put their segments; a changed path applies to jobs started afterwards.</summary>
    public string TempRoot => Opt.TempDir is { Length: > 0 } dir ? dir : Path.Combine(_options.CurrentValue.DataDir, "transcodes");

    public int ActiveJobs => _jobs.Values.Count(j => j.IsRunning);

    private sealed class Job(string key, Item item, TranscodePlan plan, string dir, TranscodingOptions opt)
    {
        public string Key { get; } = key;
        public Item Item { get; } = item;
        public TranscodePlan Plan { get; } = plan;
        /// <summary>Settings are captured once per job so restarts after a seek encode the same way.</summary>
        public TranscodingOptions Options { get; } = opt;
        public VideoJob Video { get; } = new(FfmpegCommand.Source(item), FfmpegCommand.Target(plan, opt.MaxHeight),
            opt.ToProcessing(), new SegmentTiming(opt.SegmentSeconds));
        public string Dir { get; } = dir;
        public string? PlaySessionId { get; init; }
        public string? DeviceId { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Process? Process { get; set; }
        public int StartSegment { get; set; }
        public DateTime LastAccess { get; set; } = DateTime.UtcNow;
        /// <summary>Slot on the pipeline of the current run; kept while paused segments may resume.</summary>
        public PipelineLease? Lease { get; set; }
        /// <summary>Last pipeline used, preferred on restarts so one playlist is not mixed across encoders.</summary>
        public string? PipelineId { get; set; }
        /// <summary>Pipelines that failed for this job: never retried for it.</summary>
        public HashSet<string> Failed { get; } = [];
        public bool Confirmed { get; set; }
        public Queue<string> StdErr { get; } = new();
        /// <summary>Last segment the player asked for (Jellyfin's download position); -1 before the first request.</summary>
        public int Requested { get; set; } = -1;
        /// <summary>Last position from the client's progress reports, if any.</summary>
        public long? PlayerTicks { get; set; }
        /// <summary>The run was started without <c>-nostdin</c> and can be paused with the p/u keys.</summary>
        public bool Pausable { get; set; }
        /// <summary>The throttler paused the current run.</summary>
        public bool Paused { get; set; }
        /// <summary>Segments below this index were deleted by segment deletion (current run only).</summary>
        public int DeletedBelow { get; set; }
        public bool IsRunning => Process is { HasExited: false };
        /// <summary>ffmpeg exited by itself with an error (not killed by us: <see cref="Stop"/> clears <see cref="Process"/>).</summary>
        public bool Crashed => Process is { HasExited: true, ExitCode: not 0 };

        public string SegmentPath(int index) => Path.Combine(Dir, $"{index}.{Plan.SegmentExtension}");
        public string InitPath => Path.Combine(Dir, "-1.mp4");
    }

    public static int SegmentCount(long runtimeTicks, int segmentSeconds) =>
        (int)Math.Ceiling(runtimeTicks / (double)TimeSpan.TicksPerSecond / segmentSeconds);

    /// <summary>The VOD playlist covering the whole runtime; segment URLs carry the same query string.</summary>
    public string BuildPlaylist(long runtimeTicks, TranscodePlan plan, string query)
    {
        var seg = Opt.SegmentSeconds;
        var total = runtimeTicks / (double)TimeSpan.TicksPerSecond;
        var count = SegmentCount(runtimeTicks, seg);
        var sb = new System.Text.StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append(plan.SegmentContainer == "mp4" ? "#EXT-X-VERSION:7\n" : "#EXT-X-VERSION:3\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{seg + 1}\n#EXT-X-MEDIA-SEQUENCE:0\n");
        if (plan.SegmentContainer == "mp4") sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-MAP:URI=\"hls1/main/-1.mp4?{query}\"\n");
        for (var i = 0; i < count; i++)
        {
            var duration = Math.Min(seg, total - i * seg);
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{duration:0.000000},\nhls1/main/{i}.{plan.SegmentExtension}?{query}\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// Returns the path of a finished segment (index -1 = fMP4 init segment), starting or
    /// restarting ffmpeg as needed. Null when ffmpeg failed or the segment does not exist.
    /// </summary>
    public async Task<string?> GetSegmentAsync(Item item, TranscodePlan plan, string playSessionId, string? deviceId, int index, CancellationToken ct)
    {
        var key = plan.Key(item.Id, playSessionId);
        var job = _jobs.GetOrAdd(key, k => new Job(k, item, plan, Path.Combine(TempRoot, k), Opt) { PlaySessionId = playSessionId, DeviceId = deviceId });
        job.LastAccess = DateTime.UtcNow;
        var isInit = index < 0;
        if (!isInit) job.Requested = index;
        var path = isInit ? job.InitPath : job.SegmentPath(index);
        var target = isInit ? job.StartSegment : index;

        if (File.Exists(path) && (!isInit || HasAnySegment(job))) return path;

        await job.Gate.WaitAsync(ct);
        try
        {
            var produced = HighestProduced(job);
            // Checked after the scan: a segment finished meanwhile must not look deleted (target <= produced).
            if (!File.Exists(path))
            {
                if (NeedsRestart(job.IsRunning, target, job.StartSegment, job.DeletedBelow, produced))
                {
                    if (!await StartAsync(job, isInit ? 0 : index, ct)) return null;
                }
                else if (job.Paused)
                {
                    // The player caught up with a throttled ffmpeg: resume now rather than on the next tick.
                    SetPaused(job, false);
                }
            }
        }
        finally
        {
            job.Gate.Release();
        }

        // Wait for ffmpeg to finish the segment (it writes "N.ext.tmp" and renames when complete).
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && (!isInit || HasAnySegment(job)))
            {
                ConfirmPipeline(job);
                return path;
            }
            if (!job.IsRunning)
            {
                if (File.Exists(path)) return path;
                if (await TryFallbackAsync(job, ct)) continue;
                _log.LogWarning("ffmpeg ended without producing {Segment} for {Item}: {Error}",
                    Path.GetFileName(path), item.Name, string.Join(" | ", job.StdErr));
                return null;
            }
            await Task.Delay(100, ct);
            job.LastAccess = DateTime.UtcNow;
        }
        _log.LogWarning("Timed out waiting for segment {Index} of {Item}", index, item.Name);
        return null;
    }

    private static bool HasAnySegment(Job job) =>
        Directory.Exists(job.Dir) && Directory.EnumerateFiles(job.Dir, "*." + job.Plan.SegmentExtension).Any(f => !f.EndsWith("-1.mp4"));

    /// <summary>
    /// A missing segment needs a new ffmpeg run when nothing runs, when it lies before the run's start or in the
    /// range deleted by segment deletion, when it should already exist (deleted), or when it is far ahead.
    /// Otherwise the running ffmpeg will produce it soon.
    /// </summary>
    public static bool NeedsRestart(bool running, int target, int startSegment, int deletedBelow, int produced) =>
        !running || target < Math.Max(startSegment, deletedBelow) || target <= produced
        || target > Math.Max(produced, startSegment) + MaxSegmentsAhead;

    /// <summary>Highest contiguous segment of the current run (deleted ones counted as produced).</summary>
    private static int HighestProduced(Job job)
    {
        var i = Math.Max(job.StartSegment, job.DeletedBelow);
        while (File.Exists(job.SegmentPath(i))) i++;
        return i - 1;
    }

    /// <summary>The first segment of a run appeared: the pipeline works for this source.</summary>
    private void ConfirmPipeline(Job job)
    {
        if (job.Confirmed || job.PipelineId is not { } id) return;
        job.Confirmed = true;
        _pipelines.ReportSuccess(id);
    }

    /// <summary>
    /// ffmpeg crashed before producing anything: count it against the pipeline and restart the
    /// same segment on the next suitable one. False when there is nothing left to try.
    /// </summary>
    private async Task<bool> TryFallbackAsync(Job job, CancellationToken ct)
    {
        await job.Gate.WaitAsync(ct);
        try
        {
            if (job.IsRunning) return true; // another request already restarted it
            if (!job.Crashed || job.PipelineId is not { } failed || HighestProduced(job) >= job.StartSegment) return false;
            if (job.Lease?.Job.Decoded is { } decoded)
            {
                // Hardware decoding is the likelier culprit: retry the same encoder decoding in software.
                job.Failed.Add("decode:" + decoded.DecoderId);
                _log.LogWarning("ffmpeg failed for {Item} with {Decoder} decoding into {Pipeline}: {Error}; retrying with software decoding",
                    job.Item.Name, decoded.DecoderId, failed, string.Join(" | ", job.StdErr));
            }
            else
            {
                _pipelines.ReportFailure(failed, job.Item.Id);
                job.Failed.Add(failed);
                _log.LogWarning("ffmpeg failed on pipeline {Pipeline} for {Item}: {Error}; trying the next pipeline",
                    failed, job.Item.Name, string.Join(" | ", job.StdErr));
            }
            Stop(job, releaseSlot: true);
            return await StartAsync(job, job.StartSegment, ct);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private async Task<bool> StartAsync(Job job, int startSegment, CancellationToken ct)
    {
        Stop(job, releaseSlot: false);
        if (job.Lease is null)
        {
            job.Lease = await _pipelines.AcquireAsync(job.Video, job.PipelineId, job.Failed, ct);
            if (job.Lease is null) return false;
            job.PipelineId = job.Lease.Pipeline.Id;
        }
        var pipeline = job.Lease.Pipeline;
        Directory.CreateDirectory(job.Dir);
        job.StartSegment = startSegment;
        job.DeletedBelow = 0;
        job.Confirmed = false;
        job.StdErr.Clear();

        var psi = new ProcessStartInfo(job.Options.FfmpegPath)
        {
            RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = job.Dir,
        };
        var video = pipeline.Build(job.Lease.Job);
        var args = FfmpegCommand.Build(job.Item, job.Plan, video, job.Options, startSegment, job.Dir, _pipelines.Ffmpeg);
        // Throttling talks to ffmpeg through stdin (p/u keys), like Jellyfin, which never passes -nostdin.
        // Throttling can be switched on from the dashboard at any time, so every eligible run keeps stdin open.
        job.Pausable = TranscodingThrottler.IsEligible(job.Item) && await TranscodingThrottler.SupportsPauseAsync(job.Options.FfmpegPath);
        if (job.Pausable) args.Remove("-nostdin");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        try
        {
            var proc = Process.Start(psi)!;
            job.Process = proc;
            _ = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                {
                    lock (job.StdErr)
                    {
                        job.StdErr.Enqueue(line);
                        if (job.StdErr.Count > 8) job.StdErr.Dequeue();
                    }
                }
            });
            _log.LogInformation("ffmpeg started for {Item} at segment {Segment} ({Pipeline}): {Args}", job.Item.Name, startSegment,
                pipeline.Id, string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.LogError("Cannot start ffmpeg ({Path}): {Error}", job.Options.FfmpegPath, ex.Message);
            Stop(job, releaseSlot: true);
            return false;
        }
    }

    private void Stop(Job job, bool releaseSlot)
    {
        if (job.Process is { } p)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (InvalidOperationException) { }
            p.Dispose();
            job.Process = null;
        }
        job.Paused = false;
        if (releaseSlot)
        {
            job.Lease?.Dispose();
            job.Lease = null;
        }
    }

    /// <summary>Called on playback stop / DELETE /Videos/ActiveEncodings.</summary>
    public void StopSession(string? playSessionId, string? deviceId)
    {
        foreach (var job in _jobs.Values.Where(j =>
                     (playSessionId is not null && j.PlaySessionId == playSessionId) ||
                     (playSessionId is null && deviceId is not null && j.DeviceId == deviceId)).ToList())
        {
            Remove(job, "playback stopped");
        }
    }

    /// <summary>
    /// Playback progress report (/Sessions/Playing/Progress), Jellyfin's PingTranscodingJob: a playing client keeps
    /// its jobs alive even while it plays from its buffer and ffmpeg is throttled; a paused one does not, so
    /// idle kill still frees the encoder. The position also bounds segment deletion.
    /// </summary>
    public void ReportPosition(string? playSessionId, long? positionTicks, bool isPaused)
    {
        if (string.IsNullOrEmpty(playSessionId)) return;
        foreach (var job in _jobs.Values)
        {
            if (job.PlaySessionId != playSessionId) continue;
            if (positionTicks is { } p) job.PlayerTicks = p;
            if (!isPaused) job.LastAccess = DateTime.UtcNow;
        }
    }

    /// <summary>Sends p/u to a pausable run; the state only changes when ffmpeg got the key.</summary>
    private void SetPaused(Job job, bool pause)
    {
        if (job.Paused == pause || !job.Pausable || job.Process is not { } p) return;
        if (!TranscodingThrottler.Send(p, pause)) return;
        job.Paused = pause;
        _log.LogDebug("{Action} ffmpeg for {Item}", pause ? "Throttled" : "Resumed", job.Item.Name);
    }

    private static long SegmentEndTicks(int index, int segmentSeconds) => (index + 1L) * segmentSeconds * TimeSpan.TicksPerSecond;

    /// <summary>Jellyfin's throttler tick: pause when far enough ahead of the player, resume otherwise.</summary>
    private async Task ThrottleAsync(Job job, CancellationToken ct)
    {
        if (!job.Pausable || !job.IsRunning) return;
        await job.Gate.WaitAsync(ct);
        try
        {
            if (!job.IsRunning) return;
            var seg = job.Options.SegmentSeconds;
            var throttling = Opt.Throttling;
            var pause = throttling.Enabled && job.Requested >= 0
                && TranscodingThrottler.ShouldPause(SegmentEndTicks(HighestProduced(job), seg), SegmentEndTicks(job.Requested, seg), throttling.DelaySeconds);
            SetPaused(job, pause);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    /// <summary>
    /// Jellyfin's segment cleaner tick. The player's position is the lower of the last requested segment and the
    /// last progress report, so a seek back (either signal) protects the segments it needs.
    /// </summary>
    private void CleanSegments(Job job)
    {
        var deletion = Opt.SegmentDeletion;
        if (!deletion.Enabled || job.Item.RuntimeTicks < TranscodingThrottler.MinRuntime.Ticks || job.Requested < 0) return;
        var seg = job.Options.SegmentSeconds;
        int MaxIndex()
        {
            var position = SegmentEndTicks(job.Requested, seg);
            if (job.PlayerTicks is { } played) position = Math.Min(position, played);
            return SegmentCleaner.MaxIndexToDelete(position, deletion.KeepSeconds, seg);
        }
        var max = MaxIndex();
        if (max < 0) return;
        var deleted = SegmentCleaner.Delete(job.Dir, job.Plan.SegmentExtension, max, i => i <= MaxIndex());
        if (deleted.Count == 0) return;
        // Only the deleted prefix of the current run counts, never segments it has not produced yet.
        var below = Math.Max(job.StartSegment, job.DeletedBelow);
        while (deleted.Contains(below)) below++;
        job.DeletedBelow = below;
        _log.LogDebug("Deleted {Count} old segments ({First}-{Last}) of {Item}", deleted.Count, deleted[0], deleted[^1], job.Item.Name);
    }

    private void Remove(Job job, string reason)
    {
        if (!_jobs.TryRemove(job.Key, out _)) return;
        Stop(job, releaseSlot: true);
        try { if (Directory.Exists(job.Dir)) Directory.Delete(job.Dir, true); } catch (IOException) { }
        _log.LogInformation("Transcode job for {Item} removed ({Reason})", job.Item.Name, reason);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Jellyfin's throttler checks every 5 s and its segment cleaner every 20 s.
        _ = TranscodingThrottler.SupportsPauseAsync(Opt.FfmpegPath);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;
                var clean = ++tick % 4 == 0;
                foreach (var job in _jobs.Values.ToList())
                {
                    var idle = now - job.LastAccess;
                    if (idle > Opt.CleanupAfter) Remove(job, $"idle {idle.TotalMinutes:0} min");
                    else if (idle > Opt.IdleKillAfter && job.IsRunning)
                    {
                        // Paused or abandoned: free the CPU, keep segments for a quick resume.
                        await job.Gate.WaitAsync(ct);
                        try { Stop(job, releaseSlot: true); }
                        finally { job.Gate.Release(); }
                        _log.LogInformation("Paused ffmpeg for {Item} after {Seconds:0}s without requests", job.Item.Name, idle.TotalSeconds);
                    }
                    else
                    {
                        await ThrottleAsync(job, ct);
                        if (clean)
                        {
                            await job.Gate.WaitAsync(ct);
                            try { CleanSegments(job); }
                            finally { job.Gate.Release(); }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var job in _jobs.Values.ToList()) Remove(job, "shutdown");
        }
    }
}
