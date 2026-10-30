using System.Text.Json;
using JellyServer.Api;
using JellyServer.Api.Endpoints;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Realtime;

/// <summary><c>ScheduledTasksInfo</c>: what the dashboard's task page and "Scan library" button show live.</summary>
public sealed class ScheduledTasksTopic : PeriodicTopic
{
    private readonly ITaskManager _tasks;

    public ScheduledTasksTopic(ITaskManager tasks)
    {
        _tasks = tasks;
        tasks.StateChanged += Notify;
    }

    public override string Name => "ScheduledTasksInfo";

    protected override async Task<JsonElement?> BuildAsync(SocketConnection connection, CancellationToken ct)
    {
        var list = (await _tasks.ListAsync(ct)).Where(s => !s.Task.IsHidden)
            .OrderBy(s => s.Task.Category, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Task.Name, StringComparer.OrdinalIgnoreCase)
            .Select(TaskEndpoints.ToDto).ToList();
        return JsonSerializer.SerializeToElement(list, AppJson.Default.ListScheduledTaskInfo);
    }
}

/// <summary><c>Sessions</c>: the dashboard's "Activity" panel and a client's remote-control list.</summary>
public sealed class SessionsTopic(SessionInfoBuilder sessions) : PeriodicTopic
{
    public override string Name => "Sessions";

    protected override async Task<JsonElement?> BuildAsync(SocketConnection connection, CancellationToken ct) =>
        JsonSerializer.SerializeToElement(await sessions.BuildAsync(connection.Auth.User, FieldSet.None, ct), AppJson.Default.ListSessionInfoDto);
}
