using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>The WHERE conditions of <see cref="ItemRestrictions"/>, for any query over <c>items</c> (positional or Dapper parameters).</summary>
internal static class RestrictionSql
{
    // An episode or season has no rating of its own in most libraries: it inherits its series'.
    private static string Rating(string a) =>
        $"lower(coalesce(nullif({a}.official_rating, ''), (SELECT s.official_rating FROM items s WHERE s.id = {a}.series_id)))";

    /// <param name="alias">The alias of the items table in the query.</param>
    /// <param name="arg">Registers a parameter value and returns its placeholder.</param>
    public static List<string> Conditions(ItemRestrictions? r, string alias, Func<object, string> arg)
    {
        var conds = new List<string>();
        if (r is null || r.IsNone) return conds;
        if (r.Libraries is { } libs) conds.Add($"{alias}.library_id = ANY({arg(libs)})");
        if (r.BlockedRatings is { Length: > 0 } blocked) conds.Add($"coalesce({Rating(alias)}, '') <> ALL({arg(blocked)})");
        if (r.UnratedBlockedTypes is { Length: > 0 } types)
            conds.Add($"({alias}.type <> ALL({arg(types)}) OR {Rating(alias)} = ANY({arg(r.KnownRatings ?? [])}))");
        return conds;
    }

    /// <summary>Dapper form: the conditions with named parameters added to <paramref name="parameters"/>.</summary>
    public static string Sql(ItemRestrictions? r, string alias, Dapper.DynamicParameters parameters)
    {
        var n = 0;
        var conds = Conditions(r, alias, value =>
        {
            var name = "r" + n++;
            parameters.Add(name, value);
            return "@" + name;
        });
        return conds.Count == 0 ? "" : " AND " + string.Join(" AND ", conds);
    }
}
