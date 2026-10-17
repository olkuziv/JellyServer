using Xunit;
using JellyServer.Tasks;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Tests;

public class TriggerScheduleTests
{
    private static readonly DateTime Start = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc); // a Friday
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Interval_CountsFromTheEndOfTheLastRun()
    {
        var end = Start.AddHours(-1);
        Assert.Equal(end.AddHours(12), TriggerSchedule.Next(TaskTrigger.Interval(TimeSpan.FromHours(12)), end.AddMinutes(-5), end, Start, Utc));
    }

    [Fact]
    public void Interval_WithoutALastRunCountsFromProcessStart()
        => Assert.Equal(Start.AddHours(12), TriggerSchedule.Next(TaskTrigger.Interval(TimeSpan.FromHours(12)), null, null, Start, Utc));

    [Fact]
    public void Interval_OverdueIsInThePast_SoItFiresAtOnce()
    {
        var end = Start.AddDays(-3);
        Assert.True(TriggerSchedule.Next(TaskTrigger.Interval(TimeSpan.FromHours(12)), end, end, Start, Utc) <= Start);
    }

    [Fact]
    public void Daily_TakesTheNextTimeOfDay()
    {
        Assert.Equal(new DateTime(2026, 10, 16, 23, 0, 0, DateTimeKind.Utc), TriggerSchedule.Next(TaskTrigger.Daily(TimeSpan.FromHours(23)), null, null, Start, Utc));
        Assert.Equal(new DateTime(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc), TriggerSchedule.Next(TaskTrigger.Daily(TimeSpan.FromHours(3)), null, null, Start, Utc));
    }

    [Fact]
    public void Daily_AfterTodaysRun_WaitsForTomorrow()
    {
        var ran = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc), TriggerSchedule.Next(TaskTrigger.Daily(TimeSpan.FromHours(9)), ran, ran.AddMinutes(1), Start, Utc));
    }

    [Fact]
    public void Daily_DoesNotCatchUpAMissedNight()
    {
        var lastRun = Start.AddDays(-2).AddHours(-5); // two days ago; the server was off since
        var next = TriggerSchedule.Next(TaskTrigger.Daily(TimeSpan.FromHours(3)), lastRun, lastRun, Start, Utc);
        Assert.True(next > Start);
    }

    [Fact]
    public void Weekly_TakesTheNextMatchingDay()
        => Assert.Equal(new DateTime(2026, 10, 19, 4, 30, 0, DateTimeKind.Utc),
            TriggerSchedule.Next(TaskTrigger.Weekly(DayOfWeek.Monday, new TimeSpan(4, 30, 0)), null, null, Start, Utc));

    [Fact]
    public void Daily_UsesServerLocalTime()
    {
        var kyiv = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3), "t", "t"); // UTC+3
        // 08:00 UTC is 11:00 local: 10:00 local is already past, so it fires at 10:00 local the next day = 07:00 UTC.
        Assert.Equal(new DateTime(2026, 10, 17, 7, 0, 0, DateTimeKind.Utc), TriggerSchedule.Next(TaskTrigger.Daily(TimeSpan.FromHours(10)), null, null, Start, kyiv));
    }

    [Fact]
    public void StartupAndBrokenTriggers_NeverFireByTheClock()
    {
        Assert.Null(TriggerSchedule.Next(TaskTrigger.Startup(), null, null, Start, Utc));
        Assert.Null(TriggerSchedule.Next(new TaskTrigger(TaskTriggerType.IntervalTrigger), null, null, Start, Utc));
        Assert.Null(TriggerSchedule.Next(new TaskTrigger(TaskTriggerType.WeeklyTrigger, TimeOfDayTicks: 1), null, null, Start, Utc));
    }
}