using System.Text.Json;
using Dapper;
using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Data;

namespace JellyServer.Metadata;

public static class ItemStatus
{
    public const string Found = "found";
    public const string NotFound = "not_found";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>The metadata module's own data: what was looked up and how it went, and what the providers answered.</summary>
public interface IMetadataStore : IRemoteMetadataSource
{
    /// <summary>Of these ids, the ones never looked up or due for another try.</summary>
    Task<HashSet<Guid>> DueAsync(IReadOnlyCollection<Guid> ids, TimeSpan notFoundRetry, TimeSpan failedRetry, CancellationToken ct);

    Task SaveAsync(IReadOnlyCollection<(Guid Id, string Status)> states, CancellationToken ct);

    /// <summary>Keeps the providers' answers (replacing earlier ones for the same items).</summary>
    Task SaveRemoteAsync(IReadOnlyCollection<(Guid Id, RemoteMetadata Data)> answers, CancellationToken ct);

    /// <summary>Forgets everything about these items.</summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

public sealed class MetadataStore([FromKeyedServices(MetadataModule.ModuleName)] Database db) : IMetadataStore
{
    public async Task<Dictionary<Guid, RemoteMetadata>> LoadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, RemoteMetadata>();
        if (ids.Count == 0) return result;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, string Json)>(new CommandDefinition(
            "SELECT item_id, data::text FROM item_remote WHERE item_id = ANY(@ids)", new { ids = ids.ToArray() }, cancellationToken: ct));
        foreach (var (id, json) in rows)
            if (JsonSerializer.Deserialize(json, MetadataJson.Default.RemoteMetadata) is { } r) result[id] = r;
        return result;
    }

    public async Task SaveRemoteAsync(IReadOnlyCollection<(Guid Id, RemoteMetadata Data)> answers, CancellationToken ct)
    {
        if (answers.Count == 0) return;
        // One row per item: when an item appears twice, the later answer wins.
        var last = answers.GroupBy(a => a.Id).Select(g => g.Last()).ToList();
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO item_remote (item_id, data) SELECT i, d::jsonb FROM unnest(@ids, @data) AS t(i, d)
            ON CONFLICT (item_id) DO UPDATE SET data = EXCLUDED.data
            """, new
            {
                ids = last.Select(a => a.Id).ToArray(),
                data = last.Select(a => JsonSerializer.Serialize(a.Data, MetadataJson.Default.RemoteMetadata)).ToArray(),
            }, cancellationToken: ct));
    }

    /// <summary>Of these ids, the ones never looked up or due for another try.</summary>
    public async Task<HashSet<Guid>> DueAsync(IReadOnlyCollection<Guid> ids, TimeSpan notFoundRetry, TimeSpan failedRetry, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT i FROM unnest(@ids) AS i LEFT JOIN item_state s ON s.item_id = i
             WHERE s.item_id IS NULL
                OR (s.status = 'not_found' AND s.checked_at < now() - @notFound)
                OR (s.status = 'failed' AND s.checked_at < now() - @failed)
            """, new { ids = ids.ToArray(), notFound = notFoundRetry, failed = failedRetry }, cancellationToken: ct))).ToHashSet();
    }

    public async Task SaveAsync(IReadOnlyCollection<(Guid Id, string Status)> states, CancellationToken ct)
    {
        if (states.Count == 0) return;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO item_state (item_id, status) SELECT * FROM unnest(@ids, @statuses)
            ON CONFLICT (item_id) DO UPDATE SET status = EXCLUDED.status, checked_at = now(), attempts = item_state.attempts + 1
            """, new { ids = states.Select(s => s.Id).ToArray(), statuses = states.Select(s => s.Status).ToArray() }, cancellationToken: ct));
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var chunk in ids.Chunk(10_000))
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM item_state WHERE item_id = ANY(@ids)", new { ids = chunk }, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM item_remote WHERE item_id = ANY(@ids)", new { ids = chunk }, cancellationToken: ct));
        }
    }
}