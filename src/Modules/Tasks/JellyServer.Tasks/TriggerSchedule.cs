using JellyServer.Tasks.Contracts;

namespace JellyServer.Tasks;

/// <summary>When a trigger fires next. Pure, so the rules can be tested without a clock or a database.</summary>
public static class TriggerSchedule
{
    /// <summary>
    /// The next time <paramref name="trigger"/> wants the task to run, or null for a trigger that never fires again (startup, bad values).
    /// An interval counts from the end of the last run (from process start when there was none) and, when overdue, fires right away.
    /// Daily and weekly triggers only look forward from the later of the last start and the process start: a missed night is not caught up.
    /// </summary>
    public static DateTime? Next(TaskTrigger trigger, DateTime? lastStartUtc, DateTime? lastEndUtc, DateTime processStartUtc, TimeZoneInfo zone)
    {
        switch (trigger.Type)
        {
            case TaskTriggerType.IntervalTrigger:
                return trigger.IntervalTicks is > 0 and var ticks ? (lastEndUtc ?? processStartUtc).AddTicks(ticks) : null;

            case TaskTriggerType.DailyTrigger or TaskTriggerType.WeeklyTrigger:
                if (trigger.TimeOfDayTicks is not { } tod || tod < 0 || tod >= TimeSpan.TicksPerDay) return null;
                if (trigger.Type == TaskTriggerType.WeeklyTrigger && trigger.DayOfWeek is null) return null;
                var after = lastStartUtc is { } s && s > processStartUtc ? s : processStartUtc;
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(after, DateTimeKind.Utc), zone);
                var day = local.Date;
                for (var i = 0; i < 8; i++, day = day.AddDays(1))
                {
                    if (trigger.Type == TaskTriggerType.WeeklyTrigger && day.DayOfWeek != trigger.DayOfWeek) continue;
                    var at = day.AddTicks(tod);
                    if (at > local) return ToUtc(at, zone);
                }
                return null;

            default:
                return null;
        }
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        // A local time that does not exist (the hour skipped when clocks go forward) fires an hour later.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }
}