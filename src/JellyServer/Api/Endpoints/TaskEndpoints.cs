using JellyServer.Serialization;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Dashboard "Scheduled Tasks": list, start, stop and re-schedule the tasks modules register.</summary>
public static class TaskEndpoints
{
    public static void Map(WebApplication app)
    {
        var authed = app.MapGroup("").RequireToken();
        authed.MapGet("/ScheduledTasks", List);
        authed.MapGet("/ScheduledTasks/{id}", Get);

        var admin = app.MapGroup("").RequireAdmin();
        admin.MapPost("/ScheduledTasks/Running/{id}", Start);
        admin.MapDelete("/ScheduledTasks/Running/{id}", Stop);
        admin.MapPost("/ScheduledTasks/{id}/Triggers", SetTriggers);
    }

    private static async Task<IResult> List(HttpContext ctx, ITaskManager tasks)
    {
        var hidden = ctx.Request.Query.Bool("isHidden");
        var enabled = ctx.Request.Query.Bool("isEnabled");
        var list = (await tasks.ListAsync(ctx.RequestAborted))
            .Where(s => (hidden is null || s.Task.IsHidden == hidden) && (enabled is null || s.Task.IsEnabled == enabled))
            .OrderBy(s => s.Task.Category, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Task.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto).ToList();
        return Results.Json(list, AppJson.Default.ListScheduledTaskInfo);
    }

    // jellyfin-web's task page (#/dashboard/tasks/{id}) spins forever on a 404.
    private static async Task<IResult> Get(string id, ITaskManager tasks, CancellationToken ct) =>
        await tasks.GetAsync(id, ct) is { } snapshot ? Results.Json(ToDto(snapshot), AppJson.Default.ScheduledTaskInfo) : Results.NotFound();

    private static IResult Start(string id, ITaskManager tasks) => tasks.Start(id) ? Results.NoContent() : Results.NotFound();

    private static IResult Stop(string id, ITaskManager tasks) => tasks.Cancel(id) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> SetTriggers(HttpContext ctx, string id, ITaskManager tasks)
    {
        var body = await ctx.Request.ReadFromJsonAsync(AppJson.Default.ListTaskTriggerInfo, ctx.RequestAborted) ?? [];
        var triggers = new List<TaskTrigger>();
        foreach (var info in body)
        {
            if (ToTrigger(info) is not { } trigger) return Results.BadRequest($"Unknown trigger type '{info.Type}'");
            triggers.Add(trigger);
        }
        return await tasks.SetTriggersAsync(id, triggers, ctx.RequestAborted) ? Results.NoContent() : Results.NotFound();
    }

    private static TaskTrigger? ToTrigger(TaskTriggerInfo i)
    {
        if (!Enum.TryParse<TaskTriggerType>(i.Type, ignoreCase: true, out var type)) return null;
        DayOfWeek? day = Enum.TryParse<DayOfWeek>(i.DayOfWeek, ignoreCase: true, out var d) ? d : null;
        return new TaskTrigger(type, i.TimeOfDayTicks, i.IntervalTicks, day, i.MaxRuntimeTicks);
    }

    internal static ScheduledTaskInfo ToDto(TaskSnapshot s) => new()
    {
        Name = s.Task.Name,
        State = s.State.ToString(),
        CurrentProgressPercentage = s.ProgressPercent,
        Id = s.Task.Id,
        Key = s.Task.Key,
        Description = s.Task.Description,
        Category = s.Task.Category,
        IsHidden = s.Task.IsHidden,
        IsEnabled = s.Task.IsEnabled,
        Triggers = [.. s.Triggers.Select(t => new TaskTriggerInfo
        {
            Type = t.Type.ToString(), TimeOfDayTicks = t.TimeOfDayTicks, IntervalTicks = t.IntervalTicks,
            DayOfWeek = t.DayOfWeek?.ToString(), MaxRuntimeTicks = t.MaxRuntimeTicks,
        })],
        LastExecutionResult = s.LastResult is { } r ? new ScheduledTaskExecutionInfo
        {
            StartTimeUtc = r.StartTimeUtc, EndTimeUtc = r.EndTimeUtc, Status = r.Status.ToString(), Name = s.Task.Name, Key = s.Task.Key, Id = s.Task.Id,
            ErrorMessage = r.ErrorMessage, LongErrorMessage = r.LongErrorMessage,
        } : null,
    };
}