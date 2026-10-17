using System.Text.Json;
using Dapper;
using JellyServer.Kernel.Data;
using Microsoft.Extensions.Options;
using Npgsql;
using JellyServer.Kernel;
using JellyServer.Kernel.Caching;
using JellyServer.Catalog.Contracts;
using JellyServer.UserData.Contracts;

namespace JellyServer.Catalog;

/// <summary>
/// Read side for items: Redis-first hydration. Per-user state lives in the UserData module (possibly another
/// database), so every query that needs it asks <see cref="IUserDataStore"/> for IDs first and filters here.
/// </summary>
public sealed partial class ItemRepository(
    [FromKeyedServices(CatalogModule.ModuleName)] Database db, RedisCache cache, IOptions<CacheOptions> options, IUserDataStore userData)
{
    private readonly CacheOptions _cacheOpt = options.Value;

    private const string ItemSelect = """
        SELECT id, library_id, parent_id, series_id, season_id, type, name, original_title, sort_name, path,
               stream_url, container, overview, tagline, production_year, premiere_date, end_date,
               community_rating, critic_rating, official_rating, runtime_ticks, index_number,
               parent_index_number, genres, studios, provider_ids::text, people::text, media_info::text,
               images::text, subtitles::text, collection_name, child_count, recursive_child_count, size,
               fingerprint, date_created, date_modified, date_last_media_added, name_locked, versions::text
          FROM items
        """;

    private static string ItemKey(Guid id) => "item:" + id.N();

    private static Item ReadItem(NpgsqlDataReader r)
    {
        static T? Json<T>(NpgsqlDataReader r, int i, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> t) =>
            r.IsDBNull(i) ? default : JsonSerializer.Deserialize(r.GetString(i), t);
        static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
        static int? I(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        static float? F(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFloat(i);
        static DateTime? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

        return new Item
        {
            Id = r.GetGuid(0), LibraryId = r.GetGuid(1), ParentId = G(r, 2), SeriesId = G(r, 3), SeasonId = G(r, 4),
            Type = r.GetString(5), Name = r.GetString(6), OriginalTitle = Str(r, 7), SortName = r.GetString(8),
            Path = r.GetString(9), StreamUrl = Str(r, 10), Container = Str(r, 11), Overview = Str(r, 12),
            Tagline = Str(r, 13), ProductionYear = I(r, 14), PremiereDate = D(r, 15), EndDate = D(r, 16),
            CommunityRating = F(r, 17), CriticRating = F(r, 18), OfficialRating = Str(r, 19),
            RuntimeTicks = r.IsDBNull(20) ? 0 : r.GetInt64(20), IndexNumber = I(r, 21), ParentIndexNumber = I(r, 22),
            Genres = r.GetFieldValue<string[]>(23), Studios = r.GetFieldValue<string[]>(24),
            ProviderIds = Json(r, 25, CatalogJson.Default.DictionaryStringString) ?? [],
            People = Json(r, 26, CatalogJson.Default.ListPerson),
            MediaInfo = Json(r, 27, CatalogJson.Default.MediaInfo),
            Images = Json(r, 28, CatalogJson.Default.ItemImages) ?? new(),
            Subtitles = Json(r, 29, CatalogJson.Default.ListSubtitleFile),
            CollectionName = Str(r, 30), ChildCount = r.GetInt32(31), RecursiveChildCount = r.GetInt32(32),
            Size = r.GetInt64(33), Fingerprint = r.GetString(34),
            DateCreated = D(r, 35)!.Value, DateModified = D(r, 36)!.Value, DateLastMediaAdded = D(r, 37),
            NameLocked = r.GetBoolean(38),
            Versions = Json(r, 39, CatalogJson.Default.ListItemVersion),
        };
    }

    public async Task<Item?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await GetManyAsync([id], ct)).FirstOrDefault();

    /// <summary>
    /// The item as it plays: a version id (what clients call the MediaSourceId) gives the item with that file, anything else the item
    /// itself. <paramref name="mediaSourceId"/> picks one of the item's own versions when the id is the main item's.
    /// </summary>
    public async Task<Item?> GetSourceAsync(Guid id, Guid? mediaSourceId, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        if (item is null || mediaSourceId is not { } msid || msid == item.Id) return item;
        return item.Versions?.FirstOrDefault(v => v.Id == msid) is { } version ? item.AsVersion(version) : item;
    }

    /// <summary>Main item and version of a version id, null when the id is not one.</summary>
    private async Task<Item?> GetByVersionAsync(Guid versionId, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(ItemSelect + " WHERE versions @> jsonb_build_array(jsonb_build_object('Id', $1::text))", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = versionId.ToString() });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var main = ReadItem(reader);
        return main.Versions?.FirstOrDefault(v => v.Id == versionId) is { } version ? main.AsVersion(version) : null;
    }

    /// <summary>Hydrates IDs in order: one Redis MGET, then one PostgreSQL query for the misses.</summary>
    public async Task<List<Item>> GetManyAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var cached = await cache.GetManyAsync(ids.Select(ItemKey).ToList(), CatalogJson.Default.Item);
        var found = new Dictionary<Guid, Item>(ids.Count);
        var missing = new List<Guid>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (cached[i] is { } it) found[ids[i]] = it;
            else missing.Add(ids[i]);
        }

        if (missing.Count > 0)
        {
            var loaded = await db.Timed("items by id", async () =>
            {
                var list = new List<Item>(missing.Count);
                await using var conn = await db.DataSource.OpenConnectionAsync(ct);
                await using var cmd = new NpgsqlCommand(ItemSelect + " WHERE id = ANY($1)", conn);
                cmd.Parameters.Add(new NpgsqlParameter { Value = missing.ToArray() });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) list.Add(ReadItem(reader));
                return list;
            });
            foreach (var it in loaded) found[it.Id] = it;
            await cache.SetManyAsync(loaded.Select(it => (ItemKey(it.Id), it)), CatalogJson.Default.Item, _cacheOpt.ItemTtl);

            // An id that is no item may be the id of a version (not cached: the view is cheap to build, the main item is cached).
            foreach (var id in missing.Where(id => !found.ContainsKey(id)))
                if (await GetByVersionAsync(id, ct) is { } view) found[id] = view;
        }

        var result = new List<Item>(ids.Count);
        foreach (var id in ids)
            if (found.TryGetValue(id, out var it)) result.Add(it);
        return result;
    }

    /// <summary>Played-episode counts per series/season for the given folders (one page, so the episode list stays bounded).</summary>
    public async Task<Dictionary<Guid, int>> GetPlayedCountsAsync(Guid userId, IReadOnlyCollection<Guid> folderIds, CancellationToken ct = default)
    {
        if (folderIds.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var episodes = (await conn.QueryAsync<(Guid Id, Guid? SeriesId, Guid? SeasonId)>(new CommandDefinition("""
            SELECT id, series_id, season_id FROM items
             WHERE type = 'Episode' AND (series_id = ANY(@ids) OR season_id = ANY(@ids))
            """, new { ids = folderIds.ToArray() }, cancellationToken: ct))).ToList();
        if (episodes.Count == 0) return [];

        var played = await userData.PlayedAmongAsync(userId, episodes.Select(e => e.Id).ToArray(), ct);
        var folders = folderIds.ToHashSet();
        var counts = new Dictionary<Guid, int>();
        foreach (var e in episodes)
        {
            if (!played.Contains(e.Id)) continue;
            if (e.SeriesId is { } s && folders.Contains(s)) counts[s] = counts.GetValueOrDefault(s) + 1;
            if (e.SeasonId is { } n && folders.Contains(n)) counts[n] = counts.GetValueOrDefault(n) + 1;
        }
        return counts;
    }

    /// <summary>Every episode of a series or season (used to mark a whole folder played).</summary>
    public async Task<List<Guid>> EpisodeIdsAsync(Guid folderId, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM items WHERE type = 'Episode' AND (series_id = @folderId OR season_id = @folderId)",
            new { folderId }, cancellationToken: ct))).ToList();
    }

    public Task InvalidateAsync(params Guid[] ids) => cache.DeleteAsync(ids.Select(ItemKey));

    /// <summary>Stores ffprobe results (and the runtime when the NFO had none), then evicts the cached item.</summary>
    public async Task SaveMediaInfoAsync(Guid id, MediaInfo info, long runtimeTicks, Guid? primaryId = null)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        if (primaryId is { } main)
        {
            // A version: its probe result lives in the main item's list.
            await conn.ExecuteAsync("""
                UPDATE items SET versions = (SELECT jsonb_agg(CASE WHEN v->>'Id' = @vid THEN jsonb_set(v, '{MediaInfo}', @json::jsonb) ELSE v END)
                                               FROM jsonb_array_elements(versions) v)
                 WHERE id = @main
                """, new { main, vid = id.ToString(), json = JsonSerializer.Serialize(info, CatalogJson.Default.MediaInfo) });
            await InvalidateAsync(main);
            return;
        }
        await conn.ExecuteAsync("""
            UPDATE items SET media_info = @json::jsonb,
                   runtime_ticks = CASE WHEN coalesce(runtime_ticks, 0) = 0 THEN @runtimeTicks ELSE runtime_ticks END
             WHERE id = @id
            """, new { id, runtimeTicks, json = JsonSerializer.Serialize(info, CatalogJson.Default.MediaInfo) });
        await InvalidateAsync(id);
    }

    /// <summary>Next unplayed episode after the last played one, per series, most recently watched first.</summary>
    public async Task<List<Guid>> NextUpAsync(Guid userId, Guid? seriesId, Guid[]? libraryIds, int start, int limit, CancellationToken ct = default)
    {
        // 1. UserData: every played item with its time. 2. Catalog: where those episodes sit in their series.
        // 3. Per series, the first regular episode after the furthest played one (anything after it is unplayed by definition).
        var played = await userData.PlayedWithTimeAsync(userId, ct);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var ids = new List<Guid>();
        if (played.Count > 0)
        {
            const string positionsSql = """
                SELECT id, series_id, coalesce(parent_index_number, 0), coalesce(index_number, 0) FROM items
                 WHERE id = ANY(@ids) AND type = 'Episode' AND series_id IS NOT NULL
                   AND (@seriesId::uuid IS NULL OR series_id = @seriesId)
                   AND (@libs::uuid[] IS NULL OR library_id = ANY(@libs))
                """;
            var episodes = await conn.QueryAsync<(Guid Id, Guid SeriesId, int S, int N)>(new CommandDefinition(positionsSql,
                new { ids = played.Select(p => p.ItemId).ToArray(), seriesId, libs = libraryIds }, cancellationToken: ct));
            var when = played.ToDictionary(p => p.ItemId, p => p.LastPlayedAt);

            var perSeries = new Dictionary<Guid, (int S, int N, DateTime? Watched)>();
            foreach (var e in episodes)
            {
                var watched = when[e.Id];
                if (!perSeries.TryGetValue(e.SeriesId, out var cur))
                {
                    perSeries[e.SeriesId] = (e.S, e.N, watched);
                    continue;
                }
                var (s, n) = (e.S, e.N).CompareTo((cur.S, cur.N)) > 0 ? (e.S, e.N) : (cur.S, cur.N);
                perSeries[e.SeriesId] = (s, n, Latest(cur.Watched, watched));
            }

            var order = perSeries.OrderBy(x => x.Value.Watched is null).ThenByDescending(x => x.Value.Watched).ToList();
            if (order.Count > 0)
            {
                const string nextSql = """
                    SELECT nx.id FROM unnest(@sids::uuid[], @ss::int[], @ns::int[]) WITH ORDINALITY AS l(series_id, s, n, ord)
                    CROSS JOIN LATERAL (
                        SELECT e.id FROM items e
                         WHERE e.series_id = l.series_id AND e.type = 'Episode' AND coalesce(e.parent_index_number, 0) > 0
                           AND (coalesce(e.parent_index_number, 0), coalesce(e.index_number, 0)) > (l.s, l.n)
                         ORDER BY e.parent_index_number, e.index_number LIMIT 1
                    ) nx
                    ORDER BY l.ord
                    """;
                var next = await db.Timed(nextSql, () => conn.QueryAsync<Guid>(new CommandDefinition(nextSql, new
                {
                    sids = order.Select(o => o.Key).ToArray(),
                    ss = order.Select(o => o.Value.S).ToArray(),
                    ns = order.Select(o => o.Value.N).ToArray(),
                }, cancellationToken: ct)));
                ids = next.Skip(start).Take(limit).ToList();
            }
        }

        if (ids.Count == 0 && seriesId is not null && start == 0)
        {
            // Nothing watched yet: the first regular episode is "next up" on a series page.
            var first = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition("""
                SELECT id FROM items WHERE series_id = @seriesId AND type = 'Episode' AND coalesce(parent_index_number, 0) > 0
                 ORDER BY parent_index_number, index_number LIMIT 1
                """, new { seriesId }, cancellationToken: ct));
            if (first is { } f) ids.Add(f);
        }
        return ids;
    }

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    public async Task<List<(Guid Id, string Name, int Count)>> GenresAsync(Guid[]? libraryIds, CancellationToken ct = default)
    {
        var key = $"genres:{cache.Generation}:{(libraryIds is null ? "all" : string.Join(',', libraryIds.Select(x => x.N())))}";
        if (await cache.GetAsync(key, CatalogJson.Default.ListGenreCount) is { } hit)
            return hit.Select(g => (g.Id, g.Name, g.Count)).ToList();

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<(string Name, int Count)>(new CommandDefinition("""
            SELECT g, count(*)::int FROM items, unnest(genres) g
             WHERE type IN ('Movie', 'Series') AND (@libs::uuid[] IS NULL OR library_id = ANY(@libs))
             GROUP BY g ORDER BY g
            """, new { libs = libraryIds }, cancellationToken: ct))).ToList();
        var result = rows.Select(r => new GenreCount(Ids.GenreId(r.Name), r.Name, r.Count)).ToList();
        await cache.SetAsync(key, result, CatalogJson.Default.ListGenreCount, _cacheOpt.QueryTtl);
        return result.Select(g => (g.Id, g.Name, g.Count)).ToList();
    }

    public async Task<List<int>> YearsAsync(Guid[]? libraryIds, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<int>(new CommandDefinition("""
            SELECT DISTINCT production_year FROM items
             WHERE production_year IS NOT NULL AND type IN ('Movie', 'Series')
               AND (@libs::uuid[] IS NULL OR library_id = ANY(@libs))
             ORDER BY 1 DESC
            """, new { libs = libraryIds }, cancellationToken: ct))).ToList();
    }

    public async Task<Dictionary<string, int>> CountsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Type, int Count)>("SELECT type, count(*)::int FROM items GROUP BY type");
        return rows.ToDictionary(r => r.Type, r => r.Count);
    }

    public async Task<Dictionary<Guid, int>> LibraryChildCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Lib, int Count)>(
            "SELECT library_id, count(*)::int FROM items WHERE type IN ('Movie', 'Series') GROUP BY library_id");
        return rows.ToDictionary(r => r.Lib, r => r.Count);
    }
}

public sealed record GenreCount(Guid Id, string Name, int Count);
