using System.Text.Json;
using Dapper;
using JellyServer.Kernel.Data;
using Npgsql;
using NpgsqlTypes;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>Write side of the library: bulk upserts, deletes and aggregates of the catalog rows (files and people: see <see cref="ItemWriter"/>).</summary>
public sealed class LibraryStore([FromKeyedServices(CatalogModule.ModuleName)] Database db)
{
    private static readonly string[] Columns =
    [
        "id", "library_id", "parent_id", "series_id", "season_id", "type", "name", "original_title",
        "sort_name", "search_text", "path", "overview", "tagline",
        "production_year", "premiere_date", "end_date", "community_rating", "critic_rating",
        "official_rating", "runtime_ticks", "index_number", "parent_index_number", "genres", "studios",
        "provider_ids", "images", "collection_name", "size",
        "fingerprint", "date_created", "date_modified", "name_locked",
    ];

    private static readonly string CopySql =
        $"COPY items_stage ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";

    // date_created is never overwritten: it is the "date added" users sort by.
    private static readonly string MergeSql =
        $"""
         INSERT INTO items ({string.Join(", ", Columns)}) SELECT {string.Join(", ", Columns)} FROM items_stage
         ON CONFLICT (id) DO UPDATE SET {string.Join(", ", Columns.Where(c => c is not ("id" or "date_created")).Select(c => $"{c} = EXCLUDED.{c}"))}
         """;

    /// <summary>
    /// Writes a batch via binary COPY into a temp table plus one merge statement:
    /// an order of magnitude faster than row-by-row INSERTs for 100k+ items.
    /// </summary>
    public Task UpsertAsync(IReadOnlyList<Item> items, CancellationToken ct) => db.Timed("upsert items batch", async () =>
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE items_stage (LIKE items INCLUDING DEFAULTS) ON COMMIT DROP", conn, tx))
            await create.ExecuteNonQueryAsync(ct);

        await using (var w = await conn.BeginBinaryImportAsync(CopySql, ct))
        {
            foreach (var it in items)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(it.Id, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(it.LibraryId, NpgsqlDbType.Uuid, ct);
                await WriteNullable(w, it.ParentId, NpgsqlDbType.Uuid, ct);
                await WriteNullable(w, it.SeriesId, NpgsqlDbType.Uuid, ct);
                await WriteNullable(w, it.SeasonId, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(it.Type, NpgsqlDbType.Text, ct);
                await w.WriteAsync(it.Name, NpgsqlDbType.Text, ct);
                await WriteText(w, it.OriginalTitle, ct);
                await w.WriteAsync(it.SortName, NpgsqlDbType.Text, ct);
                await w.WriteAsync(it.SearchText, NpgsqlDbType.Text, ct);
                await w.WriteAsync(it.Path, NpgsqlDbType.Text, ct);
                await WriteText(w, it.Overview, ct);
                await WriteText(w, it.Tagline, ct);
                await WriteNullable(w, it.ProductionYear, NpgsqlDbType.Integer, ct);
                await WriteNullable(w, it.PremiereDate, NpgsqlDbType.Date, ct);
                await WriteNullable(w, it.EndDate, NpgsqlDbType.Date, ct);
                await WriteNullable(w, it.CommunityRating, NpgsqlDbType.Real, ct);
                await WriteNullable(w, it.CriticRating, NpgsqlDbType.Real, ct);
                await WriteText(w, it.OfficialRating, ct);
                await w.WriteAsync(it.RuntimeTicks, NpgsqlDbType.Bigint, ct);
                await WriteNullable(w, it.IndexNumber, NpgsqlDbType.Integer, ct);
                await WriteNullable(w, it.ParentIndexNumber, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(it.Genres, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await w.WriteAsync(it.Studios, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await w.WriteAsync(JsonSerializer.Serialize(it.ProviderIds, CatalogJson.Default.DictionaryStringString), NpgsqlDbType.Jsonb, ct);
                await w.WriteAsync(JsonSerializer.Serialize(it.Images, CatalogJson.Default.ItemImages), NpgsqlDbType.Jsonb, ct);
                await WriteText(w, it.CollectionName, ct);
                await w.WriteAsync(it.Size, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(it.Fingerprint, NpgsqlDbType.Text, ct);
                await w.WriteAsync(it.DateCreated, NpgsqlDbType.TimestampTz, ct);
                await w.WriteAsync(it.DateModified, NpgsqlDbType.TimestampTz, ct);
                await w.WriteAsync(it.NameLocked, NpgsqlDbType.Boolean, ct);
            }
            await w.CompleteAsync(ct);
        }

        await using (var merge = new NpgsqlCommand(MergeSql, conn, tx))
            await merge.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return 0;
    });

    private static Task WriteText(NpgsqlBinaryImporter w, string? v, CancellationToken ct) =>
        v is null ? w.WriteNullAsync(ct) : w.WriteAsync(v, NpgsqlDbType.Text, ct);

    private static Task WriteNullable<T>(NpgsqlBinaryImporter w, T? v, NpgsqlDbType type, CancellationToken ct) where T : struct =>
        v.HasValue ? w.WriteAsync(v.Value, type, ct) : w.WriteNullAsync(ct);

    /// <summary>Makes the next scan rewrite these items (their fingerprint no longer matches), which puts back the local values.</summary>
    public async Task ForgetFingerprintsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE items SET fingerprint = '' WHERE id = ANY(@ids)", new { ids = ids.ToArray() }, cancellationToken: ct));
    }

    /// <summary>Stores the catalog part of items with provider metadata laid over them (the metadata itself is kept by the metadata system).</summary>
    public async Task SaveOverlayAsync(IReadOnlyList<Item> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        const string sql = """
            UPDATE items SET name = @Name, sort_name = @SortName, search_text = @SearchText, original_title = @OriginalTitle,
                   overview = @Overview, tagline = @Tagline, production_year = @ProductionYear, premiere_date = @PremiereDate::date,
                   end_date = @EndDate::date, community_rating = @CommunityRating, critic_rating = @CriticRating,
                   official_rating = @OfficialRating, runtime_ticks = @RuntimeTicks, genres = @Genres, studios = @Studios,
                   provider_ids = @ProviderIds::jsonb, images = @Images::jsonb,
                   collection_name = @CollectionName, date_modified = now()
             WHERE id = @Id
            """;
        var args = rows.Select(item => new
        {
            item.Id, item.Name, item.SortName, item.SearchText, item.OriginalTitle, item.Overview, item.Tagline,
            item.ProductionYear, item.PremiereDate, item.EndDate, item.CommunityRating, item.CriticRating,
            item.OfficialRating, item.RuntimeTicks, item.Genres, item.Studios, item.CollectionName,
            ProviderIds = JsonSerializer.Serialize(item.ProviderIds, CatalogJson.Default.DictionaryStringString),
            Images = JsonSerializer.Serialize(item.Images, CatalogJson.Default.ItemImages),
        }).ToList();
        await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Makes sure every genre name has a row (the genre list and filters are built from them).</summary>
    public async Task EnsureGenresAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
        if (distinct.Length == 0) return;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO genres (id, name) SELECT md5('genre:' || g)::uuid, g FROM unnest(@names) g ON CONFLICT DO NOTHING",
            new { names = distinct }, cancellationToken: ct));
    }

    private const string RefSelect = "SELECT id, type, name, production_year, library_id, series_id, parent_index_number, index_number, provider_ids::text FROM items";

    private static ItemRef ToRef((Guid Id, string Type, string Name, int? Year, Guid Lib, Guid? Series, int? ParentIndex, int? Index, string Ids) r) =>
        new(r.Id, r.Type, r.Name, r.Year, r.Lib, r.Series, r.ParentIndex, r.Index,
            JsonSerializer.Deserialize(r.Ids, CatalogJson.Default.DictionaryStringString) ?? []);

    public async Task<List<ItemRef>> PageRefsAsync(Guid? afterId, int limit, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid, string, string, int?, Guid, Guid?, int?, int?, string)>(new CommandDefinition(
            RefSelect + " WHERE (@after::uuid IS NULL OR id > @after) ORDER BY id LIMIT @limit", new { after = afterId, limit }, cancellationToken: ct));
        return rows.Select(ToRef).ToList();
    }

    public async Task<List<ItemRef>> GetDescendantRefsAsync(Guid itemId, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid, string, string, int?, Guid, Guid?, int?, int?, string)>(new CommandDefinition(
            RefSelect + " WHERE series_id = @itemId OR season_id = @itemId ORDER BY parent_index_number, index_number", new { itemId }, cancellationToken: ct));
        return rows.Select(ToRef).ToList();
    }

    public async Task<List<ItemRef>> GetRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid, string, string, int?, Guid, Guid?, int?, int?, string)>(new CommandDefinition(
            RefSelect + " WHERE id = ANY(@ids)", new { ids = ids.ToArray() }, cancellationToken: ct));
        return rows.Select(ToRef).ToList();
    }

    public async Task<List<MovieSetRef>> MovieSetsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<(Guid, Guid, string, string?)>(new CommandDefinition(
            "SELECT id, library_id, collection_name, provider_ids->>'TmdbCollection' FROM items WHERE id = ANY(@ids) AND type = 'Movie' AND collection_name <> ''",
            new { ids = ids.ToArray() }, cancellationToken: ct))).Select(r => new MovieSetRef(r.Item1, r.Item2, r.Item3, r.Item4)).ToList();
    }

    public async Task<List<MovieSetRef>> MovieSetMembersAsync(IReadOnlyCollection<string> setNames, IReadOnlyCollection<Guid> libraryIds, CancellationToken ct)
    {
        if (setNames.Count == 0 || libraryIds.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<(Guid, Guid, string, string?)>(new CommandDefinition("""
            SELECT id, library_id, collection_name, provider_ids->>'TmdbCollection' FROM items
             WHERE type = 'Movie' AND library_id = ANY(@libs) AND lower(collection_name) = ANY(@names)
             ORDER BY sort_name, id
            """, new { libs = libraryIds.ToArray(), names = setNames.Select(n => n.ToLowerInvariant()).Distinct().ToArray() },
            cancellationToken: ct))).Select(r => new MovieSetRef(r.Item1, r.Item2, r.Item3, r.Item4)).ToList();
    }

    public async Task<List<MovieSetRef>> MovieSetsOfLibrariesAsync(IReadOnlyCollection<Guid> libraryIds, CancellationToken ct)
    {
        if (libraryIds.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<(Guid, Guid, string, string?)>(new CommandDefinition(
            "SELECT id, library_id, collection_name, provider_ids->>'TmdbCollection' FROM items WHERE type = 'Movie' AND library_id = ANY(@libs) AND collection_name <> '' ORDER BY sort_name, id",
            new { libs = libraryIds.ToArray() }, cancellationToken: ct))).Select(r => new MovieSetRef(r.Item1, r.Item2, r.Item3, r.Item4)).ToList();
    }

    public async Task<long> CountItemsAsync(CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM items", cancellationToken: ct));
    }

    /// <summary>Of these ids, the ones that are items.</summary>
    public async Task<HashSet<Guid>> ExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM items WHERE id = ANY(@ids)", new { ids = ids.ToArray() },
            cancellationToken: ct))).ToHashSet();
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var total = 0;
        foreach (var chunk in ids.Chunk(5000))
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            total += await conn.ExecuteAsync(new CommandDefinition("DELETE FROM items WHERE id = ANY(@ids)", new { ids = chunk }, cancellationToken: ct));
        }
        return total;
    }

    public async Task<Dictionary<Guid, string>> LoadFingerprintsAsync(Guid libraryId, CancellationToken ct)
    {
        var map = new Dictionary<Guid, string>(4096);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, fingerprint FROM items WHERE library_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = libraryId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) map[reader.GetGuid(0)] = reader.GetString(1);
        return map;
    }

    /// <summary>
    /// Recomputes child counts and "last media added" for folders. Returns changed IDs
    /// so their cached copies can be evicted.
    /// </summary>
    public async Task<List<Guid>> RefreshAggregatesAsync(Guid libraryId, CancellationToken ct)
    {
        string[] statements =
        [
            """
            UPDATE items p SET child_count = c.cnt
              FROM (SELECT parent_id, count(*)::int AS cnt FROM items
                     WHERE library_id = @lib AND parent_id IS NOT NULL GROUP BY parent_id) c
             WHERE p.id = c.parent_id AND p.child_count <> c.cnt
            RETURNING p.id
            """,
            """
            UPDATE items p SET recursive_child_count = c.cnt, date_last_media_added = c.last
              FROM (SELECT season_id AS id, count(*)::int AS cnt, max(date_created) AS last FROM items
                     WHERE library_id = @lib AND type = 'Episode' GROUP BY season_id
                    UNION ALL
                    SELECT series_id, count(*)::int, max(date_created) FROM items
                     WHERE library_id = @lib AND type = 'Episode' GROUP BY series_id) c
             WHERE p.id = c.id AND (p.recursive_child_count <> c.cnt OR p.date_last_media_added IS DISTINCT FROM c.last)
            RETURNING p.id
            """,
            """
            UPDATE items SET date_last_media_added = date_created
             WHERE library_id = @lib AND type = 'Movie' AND date_last_media_added IS DISTINCT FROM date_created
            RETURNING id
            """,
        ];
        var changed = new List<Guid>();
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var sql in statements)
            changed.AddRange(await db.Timed(sql, () => conn.QueryAsync<Guid>(new CommandDefinition(sql, new { lib = libraryId }, cancellationToken: ct, commandTimeout: 300))));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO genres (id, name)
            SELECT DISTINCT md5('genre:' || g)::uuid, g FROM items, unnest(genres) g WHERE library_id = @lib
            ON CONFLICT DO NOTHING
            """, new { lib = libraryId }, cancellationToken: ct, commandTimeout: 300));
        await conn.ExecuteAsync("UPDATE libraries SET last_scan_at = now() WHERE id = @lib", new { lib = libraryId });
        return changed;
    }

    public async Task<List<Library>> LoadLibrariesAsync(CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Library>(
            "SELECT id, name, collection_type AS CollectionType, paths, options::text AS OptionsJson, images::text AS ImagesJson, sort_order AS SortOrder, created_at AS CreatedAt FROM libraries ORDER BY sort_order, created_at");
        var libraries = rows.ToList();
        foreach (var l in libraries)
            l.Images = JsonSerializer.Deserialize(l.ImagesJson, CatalogJson.Default.ItemImages) ?? new();
        return libraries;
    }

    public async Task SetLibraryImagesAsync(Guid id, ItemImages images, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE libraries SET images = @json::jsonb WHERE id = @id",
            new { id, json = JsonSerializer.Serialize(images, CatalogJson.Default.ItemImages) }, cancellationToken: ct));
    }

    public async Task SetItemImagesAsync(Guid id, ItemImages images, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE items SET images = @json::jsonb, date_modified = now() WHERE id = @id",
            new { id, json = JsonSerializer.Serialize(images, CatalogJson.Default.ItemImages) }, cancellationToken: ct));
    }

    public async Task<Library> CreateLibraryAsync(string name, string collectionType, string[] paths, string optionsJson, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Library>(new CommandDefinition("""
            INSERT INTO libraries (id, name, collection_type, paths, options, sort_order)
            VALUES (@id, @name, @collectionType, @paths, @optionsJson::jsonb, coalesce((SELECT max(sort_order) + 1 FROM libraries), 0))
            RETURNING id, name, collection_type AS CollectionType, paths, options::text AS OptionsJson, sort_order AS SortOrder, created_at AS CreatedAt
            """, new { id = Guid.NewGuid(), name, collectionType, paths, optionsJson }, cancellationToken: ct));
    }

    /// <summary>Removes the library; its items go with it (cascade). Returns the IDs of the removed items.</summary>
    public async Task<List<Guid>> DeleteLibraryAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var items = (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM items WHERE library_id = @id", new { id }, cancellationToken: ct))).ToList();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM libraries WHERE id = @id", new { id }, cancellationToken: ct, commandTimeout: 600));
        return items;
    }

    /// <summary>False when another library already has that name.</summary>
    public async Task<bool> RenameLibraryAsync(Guid id, string name, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("UPDATE libraries SET name = @name WHERE id = @id", new { id, name }, cancellationToken: ct));
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task SetPathsAsync(Guid id, string[] paths, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE libraries SET paths = @paths WHERE id = @id", new { id, paths }, cancellationToken: ct));
    }

    public async Task SetOptionsAsync(Guid id, string optionsJson, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE libraries SET options = @optionsJson::jsonb WHERE id = @id", new { id, optionsJson }, cancellationToken: ct));
    }
}