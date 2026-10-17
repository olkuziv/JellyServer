using JellyServer.Configuration;
using JellyServer.Transcoding.Pipelines;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding;

public sealed record PipelineStatus(string Id, int InUse, int MaxConcurrent, int ConsecutiveFailures, DateTime? DisabledUntil);

/// <summary>
/// Picks the video pipeline for each ffmpeg run. Pipelines are probed at startup and again whenever the
/// transcoding settings change (dashboard save), then tried in the configured order
/// (<c>Transcoding:Pipelines</c>); each has its own concurrency limit. Software encoders can be fed by the
/// configured hardware decoder (<c>Transcoding:Decoder</c>). A pipeline that keeps failing on different
/// items is benched for a while so jobs fall back to the next one.
/// </summary>
public sealed class PipelineSelector : IDisposable
{
    private const int FailuresBeforeBench = 3;
    private static readonly TimeSpan BenchFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(5);

    private readonly IEnumerable<IVideoPipeline> _registered;
    private readonly IEnumerable<IVideoDecoder> _decoders;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<PipelineSelector> _log;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _subscription;
    private State _state = new([], null, false, new HashSet<string>());

    /// <summary>The probed result: active pipelines in order and the decoder for software encoders.</summary>
    private sealed record State(IReadOnlyList<Slot> Active, IVideoDecoder? Decoder, bool Available, IReadOnlySet<string> Encodable);

    public PipelineSelector(IEnumerable<IVideoPipeline> registered, IEnumerable<IVideoDecoder> decoders,
        IOptionsMonitor<ServerOptions> options, ILogger<PipelineSelector> log)
    {
        _registered = registered;
        _decoders = decoders;
        _options = options;
        _log = log;
        // Dashboard saves reload configuration: probe again so new devices or hardware types take effect.
        _subscription = options.OnChange(changed => Task.Run(async () =>
        {
            try { await InitializeAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogError(ex, "Re-probing video pipelines failed"); }
        }));
    }

    /// <summary>True once ffmpeg was found; remuxing is then always possible.</summary>
    public bool IsAvailable => _state.Available;

    /// <summary>Codecs that at least one working pipeline can encode to.</summary>
    public IReadOnlySet<string> EncodableCodecs => _state.Encodable;

    /// <summary>The first working hardware pipeline (as a Jellyfin HardwareAccelerationType), or null.</summary>
    public string? ActiveHardware => _state.Active.Select(s => s.Pipeline).FirstOrDefault(p => !p.AcceptsDecodedInput && p.Id != CopyPipeline.PipelineId)?.Id
        ?? _state.Decoder?.Id;

    public IEnumerable<PipelineStatus> Status => _state.Active.Select(s =>
        new PipelineStatus(s.Pipeline.Id, s.InUse, s.Pipeline.MaxConcurrent, s.Failures, s.BenchedUntil > DateTime.UtcNow ? s.BenchedUntil : (DateTime?)null));

    public string? DecoderId => _state.Decoder?.Id;

    /// <summary>Capabilities of the detected ffmpeg, or null when it is missing.</summary>
    public IFfmpegCapabilities? Ffmpeg { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var opt = _options.CurrentValue.Transcoding;
        if (!opt.Enabled)
        {
            _state = new State([], null, false, new HashSet<string>());
            return;
        }
        var ffmpeg = await FfmpegCapabilities.DetectAsync(opt.FfmpegPath, ct);
        if (ffmpeg is null)
        {
            _log.LogWarning("ffmpeg not found at {Path}: conversion is disabled, every client gets direct play", opt.FfmpegPath);
            _state = new State([], null, false, new HashSet<string>());
            return;
        }
        await InitializeAsync(ffmpeg, ct);
    }

    /// <summary>Probes the registered pipelines and decoders against an already detected ffmpeg.</summary>
    public async Task InitializeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        await _probeGate.WaitAsync(ct);
        try
        {
            var opt = _options.CurrentValue.Transcoding;
            var active = new List<Slot>();
            foreach (var pipeline in Ordered(opt.Pipelines))
            {
                if (await pipeline.ProbeAsync(ffmpeg, ct)) active.Add(SlotFor(pipeline));
                else _log.LogWarning("Video pipeline {Id} is not usable with this ffmpeg/hardware; skipping it", pipeline.Id);
            }

            IVideoDecoder? decoder = null;
            var hardwareFirst = active.Select(s => s.Pipeline.Id).Where(id => _decoders.Any(d => d.Id == id));
            var wanted = opt.Decoder is null ? hardwareFirst.Concat(_decoders.Select(d => d.Id)) : [opt.Decoder];
            foreach (var id in wanted.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_decoders.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } d) continue;
                if (await d.ProbeAsync(ffmpeg, ct)) { decoder = d; break; }
                _log.LogWarning("Hardware decoder {Id} is not usable with this ffmpeg/hardware", d.Id);
            }

            _state = new State(active, decoder, true, active.SelectMany(s => s.Pipeline.OutputCodecs).ToHashSet());
            Ffmpeg = ffmpeg;
            _log.LogInformation("Video pipelines in order: {Pipelines}; hardware decoding for software encoders: {Decoder}",
                string.Join(", ", active.Select(s => s.Pipeline.MaxConcurrent > 0 ? $"{s.Pipeline.Id} (max {s.Pipeline.MaxConcurrent})" : s.Pipeline.Id)),
                decoder?.Id ?? "off");
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>Slots survive re-probes, so jobs that hold one keep counting against the limit.</summary>
    private Slot SlotFor(IVideoPipeline pipeline)
    {
        lock (_slots)
        {
            if (!_slots.TryGetValue(pipeline.Id, out var slot) || slot.Pipeline != pipeline) _slots[pipeline.Id] = slot = new Slot(pipeline);
            return slot;
        }
    }

    /// <summary>Stream copy first, then encoders in configured order (or registration order).</summary>
    private IEnumerable<IVideoPipeline> Ordered(string[]? configured)
    {
        var byId = _registered.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        if (byId.Remove(CopyPipeline.PipelineId, out var copy)) yield return copy;
        if (configured is null)
        {
            foreach (var p in byId.Values) yield return p;
            yield break;
        }
        foreach (var id in configured.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (byId.Remove(id, out var p)) yield return p;
            else _log.LogWarning("Transcoding:Pipelines lists unknown or duplicate pipeline {Id}", id);
        }
        foreach (var id in byId.Keys) _log.LogInformation("Video pipeline {Id} is not listed in Transcoding:Pipelines; it stays off", id);
    }

    /// <summary>
    /// Takes a slot on the first suitable pipeline, trying <paramref name="preferred"/> first so a job keeps its
    /// pipeline across seeks. Software encoders get the hardware decoder attached when it can take the source.
    /// <paramref name="exclude"/> holds failed pipeline ids and <c>decode:{id}</c> for a failed decoder.
    /// Waits briefly when every candidate is at its limit.
    /// </summary>
    public async Task<PipelineLease?> AcquireAsync(VideoJob job, string? preferred, IReadOnlySet<string> exclude, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SlotWait;
        while (true)
        {
            var state = _state;
            var now = DateTime.UtcNow;
            var decoder = state.Decoder is { } d && !exclude.Contains("decode:" + d.Id) && d.CanDecode(job) ? d : null;
            var candidates = new List<(Slot Slot, VideoJob Job)>();
            foreach (var slot in state.Active.OrderBy(s => s.Pipeline.Id == preferred ? 0 : 1))
            {
                if (exclude.Contains(slot.Pipeline.Id) || slot.BenchedUntil > now) continue;
                var withDecoder = decoder is not null && slot.Pipeline.AcceptsDecodedInput ? job with { Decoded = decoder.Build(job) } : null;
                if (withDecoder is not null && slot.Pipeline.CanHandle(withDecoder)) candidates.Add((slot, withDecoder));
                else if (slot.Pipeline.CanHandle(job)) candidates.Add((slot, job));
            }
            if (candidates.Count == 0)
            {
                var s = job.Source;
                _log.LogWarning("No video pipeline can convert {Codec} {Width}x{Height}{Hdr} to {Target}", s.Codec, s.Width, s.Height,
                    s.IsHdr ? " " + s.HdrType : "", job.Target.Copy ? "copy" : job.Target.Codec);
                return null;
            }
            foreach (var (slot, built) in candidates)
                if (slot.TryEnter()) return new PipelineLease(slot, built);
            if (now >= deadline)
            {
                _log.LogWarning("Every suitable video pipeline is at its limit ({Pipelines}); refusing conversion",
                    string.Join(", ", candidates.Select(c => $"{c.Slot.Pipeline.Id} {c.Slot.InUse}/{c.Slot.Pipeline.MaxConcurrent}")));
                return null;
            }
            await Task.Delay(200, ct);
        }
    }

    /// <summary>
    /// ffmpeg exited with an error before producing anything on this pipeline. Only failures on
    /// different items count: one broken file retried by a client must not bench the pipeline.
    /// </summary>
    public void ReportFailure(string id, Guid itemId)
    {
        if (id == CopyPipeline.PipelineId || Find(id) is not { } slot) return; // copy is the only way to remux: never bench it
        lock (slot.FailedItems)
        {
            if (!slot.FailedItems.Add(itemId) || slot.FailedItems.Count < FailuresBeforeBench) return;
            slot.FailedItems.Clear();
            slot.BenchedUntil = DateTime.UtcNow + BenchFor;
        }
        _log.LogError("Video pipeline {Id} failed on {Count} different items in a row; benched until {Until:HH:mm:ss}",
            id, FailuresBeforeBench, slot.BenchedUntil);
    }

    public void ReportSuccess(string id)
    {
        if (Find(id) is not { } slot) return;
        lock (slot.FailedItems) slot.FailedItems.Clear();
    }

    private Slot? Find(string id) => _state.Active.FirstOrDefault(s => s.Pipeline.Id == id);

    public void Dispose()
    {
        _subscription?.Dispose();
        _probeGate.Dispose();
    }

    internal sealed class Slot(IVideoPipeline pipeline)
    {
        private int _inUse;

        public IVideoPipeline Pipeline { get; } = pipeline;
        /// <summary>Items that failed since the last success (lock on the set itself).</summary>
        public HashSet<Guid> FailedItems { get; } = [];
        public int Failures { get { lock (FailedItems) return FailedItems.Count; } }
        public DateTime BenchedUntil { get; set; } = DateTime.MinValue;
        public int InUse => Volatile.Read(ref _inUse);

        public bool TryEnter()
        {
            var max = Pipeline.MaxConcurrent;
            while (true)
            {
                var current = Volatile.Read(ref _inUse);
                if (max > 0 && current >= max) return false;
                if (Interlocked.CompareExchange(ref _inUse, current + 1, current) == current) return true;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _inUse);
    }
}

/// <summary>A held slot on one pipeline plus the job to build on it; disposing frees the slot (once).</summary>
public sealed class PipelineLease : IDisposable
{
    private readonly PipelineSelector.Slot _slot;
    private int _released;

    internal PipelineLease(PipelineSelector.Slot slot, VideoJob job) => (_slot, Job) = (slot, job);

    public IVideoPipeline Pipeline => _slot.Pipeline;

    /// <summary>The job as it will run, including <see cref="VideoJob.Decoded"/> when a hardware decoder was attached.</summary>
    public VideoJob Job { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _slot.Exit();
    }
}
