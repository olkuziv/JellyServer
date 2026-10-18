using System.Data.Common;
using System.Text.Json;
using Dapper;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Kernel.Data;
using Npgsql;

namespace JellyServer.Collections;

/// <summary>The rows behind playlists and box sets; the rules are in <see cref="CollectionManager"/>.</summary>
public sealed class CollectionStore([FromKeyedServices(CollectionsModule.ModuleName)] Database db) : IItemLinkedData
{
    private const string Select = """
        SELECT c.id, c.kind, c.name, c.owner_id, c.open_access, c.shares::text, c.media_type, c.is_locked, c.images::text,
               c.date_created, c.date_modified, c.date_last_media_added,
               (SELECT count(*)::int FROM entries e WHERE e.collection_id = c.id), c.provider_id
          FROM collections c
        """;

    public string Name => "playlist and collection entries";

    private static Collection Read(DbDataReader r)
    {
        static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return new Collection
        {
            Id = r.GetGuid(0), Kind = r.GetString(1), Name = r.GetString(2), OwnerId = r.IsDBNull(3) ? null : r.GetGuid(3),
            OpenAccess = r.GetBoolean(4), Shares = JsonSerializer.Deserialize(r.GetString(5), CollectionsJson.Default.ListCollectionShare) ?? [],
            MediaType = r.IsDBNull(6) ? null : r.GetString(6), IsLocked = r.GetBoolean(7),
            Images = JsonSerializer.Deserialize(r.GetString(8), CollectionsJson.Default.ItemImages) ?? new(),
            DateCreated = Utc(r.GetDateTime(9)), DateModified = Utc(r.GetDateTime(10)),
            DateLastMediaAdded = r.IsDBNull(11) ? null : Utc(r.GetDateTime(11)), ItemCount = r.GetInt32(12),
            ProviderId = r.IsDBNull(13) ? null : r.GetString(13),
        };
    }

    private async Task<List<Collection>> QueryAsync(string where, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(Select + " " + where, conn);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Collection>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<Collection?> GetAsync(Guid id, CancellationToken ct) =>
        (await QueryAsync("WHERE c.id = $1", cmd => cmd.Parameters.Add(new NpgsqlParameter { Value = id }), ct)).FirstOrDefault();

    public Task<List<Collection>> ListAsync(string kind, CancellationToken ct) =>
        QueryAsync("WHERE c.kind = $1 ORDER BY lower(c.name), c.id", cmd => cmd.Parameters.Add(new NpgsqlParameter { Value = kind }), ct);

    /// <summary>Playlists the user owns or is shared with (what deleting the user affects).</summary>
    public Task<List<Collection>> OfUserAsync(Guid userId, CancellationToken ct) =>
        QueryAsync("WHERE c.kind = 'Playlist' AND (c.owner_id = $1 OR c.shares @> jsonb_build_array(jsonb_build_object('UserId', $1::text)))",
            cmd => cmd.Parameters.Add(new NpgsqlParameter { Value = userId }), ct);

    public async Task<Dictionary<Guid, List<Guid>>> EntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = ids.Distinct().ToDictionary(id => id, _ => new List<Guid>());
        if (result.Count == 0) return result;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Collection, Guid Item)>(new CommandDefinition(
            "SELECT collection_id, item_id FROM entries WHERE collection_id = ANY(@ids) ORDER BY collection_id, position",
            new { ids = result.Keys.ToArray() }, cancellationToken: ct));
        foreach (var (collection, item) in rows) result[collection].Add(item);
        return result;
    }

    public async Task InsertAsync(Collection c, IReadOnlyList<Guid> entries, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO collections (id, kind, name, owner_id, open_access, shares, media_type, is_locked, images, date_created, date_modified, date_last_media_added, provider_id)
            VALUES (@Id, @Kind, @Name, @OwnerId, @OpenAccess, @Shares::jsonb, @MediaType, @IsLocked, @Images::jsonb, @DateCreated, @DateModified, @DateLastMediaAdded, @ProviderId)
            """, Args(c), tx, cancellationToken: ct));
        await WriteEntriesAsync(conn, tx, c.Id, entries, ct);
        await tx.CommitAsync(ct);
    }

    public async Task UpdateAsync(Collection c, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE collections SET name = @Name, owner_id = @OwnerId, open_access = @OpenAccess, shares = @Shares::jsonb, media_type = @MediaType,
                   is_locked = @IsLocked, images = @Images::jsonb, provider_id = @ProviderId, date_modified = now()
             WHERE id = @Id
            """, Args(c), cancellationToken: ct));
    }

    private static object Args(Collection c) => new
    {
        c.Id, c.Kind, c.Name, c.OwnerId, c.OpenAccess, c.MediaType, c.IsLocked, c.ProviderId, c.DateCreated, c.DateModified, c.DateLastMediaAdded,
        Shares = JsonSerializer.Serialize(c.Shares, CollectionsJson.Default.ListCollectionShare),
        Images = JsonSerializer.Serialize(c.Images, CollectionsJson.Default.ItemImages),
    };

    /// <summary>
    /// Changes a list's lines under a row lock, so two clients editing one playlist cannot lose each other's change.
    /// <paramref name="change"/> gets the current lines and returns the new ones, or null to leave them.
    /// </summary>
    public async Task<bool> ChangeEntriesAsync(Guid id, Func<List<Guid>, List<Guid>?> change, bool added, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM collections WHERE id = @id FOR UPDATE", new { id }, tx, cancellationToken: ct)) is null)
            return false;
        var current = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT item_id FROM entries WHERE collection_id = @id ORDER BY position", new { id }, tx, cancellationToken: ct))).ToList();
        if (change(current) is not { } next) return true;
        await WriteEntriesAsync(conn, tx, id, next, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE collections SET date_modified = now(), date_last_media_added = CASE WHEN @added THEN now() ELSE date_last_media_added END WHERE id = @id",
            new { id, added }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task WriteEntriesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, IReadOnlyList<Guid> entries, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM entries WHERE collection_id = @id", new { id }, tx, cancellationToken: ct));
        if (entries.Count == 0) return;
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO entries (collection_id, position, item_id)
            SELECT @id, (t.ord - 1)::int, t.item FROM unnest(@items) WITH ORDINALITY AS t(item, ord)
            """, new { id, items = entries.ToArray() }, tx, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM collections WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> PageItemIdsAsync(Guid? afterId, int limit, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT item_id FROM entries WHERE (@after::uuid IS NULL OR item_id > @after) ORDER BY item_id LIMIT @limit",
            new { after = afterId, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Items that are gone leave every list they were in (the lists' artwork tags change with them).</summary>
    public async Task RemoveAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var chunk in itemIds.Chunk(10_000))
            await conn.ExecuteAsync(new CommandDefinition("""
                WITH gone AS (DELETE FROM entries WHERE item_id = ANY(@ids) RETURNING collection_id)
                UPDATE collections SET date_modified = now() WHERE id IN (SELECT collection_id FROM gone)
                """, new { ids = chunk }, cancellationToken: ct));
    }
}
