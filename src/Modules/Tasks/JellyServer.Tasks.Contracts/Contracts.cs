namespace JellyServer.Tasks.Contracts;

public enum TaskTriggerType { DailyTrigger, WeeklyTrigger, IntervalTrigger, StartupTrigger }

public enum TaskState { Idle, Cancelling, Running }

public enum TaskCompletionStatus { Completed, Failed, Cancelled, Aborted }

/// <summary>When a task starts by itself (Jellyfin's <c>TaskTriggerInfo</c>). Times of day are server local time.</summary>
public sealed record TaskTrigger(
    TaskTriggerType Type, long? TimeOfDayTicks = null, long? IntervalTicks = null, DayOfWeek? DayOfWeek = null, long? MaxRuntimeTicks = null)
{
    public static TaskTrigger Startup() => new(TaskTriggerType.StartupTrigger);
    public static TaskTrigger Interval(TimeSpan every) => new(TaskTriggerType.IntervalTrigger, IntervalTicks: every.Ticks);
    public static TaskTrigger Daily(TimeSpan timeOfDay) => new(TaskTriggerType.DailyTrigger, TimeOfDayTicks: timeOfDay.Ticks);
    public static TaskTrigger Weekly(DayOfWeek day, TimeSpan timeOfDay) => new(TaskTriggerType.WeeklyTrigger, TimeOfDayTicks: timeOfDay.Ticks, DayOfWeek: day);
}

/// <summary>How the last run of a task went.</summary>
public sealed record TaskResult(
    DateTime StartTimeUtc, DateTime EndTimeUtc, TaskCompletionStatus Status, string? ErrorMessage = null, string? LongErrorMessage = null);

/// <summary>
/// A job the server runs on a schedule and the dashboard lists (Scheduled Tasks). Modules register implementations in DI;
/// the Tasks module runs them by their triggers, shows progress and lets an administrator start, stop and re-schedule them.
/// </summary>
public interface IScheduledTask
{
    /// <summary>Stable identifier clients use in URLs (32 hex digits).</summary>
    string Id { get; }

    /// <summary>Stable name clients look tasks up by (e.g. <c>RefreshLibrary</c>).</summary>
    string Key { get; }

    string Name { get; }
    string Description { get; }
    string Category { get; }
    bool IsHidden => false;
    bool IsEnabled => true;

    /// <summary>The triggers used until an administrator changes them.</summary>
    IReadOnlyList<TaskTrigger> DefaultTriggers { get; }

    /// <summary>Does the work; <paramref name="progress"/> takes percent values (0-100). Cancelled through <paramref name="ct"/>.</summary>
    Task ExecuteAsync(IProgress<double> progress, CancellationToken ct);
}

/// <summary>A task as the dashboard sees it.</summary>
public sealed record TaskSnapshot(
    IScheduledTask Task, TaskState State, double? ProgressPercent, IReadOnlyList<TaskTrigger> Triggers, TaskResult? LastResult);

public interface ITaskManager
{
    Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct = default);
    Task<TaskSnapshot?> GetAsync(string idOrKey, CancellationToken ct = default);

    /// <summary>Starts the task in the background; false when it is unknown or already running.</summary>
    bool Start(string idOrKey);

    /// <summary>Asks a running task to stop; false when it is unknown or not running.</summary>
    bool Cancel(string idOrKey);

    /// <summary>Replaces the triggers; false when the task is unknown.</summary>
    Task<bool> SetTriggersAsync(string idOrKey, IReadOnlyList<TaskTrigger> triggers, CancellationToken ct = default);
}