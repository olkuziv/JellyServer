using System.Collections.Concurrent;
using JellyServer.Kernel.Caching;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Tasks;

/// <summary>Runs tasks (by hand or by trigger), tracks what is running and answers for the dashboard.</summary>
public sealed class TaskManager(
    IEnumerable<IScheduledTask> tasks, TaskStore store, RedisCache cache, IHostApplicationLifetime lifetime, ILogger<TaskManager> log) : ITaskManager
{
    private sealed class Run(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public volatile bool Cancelling;
        public double Percent;
    }

    private readonly IReadOnlyList<IScheduledTask> _tasks = [.. tasks];
    private readonly ConcurrentDictionary<string, Run> _running = new();
    private TaskCompletionSource _changed = NewSignal();

    public IReadOnlyList<IScheduledTask> Tasks => _tasks;

    /// <summary>Completes when a task finished or its triggers changed; the scheduler plans again then.</summary>
    public Task Changed => _changed.Task;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();

    public IScheduledTask? Find(string idOrKey) =>
        _tasks.FirstOrDefault(t => t.Id.Equals(idOrKey, StringComparison.OrdinalIgnoreCase) || t.Key.Equals(idOrKey, StringComparison.OrdinalIgnoreCase));

    public bool IsRunning(IScheduledTask task) => _running.ContainsKey(task.Id);

    public async Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct = default)
    {
        var states = await store.LoadAllAsync(ct);
        return [.. _tasks.Select(t => Snapshot(t, states.GetValueOrDefault(t.Id)))];
    }

    public async Task<TaskSnapshot?> GetAsync(string idOrKey, CancellationToken ct = default)
    {
        if (Find(idOrKey) is not { } task) return null;
        return Snapshot(task, (await store.LoadAllAsync(ct)).GetValueOrDefault(task.Id));
    }

    public TaskSnapshot Snapshot(IScheduledTask task, TaskStore.State? state)
    {
        _running.TryGetValue(task.Id, out var run);
        return new TaskSnapshot(task, run is null ? TaskState.Idle : run.Cancelling ? TaskState.Cancelling : TaskState.Running,
            run is null ? null : Math.Round(Volatile.Read(ref run.Percent), 1), state?.Triggers ?? task.DefaultTriggers, state?.LastResult);
    }

    public bool Start(string idOrKey) => Find(idOrKey) is { } task && Start(task, maxRuntimeTicks: null, scheduled: false);

    /// <summary>Starts the task in the background; <paramref name="scheduled"/> runs also take a cluster-wide lock so replicas do not all fire at once.</summary>
    public bool Start(IScheduledTask task, long? maxRuntimeTicks, bool scheduled)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        var run = new Run(cts);
        if (!_running.TryAdd(task.Id, run))
        {
            cts.Dispose();
            return false;
        }
        if (maxRuntimeTicks is > 0) cts.CancelAfter(TimeSpan.FromTicks(maxRuntimeTicks.Value));
        _ = Task.Run(() => RunAsync(task, run, scheduled));
        Signal();
        return true;
    }

    public bool Cancel(string idOrKey)
    {
        if (Find(idOrKey) is not { } task || !_running.TryGetValue(task.Id, out var run)) return false;
        run.Cancelling = true;
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public async Task<bool> SetTriggersAsync(string idOrKey, IReadOnlyList<TaskTrigger> triggers, CancellationToken ct = default)
    {
        if (Find(idOrKey) is not { } task) return false;
        await store.SaveTriggersAsync(task.Id, triggers, ct);
        Signal();
        return true;
    }

    private async Task RunAsync(IScheduledTask task, Run run, bool scheduled)
    {
        var start = DateTime.UtcNow;
        var status = TaskCompletionStatus.Completed;
        string? error = null, longError = null;
        IAsyncDisposable? clusterLock = null;
        var skipped = false; // another replica already runs it
        try
        {
            if (scheduled)
            {
                clusterLock = await cache.TryLockAsync("task:" + task.Id, TimeSpan.FromMinutes(2));
                skipped = clusterLock is null;
            }
            if (!skipped)
            {
                log.LogInformation("Task {Task} started", task.Name);
                await task.ExecuteAsync(new Progress<double>(p => Volatile.Write(ref run.Percent, Math.Clamp(p, 0, 100))), run.Cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            status = lifetime.ApplicationStopping.IsCancellationRequested ? TaskCompletionStatus.Aborted : TaskCompletionStatus.Cancelled;
        }
        catch (Exception ex)
        {
            status = TaskCompletionStatus.Failed;
            error = ex.Message;
            longError = ex.ToString();
            log.LogError(ex, "Task {Task} failed", task.Name);
        }
        finally
        {
            if (clusterLock is not null) await clusterLock.DisposeAsync();
        }

        try
        {
            if (!skipped)
                await store.SaveResultAsync(task.Id, new TaskResult(start, DateTime.UtcNow, status, error, longError), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Task {Task}: result not saved", task.Name);
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            run.Cts.Dispose();
            Signal();
        }
    }
}
