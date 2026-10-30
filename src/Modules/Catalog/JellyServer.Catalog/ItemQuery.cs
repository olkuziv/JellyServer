using System.Security.Cryptography;
using System.Text;
using Npgsql;
using JellyServer.Kernel;
using JellyServer.UserData.Contracts;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>Backend form of Jellyfin's /Items query parameters.</summary>
public sealed class ItemQuery
{
    public Guid UserId { get; set; }
    public Guid[]? LibraryIds { get; set; }
    /// <summary>Set by <see cref="ItemRepository.QueryAsync"/> from the user's policy; callers never set it.</summary>
    public ItemRestrictions? Restrictions { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? SeriesId { get; set; }
    public Guid? SeasonId { get; set; }
    public string[]? Types { get; set; }
    public string[]? ExcludeTypes { get; set; }
    public Guid[]? Ids { get; set; }
    public Guid[]? ExcludeIds { get; set; }
    /// <summary>Already normalised with <see cref="Scanning.ItemNaming.SearchText"/>.</summary>
    public string? SearchTerm { get; set; }
    public string? NameStartsWith { get; set; }
    public string? NameLessThan { get; set; }
    public string? NameStartsWithOrGreater { get; set; }
    public string[]? Genres { get; set; }
    public Guid[]? GenreIds { get; set; }
    public int[]? Years { get; set; }
    public string[]? OfficialRatings { get; set; }
    public int? ParentIndexNumber { get; set; }
    public int? IndexNumber { get; set; }
    public double? MinCommunityRating { get; set; }
    public bool? IsFavorite { get; set; }
    public bool? IsPlayed { get; set; }
    public bool IsResumable { get; set; }
    public List<(string Field, bool Descending)> Sort { get; set; } = [];
    public int StartIndex { get; set; }
    public int? Limit { get; set; }
    public bool IncludeTotal { get; set; } = true;

    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SortName"] = "i.sort_name",
        ["Name"] = "i.sort_name",
        ["SeriesSortName"] = "i.sort_name",
        ["ProductionYear"] = "i.production_year",
        ["PremiereDate"] = "i.premiere_date",
        ["DateCreated"] = "i.date_created",
        ["DateLastContentAdded"] = "coalesce(i.date_last_media_added, i.date_created)",
        ["CommunityRating"] = "i.community_rating",
        ["CriticRating"] = "i.critic_rating",
        ["Runtime"] = "i.runtime_ticks",
        ["OfficialRating"] = "i.official_rating",
        ["ParentIndexNumber"] = "i.parent_index_number",
        ["IndexNumber"] = "i.index_number",
        ["AiredEpisodeOrder"] = "i.parent_index_number|i.index_number",
        ["Random"] = "random()",
    };

    /// <summary>Sort keys that live in the UserData module: applied in memory after the catalog returned its IDs.</summary>
    private static readonly HashSet<string> UserDataSorts =
        new(["DatePlayed", "PlayCount", "IsFavoriteOrLiked", "IsPlayed", "IsUnplayed"], StringComparer.OrdinalIgnoreCase);

    public static bool IsUserDataSort(string field) => UserDataSorts.Contains(field);

    public bool NeedsUserData =>
        IsFavorite is not null || IsPlayed is not null || IsResumable || Sort.Any(s => IsUserDataSort(s.Field));

    public ItemQuery Clone() => (ItemQuery)MemberwiseClone();

    public bool IsRandom => Sort.Any(s => s.Field.Equals("Random", StringComparison.OrdinalIgnoreCase));

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Builds the shared FROM/WHERE (list + count) and ORDER BY with positional parameters. User-data filters and
    /// sorts are not part of the SQL: <see cref="ItemRepository.QueryAsync"/> resolves them through the UserData module.
    /// </summary>
    public (string FromWhere, string OrderBy, List<object> Args, int WhereArgCount) Build()
    {
        var args = new List<object>();
        string Arg(object v)
        {
            args.Add(v);
            return "$" + args.Count;
        }
        var conds = new List<string>();
        var from = "FROM items i";

        if (LibraryIds is { Length: 1 }) conds.Add($"i.library_id = {Arg(LibraryIds[0])}");
        else if (LibraryIds is { Length: > 1 }) conds.Add($"i.library_id = ANY({Arg(LibraryIds)})");
        conds.AddRange(RestrictionSql.Conditions(Restrictions, "i", Arg));
        if (ParentId is { } p) conds.Add($"i.parent_id = {Arg(p)}");
        if (SeriesId is { } sid) conds.Add($"i.series_id = {Arg(sid)}");
        if (SeasonId is { } ssid) conds.Add($"i.season_id = {Arg(ssid)}");
        if (Types is { Length: 1 }) conds.Add($"i.type = {Arg(Types[0])}");
        else if (Types is { Length: > 1 }) conds.Add($"i.type = ANY({Arg(Types)})");
        if (ExcludeTypes is { Length: > 0 }) conds.Add($"NOT (i.type = ANY({Arg(ExcludeTypes)}))");
        if (Ids is { Length: > 0 }) conds.Add($"i.id = ANY({Arg(Ids)})");
        if (ExcludeIds is { Length: > 0 }) conds.Add($"NOT (i.id = ANY({Arg(ExcludeIds)}))");
        if (!string.IsNullOrEmpty(SearchTerm)) conds.Add($"i.search_text LIKE {Arg("%" + EscapeLike(SearchTerm) + "%")}");
        if (!string.IsNullOrEmpty(NameStartsWith)) conds.Add($"i.sort_name LIKE {Arg(EscapeLike(NameStartsWith.ToLowerInvariant()) + "%")}");
        if (!string.IsNullOrEmpty(NameLessThan)) conds.Add($"i.sort_name < {Arg(NameLessThan.ToLowerInvariant())}");
        if (!string.IsNullOrEmpty(NameStartsWithOrGreater)) conds.Add($"i.sort_name >= {Arg(NameStartsWithOrGreater.ToLowerInvariant())}");
        if (Genres is { Length: > 0 }) conds.Add($"i.genres && {Arg(Genres)}");
        if (GenreIds is { Length: > 0 })
            conds.Add($"i.genres && (SELECT coalesce(array_agg(name), '{{}}') FROM genres WHERE id = ANY({Arg(GenreIds)}))");
        if (Years is { Length: > 0 }) conds.Add($"i.production_year = ANY({Arg(Years)})");
        if (OfficialRatings is { Length: > 0 }) conds.Add($"i.official_rating = ANY({Arg(OfficialRatings)})");
        if (ParentIndexNumber is { } pin) conds.Add($"i.parent_index_number = {Arg(pin)}");
        if (IndexNumber is { } ix) conds.Add($"i.index_number = {Arg(ix)}");
        if (MinCommunityRating is { } mcr) conds.Add($"i.community_rating >= {Arg((float)mcr)}");

        var fromWhere = conds.Count > 0 ? $"{from} WHERE {string.Join(" AND ", conds)}" : from;
        var whereArgs = args.Count;

        var order = new List<string>();
        if (!string.IsNullOrEmpty(SearchTerm) && Sort.Count == 0)
            order.Add($"(i.search_text LIKE {Arg(EscapeLike(SearchTerm) + "%")}) DESC"); // prefix matches first
        foreach (var (field, desc) in Sort)
        {
            if (!SortColumns.TryGetValue(field, out var col)) continue;
            foreach (var c in col.Split('|'))
                order.Add(c == "random()" ? c : c + (desc ? " DESC NULLS LAST" : " ASC NULLS LAST"));
        }
        if (order.Count == 0 || (Sort.Count == 0 && !string.IsNullOrEmpty(SearchTerm))) order.Add("i.sort_name ASC");
        order.Add("i.id"); // stable paging
        return (fromWhere, string.Join(", ", order), args, whereArgs);
    }

    /// <summary>Cache key derived from the final SQL and arguments: identical queries share results.</summary>
    public static string CacheKey(long generation, string sql, IEnumerable<object> args, bool includeTotal)
    {
        var sb = new StringBuilder(sql).Append('|').Append(includeTotal);
        foreach (var a in args)
        {
            sb.Append('|');
            if (a is Array arr) foreach (var e in arr) sb.Append(e).Append(',');
            else sb.Append(a);
        }
        return $"q:{generation}:{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))}";
    }
}

public sealed record QueryPage(List<Guid> Ids, int Total);

public sealed partial class ItemRepository
{
    private const int MaxLimit = 20000;

    /// <summary>
    /// One page of IDs plus the total. User-independent results are cached in Redis under the
    /// library generation, so any scan that changes data invalidates them all at once.
    /// </summary>
    public async Task<QueryPage> QueryAsync(ItemQuery q, CancellationToken ct = default)
    {
        // Every query made for a user is limited to what that user may see: endpoints cannot forget it.
        if (await RestrictionsOf(q.UserId, ct) is { IsNone: false } r)
        {
            q = q.Clone();
            q.Restrictions = r;
        }
        return q.NeedsUserData ? await QueryWithUserDataAsync(q, ct) : await QueryCoreAsync(q, !q.IsRandom, ct);
    }

    /// <summary>
    /// Favorites, played, resumable and "sort by play date/count" live in the UserData module's database, so no JOIN is
    /// possible. UserData answers with item IDs (positive filters narrow the catalog query, negative ones exclude), and a
    /// leading user-data sort key is applied in memory to the catalog's IDs. Cost is bounded by the user's own history.
    /// </summary>
    private async Task<QueryPage> QueryWithUserDataAsync(ItemQuery q, CancellationToken ct)
    {
        HashSet<Guid>? include = q.Ids is { Length: > 0 } ? [.. q.Ids] : null;
        var exclude = new HashSet<Guid>(q.ExcludeIds ?? []);
        void Narrow(IEnumerable<Guid> ids)
        {
            if (include is null) include = [.. ids];
            else include.IntersectWith(ids);
        }

        if (q.IsFavorite is { } fav)
        {
            var ids = await userData.FavoriteIdsAsync(q.UserId, ct);
            if (fav) Narrow(ids); else exclude.UnionWith(ids);
        }
        if (q.IsPlayed is { } played)
        {
            var ids = await userData.PlayedIdsAsync(q.UserId, ct);
            if (played) Narrow(ids); else exclude.UnionWith(ids);
        }
        if (q.IsResumable) Narrow(await userData.ResumableIdsAsync(q.UserId, ct));
        if (include is { Count: 0 }) return new QueryPage([], 0);

        var lead = q.Sort.TakeWhile(s => ItemQuery.IsUserDataSort(s.Field)).ToList();
        if (lead.Count == 0 && q.Sort.Count == 0 && q.IsResumable) lead.Add(("DatePlayed", true));

        var core = q.Clone();
        core.Ids = include?.ToArray();
        core.ExcludeIds = exclude.Count > 0 ? exclude.ToArray() : null;
        core.IsFavorite = null;
        core.IsPlayed = null;
        core.IsResumable = false;
        core.Sort = q.Sort.Skip(lead.Count).Where(s => !ItemQuery.IsUserDataSort(s.Field)).ToList();
        if (lead.Count == 0) return await QueryCoreAsync(core, false, ct);

        core.StartIndex = 0;
        core.Limit = null;
        var all = (await QueryCoreAsync(core, false, ct, unbounded: true)).Ids;
        var data = await userData.GetAllAsync(q.UserId, ct);
        var ordered = all.OrderBy(id => id, Comparer<Guid>.Create((a, b) => CompareUserData(lead, data, a, b))).ToList();
        var page = ordered.Skip(Math.Max(0, q.StartIndex));
        if (q.Limit is > 0 and var take) page = page.Take(take);
        return new QueryPage(page.ToList(), ordered.Count);
    }

    // NULLS LAST in both directions, like the SQL it replaces; ties keep the catalog's order (OrderBy is stable).
    private static int CompareUserData(List<(string Field, bool Descending)> keys, Dictionary<Guid, ItemUserData> data, Guid a, Guid b)
    {
        data.TryGetValue(a, out var da);
        data.TryGetValue(b, out var db2);
        foreach (var (field, desc) in keys)
        {
            var ka = UserDataKey(field, da);
            var kb = UserDataKey(field, db2);
            if (ka is null && kb is null) continue;
            if (ka is null) return 1;
            if (kb is null) return -1;
            var c = ka.CompareTo(kb);
            if (c != 0) return desc ? -c : c;
        }
        return 0;
    }

    private static IComparable? UserDataKey(string field, ItemUserData? d) => field.ToLowerInvariant() switch
    {
        "dateplayed" => d?.LastPlayedAt,
        "playcount" => d?.PlayCount ?? 0,
        "isfavoriteorliked" => d?.IsFavorite ?? false,
        _ => d?.Played ?? false, // IsPlayed, IsUnplayed
    };

    private async Task<QueryPage> QueryCoreAsync(ItemQuery q, bool cacheable, CancellationToken ct, bool unbounded = false)
    {
        var (fromWhere, orderBy, args, whereArgs) = q.Build();
        var limit = unbounded ? int.MaxValue : q.Limit is > 0 and <= MaxLimit ? q.Limit.Value : MaxLimit;
        var listSql = $"SELECT i.id {fromWhere} ORDER BY {orderBy} LIMIT {limit} OFFSET {Math.Max(0, q.StartIndex)}";

        string? key = null;
        if (cacheable)
        {
            key = ItemQuery.CacheKey(cache.Generation, listSql, args, q.IncludeTotal);
            if (await cache.GetAsync(key, CatalogJson.Default.QueryPage) is { } hit) return hit;
        }

        var page = await db.Timed(listSql, async () =>
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            var ids = new List<Guid>(Math.Min(limit, 1000));
            await using (var cmd = new NpgsqlCommand(listSql, conn))
            {
                foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            }
            var total = q.StartIndex + ids.Count;
            // A short page already tells us the total; only count when needed.
            if (q.IncludeTotal && ids.Count == limit)
            {
                await using var count = new NpgsqlCommand("SELECT count(*) " + fromWhere, conn);
                foreach (var a in args.Take(whereArgs)) count.Parameters.Add(new NpgsqlParameter { Value = a });
                total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            }
            return new QueryPage(ids, total);
        });

        if (key is not null) await cache.SetAsync(key, page, CatalogJson.Default.QueryPage, _cacheOpt.QueryTtl);
        return page;
    }
}