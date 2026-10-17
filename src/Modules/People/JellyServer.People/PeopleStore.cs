using Dapper;
using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Data;
using JellyServer.People.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace JellyServer.People;

public sealed class PeopleStore([FromKeyedServices(PeopleModule.ModuleName)] Database db, ILogger<PeopleStore> log) : IPeopleStore, IItemLinkedData
{
    public string Name => "credits";

    public async Task<Dictionary<Guid, List<Person>>> GetCreditsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, List<Person>>();
        if (itemIds.Count == 0) return result;
        const string sql = """
            SELECT c.item_id, p.name, u.k, u.r, p.image_url, p.provider_id
              FROM credits c
              CROSS JOIN LATERAL unnest(c.person_ids, c.kinds, c.roles) WITH ORDINALITY AS u(pid, k, r, ord)
              JOIN persons p ON p.id = u.pid
             WHERE c.item_id = ANY($1)
             ORDER BY c.item_id, u.ord
            """;
        return await db.Timed(sql, async () =>
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = itemIds.ToArray() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var itemId = r.GetGuid(0);
                if (!result.TryGetValue(itemId, out var list)) result[itemId] = list = [];
                var role = r.GetString(3);
                list.Add(new Person(r.GetString(1), role.Length > 0 ? role : null, PersonKinds.Name(r.GetInt16(2)))
                {
                    ImageUrl = r.IsDBNull(4) ? null : r.GetString(4),
                    ProviderId = r.IsDBNull(5) ? null : r.GetString(5),
                });
            }
            return result;
        });
    }

    public async Task SetCreditsAsync(IReadOnlyList<(Guid ItemId, IReadOnlyList<Person>? People)> credits, CancellationToken ct = default)
    {
        if (credits.Count == 0) return;
        // One entry per item: when an item appears twice, the later list wins.
        var last = credits.GroupBy(c => c.ItemId).Select(g => g.Last()).ToList();
        var filled = last.Where(c => c.People is { Count: > 0 }).Select(c => (c.ItemId, People: c.People!.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList()))
            .Where(c => c.People.Count > 0).ToList();
        var cleared = last.Select(c => c.ItemId).Except(filled.Select(c => c.ItemId)).ToArray();

        // Everyone once; a portrait or provider id seen anywhere in the batch is kept.
        var persons = new Dictionary<Guid, PersonInfo>();
        foreach (var p in filled.SelectMany(c => c.People))
        {
            var id = PersonIds.Of(p.Name);
            persons[id] = persons.TryGetValue(id, out var seen)
                ? seen with { ImageUrl = p.ImageUrl ?? seen.ImageUrl, ProviderId = p.ProviderId ?? seen.ProviderId }
                : new PersonInfo(id, p.Name, p.ImageUrl, p.ProviderId);
        }

        await db.Timed("set credits", async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await WriteAsync(persons.Values.OrderBy(p => p.Id).ToList(), filled, cleared, ct);
                    return 0;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected && attempt < 3)
                {
                    // Scan and metadata writers may meet on the same people; the rows are locked in id order, so a retry passes.
                    log.LogDebug("Credits write hit a deadlock, retrying");
                }
            }
        });
    }

    private async Task WriteAsync(List<PersonInfo> persons, List<(Guid ItemId, List<Person> People)> filled, Guid[] cleared, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (persons.Count > 0)
        {
            // Unchanged people are not rewritten (no dead rows for every rescan).
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO persons (id, name, image_url, provider_id) SELECT * FROM unnest(@ids, @names, @images, @providers)
                ON CONFLICT (id) DO UPDATE SET image_url = coalesce(EXCLUDED.image_url, persons.image_url),
                                               provider_id = coalesce(EXCLUDED.provider_id, persons.provider_id)
                 WHERE (EXCLUDED.image_url IS NOT NULL AND EXCLUDED.image_url IS DISTINCT FROM persons.image_url)
                    OR (EXCLUDED.provider_id IS NOT NULL AND EXCLUDED.provider_id IS DISTINCT FROM persons.provider_id)
                """, new
            {
                ids = persons.Select(p => p.Id).ToArray(),
                names = persons.Select(p => p.Name).ToArray(),
                images = persons.Select(p => p.ImageUrl).ToArray(),
                providers = persons.Select(p => p.ProviderId).ToArray(),
            }, tx, cancellationToken: ct));
        }

        if (filled.Count > 0)
        {
            await using (var create = new NpgsqlCommand("CREATE TEMP TABLE credits_stage (LIKE credits) ON COMMIT DROP", conn, tx))
                await create.ExecuteNonQueryAsync(ct);
            await using (var w = await conn.BeginBinaryImportAsync("COPY credits_stage (item_id, person_ids, kinds, roles) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var (itemId, people) in filled)
                {
                    await w.StartRowAsync(ct);
                    await w.WriteAsync(itemId, NpgsqlDbType.Uuid, ct);
                    await w.WriteAsync(people.Select(p => PersonIds.Of(p.Name)).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ct);
                    await w.WriteAsync(people.Select(p => PersonKinds.Code(p.Type)).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Smallint, ct);
                    await w.WriteAsync(people.Select(p => p.Role ?? "").ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                }
                await w.CompleteAsync(ct);
            }
            await using var merge = new NpgsqlCommand("""
                INSERT INTO credits SELECT * FROM credits_stage
                ON CONFLICT (item_id) DO UPDATE SET person_ids = EXCLUDED.person_ids, kinds = EXCLUDED.kinds, roles = EXCLUDED.roles
                 WHERE (credits.person_ids, credits.kinds, credits.roles) IS DISTINCT FROM (EXCLUDED.person_ids, EXCLUDED.kinds, EXCLUDED.roles)
                """, conn, tx);
            await merge.ExecuteNonQueryAsync(ct);
        }

        if (cleared.Length > 0)
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM credits WHERE item_id = ANY(@cleared)", new { cleared }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<Guid[]> ItemsWithAsync(IReadOnlyCollection<Guid> personIds, string[]? kinds, CancellationToken ct = default)
    {
        if (personIds.Count == 0) return [];
        var codes = kinds is { Length: > 0 } ? kinds.Select(PersonKinds.Code).Distinct().ToArray() : null;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var sql = codes is null
            ? "SELECT item_id FROM credits WHERE person_ids && @ids"
            : """
              SELECT c.item_id FROM credits c
               WHERE c.person_ids && @ids
                 AND EXISTS (SELECT 1 FROM unnest(c.person_ids, c.kinds) AS u(p, k) WHERE u.p = ANY(@ids) AND u.k = ANY(@codes))
              """;
        return (await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { ids = personIds.ToArray(), codes }, cancellationToken: ct))).ToArray();
    }

    public async Task<List<PersonInfo>> GetPersonsAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default)
    {
        if (personIds.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<PersonInfo>(new CommandDefinition(
            "SELECT id, name, image_url AS ImageUrl, provider_id AS ProviderId FROM persons WHERE id = ANY(@ids)",
            new { ids = personIds.ToArray() }, cancellationToken: ct))).ToList();
    }

    public async Task<(List<PersonInfo> People, int Total)> QueryAsync(PersonQuery q, CancellationToken ct = default)
    {
        var conds = new List<string>();
        var args = new DynamicParameters();
        if (q.Ids is { } ids)
        {
            if (ids.Length == 0) return ([], 0);
            conds.Add("p.id = ANY(@ids)");
            args.Add("ids", ids);
        }
        if (!string.IsNullOrWhiteSpace(q.SearchTerm))
        {
            conds.Add(@"lower(p.name) LIKE @search ESCAPE '\'");
            args.Add("search", "%" + EscapeLike(q.SearchTerm.ToLowerInvariant()) + "%");
        }
        if (!string.IsNullOrWhiteSpace(q.NameStartsWith))
        {
            conds.Add(@"lower(p.name) COLLATE ""C"" LIKE @prefix ESCAPE '\'");
            args.Add("prefix", EscapeLike(q.NameStartsWith.ToLowerInvariant()) + "%");
        }
        if (!string.IsNullOrWhiteSpace(q.NameLessThan))
        {
            conds.Add(@"lower(p.name) COLLATE ""C"" < @less");
            args.Add("less", q.NameLessThan.ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(q.NameStartsWithOrGreater))
        {
            conds.Add(@"lower(p.name) COLLATE ""C"" >= @greater");
            args.Add("greater", q.NameStartsWithOrGreater.ToLowerInvariant());
        }
        if (q.AppearsInItemIds is { Length: > 0 } items)
        {
            conds.Add("p.id IN (SELECT unnest(person_ids) FROM credits WHERE item_id = ANY(@items))");
            args.Add("items", items);
        }
        if (await KindFilterAsync(q, ct) is var (include, exclude) && (include is not null || exclude.Length > 0))
        {
            // Credited with a wanted kind, or with any kind but the excluded ones.
            conds.Add("""
                EXISTS (SELECT 1 FROM credits c, unnest(c.person_ids, c.kinds) AS u(pid, k)
                         WHERE c.person_ids @> ARRAY[p.id] AND u.pid = p.id
                           AND (@kinds::smallint[] IS NULL OR u.k = ANY(@kinds)) AND NOT (u.k = ANY(@excludedKinds)))
                """);
            args.Add("kinds", include);
            args.Add("excludedKinds", exclude);
        }

        var where = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";
        var limit = q.Limit is > 0 and var l ? l : 1000;
        args.Add("limit", limit);
        args.Add("offset", Math.Max(0, q.StartIndex));
        var sql = $"""
            SELECT p.id, p.name, p.image_url AS ImageUrl, p.provider_id AS ProviderId FROM persons p {where}
             ORDER BY lower(p.name) COLLATE "C", p.id LIMIT @limit OFFSET @offset
            """;
        return await db.Timed(sql, async () =>
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            var page = (await conn.QueryAsync<PersonInfo>(new CommandDefinition(sql, args, cancellationToken: ct))).ToList();
            var total = q.StartIndex + page.Count;
            if (page.Count == limit)
                total = await conn.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT count(*)::int FROM persons p {where}", args, cancellationToken: ct));
            return (page, total);
        });
    }

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private volatile Tuple<HashSet<short>, DateTime>? _kindsInUse;

    /// <summary>
    /// The kind filter that actually narrows anything: checking each person's credits is costly, and clients send filters that exclude
    /// kinds nobody has (jellyfin-web's search always excludes music artists). Null include = any kind.
    /// </summary>
    private async Task<(short[]? Include, short[] Exclude)> KindFilterAsync(PersonQuery q, CancellationToken ct)
    {
        if (q.PersonTypes is not { Length: > 0 } && q.ExcludePersonTypes is not { Length: > 0 }) return (null, []);
        var cached = _kindsInUse;
        if (cached is null || DateTime.UtcNow - cached.Item2 > TimeSpan.FromMinutes(10))
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            var used = (await conn.QueryAsync<short>(new CommandDefinition("SELECT DISTINCT unnest(kinds) FROM credits", cancellationToken: ct, commandTimeout: 120))).ToHashSet();
            _kindsInUse = cached = Tuple.Create(used, DateTime.UtcNow);
        }
        var inUse = cached.Item1;
        var include = q.PersonTypes is { Length: > 0 } inc ? inc.Select(PersonKinds.Code).Where(inUse.Contains).Distinct().ToArray() : null;
        if (include is not null && inUse.All(include.Contains)) include = null; // every kind there is: no filter
        var exclude = (q.ExcludePersonTypes ?? []).Select(PersonKinds.Code).Where(inUse.Contains).Distinct().ToArray();
        return (include, exclude);
    }

    public async Task<IReadOnlyList<Guid>> PageItemIdsAsync(Guid? afterId, int limit, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT item_id FROM credits WHERE (@after::uuid IS NULL OR item_id > @after) ORDER BY item_id LIMIT @limit",
            new { after = afterId, limit }, cancellationToken: ct))).ToList();
    }

    public async Task RemoveAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var chunk in itemIds.Chunk(10_000))
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM credits WHERE item_id = ANY(@ids)", new { ids = chunk }, cancellationToken: ct));
    }

    /// <summary>Removes people no item credits any more; returns how many.</summary>
    public async Task<int> RemoveUncreditedAsync(CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM persons p WHERE NOT EXISTS (SELECT 1 FROM credits c WHERE c.person_ids @> ARRAY[p.id])
            """, cancellationToken: ct, commandTimeout: 600));
    }
}
