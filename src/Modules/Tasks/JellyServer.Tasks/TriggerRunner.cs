using JellyServer.Tasks.Contracts;

namespace JellyServer.Tasks;

/// <summary>Starts tasks when their triggers say so: startup triggers once, the others by the clock.</summary>
public sealed class TriggerRunner(TaskManager manager, TaskStore store, ILogger<TriggerRunner> log) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1); // also picks up changes other replicas saved

    private readonly DateTime _startedUtc = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            await RunStartupTriggersAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var changed = manager.Changed;
                var wait = await RunDueAsync(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(Task.Delay(wait, cts.Token), changed);
                await cts.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RunStartupTriggersAsync(CancellationToken ct)
    {
        try
        {
            var states = await store.LoadAllAsync(ct);
            foreach (var task in manager.Tasks.Where(t => t.IsEnabled))
            {
                var triggers = states.GetValueOrDefault(task.Id)?.Triggers ?? task.DefaultTriggers;
                if (triggers.FirstOrDefault(t => t.Type == TaskTriggerType.StartupTrigger) is { } startup)
                    manager.Start(task, startup.MaxRuntimeTicks, scheduled: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Startup triggers failed"); }
    }

    /// <summary>Starts what is due and returns how long to sleep until the next trigger.</summary>
    private async Task<TimeSpan> RunDueAsync(CancellationToken ct)
    {
        var wait = MaxWait;
        try
        {
            var states = await store.LoadAllAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var task in manager.Tasks.Where(t => t.IsEnabled && !manager.IsRunning(t)))
            {
                var state = states.GetValueOrDefault(task.Id);
                var last = state?.LastResult;
                (DateTime At, TaskTrigger Trigger)? next = null;
                foreach (var trigger in state?.Triggers ?? task.DefaultTriggers)
                    if (TriggerSchedule.Next(trigger, last?.StartTimeUtc, last?.EndTimeUtc, _startedUtc, TimeZoneInfo.Local) is { } at && (next is null || at < next.Value.At))
                        next = (at, trigger);
                if (next is null) continue;

                if (next.Value.At <= now) manager.Start(task, next.Value.Trigger.MaxRuntimeTicks, scheduled: true);
                else wait = TimeSpan.FromTicks(Math.Min(wait.Ticks, (next.Value.At - now).Ticks));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Task scheduling failed"); }
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }
}