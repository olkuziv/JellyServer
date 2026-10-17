using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using JellyServer.Kernel.Data;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Tasks;

/// <summary>What the dashboard changed (triggers) and what happened (last result), per task, in PostgreSQL.</summary>
public sealed class TaskStore([FromKeyedServices(TasksModule.ModuleName)] Database db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record State(IReadOnlyList<TaskTrigger>? Triggers, TaskResult? LastResult);

    private sealed record Row(string Id, string? Triggers, string? LastResult);

    public async Task<Dictionary<string, State>> LoadAllAsync(CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            "SELECT id, triggers::text AS Triggers, last_result::text AS LastResult FROM task_state", cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => new State(
            r.Triggers is null ? null : JsonSerializer.Deserialize<List<TaskTrigger>>(r.Triggers, Json),
            r.LastResult is null ? null : JsonSerializer.Deserialize<TaskResult>(r.LastResult, Json)));
    }

    public async Task SaveTriggersAsync(string id, IReadOnlyList<TaskTrigger> triggers, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO task_state (id, triggers) VALUES (@id, @json::jsonb)
            ON CONFLICT (id) DO UPDATE SET triggers = EXCLUDED.triggers, updated_at = now()
            """, new { id, json = JsonSerializer.Serialize(triggers, Json) }, cancellationToken: ct));
    }

    public async Task SaveResultAsync(string id, TaskResult result, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO task_state (id, last_result) VALUES (@id, @json::jsonb)
            ON CONFLICT (id) DO UPDATE SET last_result = EXCLUDED.last_result, updated_at = now()
            """, new { id, json = JsonSerializer.Serialize(result, Json) }, cancellationToken: ct));
    }
}