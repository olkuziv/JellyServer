using System.Text.Json;
using Dapper;
using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Data;
using JellyServer.Media.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace JellyServer.Media;

public sealed class MediaSourceStore([FromKeyedServices(MediaModule.ModuleName)] Database db) : IMediaSourceStore, IItemLinkedData
{
    private static readonly string[] Columns =
        ["id", "item_id", "ordinal", "path", "stream_url", "container", "size", "runtime_ticks", "media_info", "subtitles", "fingerprint"];

    private static readonly string CopySql = $"COPY sources_stage ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";

    // Unchanged rows are not rewritten: a rescan of a changed item must not leave dead copies of its untouched versions behind.
    private static readonly string MergeSql =
        $"""
         INSERT INTO sources ({string.Join(", ", Columns)}) SELECT {string.Join(", ", Columns)} FROM sources_stage
         ON CONFLICT (id) DO UPDATE SET {string.Join(", ", Columns.Skip(1).Select(c => $"{c} = EXCLUDED.{c}"))}
          WHERE ({string.Join(", ", Columns.Skip(1).Select(c => "sources." + c))}) IS DISTINCT FROM ({string.Join(", ", Columns.Skip(1).Select(c => "EXCLUDED." + c))})
         """;

    private const string Select = """
        SELECT id, item_id, ordinal, path, stream_url, container, size, runtime_ticks, media_info::text, subtitles::text, fingerprint FROM sources
        """;

    public string Name => "media sources";

    public async Task<Dictionary<Guid, List<MediaSource>>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, List<MediaSource>>();
        if (itemIds.Count == 0) return result;
        return await db.Timed("media sources by item", async () =>
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(Select + " WHERE item_id = ANY($1) ORDER BY item_id, ordinal", conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = itemIds.ToArray() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var source = Read(r);
                if (!result.TryGetValue(source.ItemId, out var list)) result[source.ItemId] = list = new List<MediaSource>(1);
                list.Add(source);
            }
            return result;
        });
    }

    private static MediaSource Read(NpgsqlDataReader r)
    {
        static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new MediaSource
        {
            Id = r.GetGuid(0), ItemId = r.GetGuid(1), Ordinal = r.GetInt16(2), Path = r.GetString(3), StreamUrl = Str(r, 4),
            Container = Str(r, 5), Size = r.GetInt64(6), RuntimeTicks = r.GetInt64(7),
            MediaInfo = Str(r, 8) is { } mi ? JsonSerializer.Deserialize(mi, MediaJson.Default.MediaInfo) : null,
            Subtitles = Str(r, 9) is { } subs ? JsonSerializer.Deserialize(subs, MediaJson.Default.ListSubtitleFile) : null,
            Fingerprint = r.GetString(10),
        };
    }

    public async Task<Guid?> FindItemAsync(Guid sourceId, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition("SELECT item_id FROM sources WHERE id = @sourceId",
            new { sourceId }, cancellationToken: ct));
    }

    public Task ReplaceAsync(IReadOnlyCollection<Guid> itemIds, IReadOnlyList<MediaSource> sources, CancellationToken ct = default) =>
        itemIds.Count == 0 ? Task.CompletedTask : db.Timed("replace media sources", async () =>
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM sources WHERE item_id = ANY(@items) AND NOT (id = ANY(@keep))",
                new { items = itemIds.ToArray(), keep = sources.Select(s => s.Id).ToArray() }, tx, cancellationToken: ct));
            if (sources.Count > 0)
            {
                await using (var create = new NpgsqlCommand("CREATE TEMP TABLE sources_stage (LIKE sources INCLUDING DEFAULTS) ON COMMIT DROP", conn, tx))
                    await create.ExecuteNonQueryAsync(ct);
                await using (var w = await conn.BeginBinaryImportAsync(CopySql, ct))
                {
                    foreach (var s in sources)
                    {
                        await w.StartRowAsync(ct);
                        await w.WriteAsync(s.Id, NpgsqlDbType.Uuid, ct);
                        await w.WriteAsync(s.ItemId, NpgsqlDbType.Uuid, ct);
                        await w.WriteAsync((short)s.Ordinal, NpgsqlDbType.Smallint, ct);
                        await w.WriteAsync(s.Path, NpgsqlDbType.Text, ct);
                        await Nullable(w, s.StreamUrl, NpgsqlDbType.Text, ct);
                        await Nullable(w, s.Container, NpgsqlDbType.Text, ct);
                        await w.WriteAsync(s.Size, NpgsqlDbType.Bigint, ct);
                        await w.WriteAsync(s.RuntimeTicks, NpgsqlDbType.Bigint, ct);
                        await Nullable(w, s.MediaInfo is null ? null : JsonSerializer.Serialize(s.MediaInfo, MediaJson.Default.MediaInfo), NpgsqlDbType.Jsonb, ct);
                        await Nullable(w, s.Subtitles is { Count: > 0 } ? JsonSerializer.Serialize(s.Subtitles, MediaJson.Default.ListSubtitleFile) : null, NpgsqlDbType.Jsonb, ct);
                        await w.WriteAsync(s.Fingerprint, NpgsqlDbType.Text, ct);
                    }
                    await w.CompleteAsync(ct);
                }
                await using (var merge = new NpgsqlCommand(MergeSql, conn, tx))
                    await merge.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return 0;
        });

    private static Task Nullable(NpgsqlBinaryImporter w, string? v, NpgsqlDbType type, CancellationToken ct) =>
        v is null ? w.WriteNullAsync(ct) : w.WriteAsync(v, type, ct);

    public async Task SaveMediaInfoAsync(Guid sourceId, MediaInfo info, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sources SET media_info = @json::jsonb,
                   runtime_ticks = CASE WHEN runtime_ticks = 0 THEN @ticks ELSE runtime_ticks END
             WHERE id = @sourceId
            """, new { sourceId, ticks = (long)(info.DurationSec * TimeSpan.TicksPerSecond), json = JsonSerializer.Serialize(info, MediaJson.Default.MediaInfo) },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> PageItemIdsAsync(Guid? afterId, int limit, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT item_id FROM sources WHERE (@after::uuid IS NULL OR item_id > @after) ORDER BY item_id LIMIT @limit",
            new { after = afterId, limit }, cancellationToken: ct))).ToList();
    }

    public async Task RemoveAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var chunk in itemIds.Chunk(10_000))
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM sources WHERE item_id = ANY(@ids)", new { ids = chunk }, cancellationToken: ct));
    }
}
