using JellyServer.Catalog;
using JellyServer.Collections.Contracts;
using JellyServer.UserData.Contracts;

namespace JellyServer.Api;

/// <summary>
/// <c>/Items</c> for playlists and box sets: the lists live in the Collections module, so the catalog's SQL cannot filter or sort
/// them; there are few of them, so this is done in memory with the same query parameters.
/// </summary>
public sealed class CollectionListing(ICollectionManager collections, ItemRepository items, IUserDataStore userData)
{
    /// <param name="libraryId">A library the box sets must have an item from (a movies library's "Collections" tab).</param>
    public async Task<(List<Collection> Page, int Total)> QueryAsync(HttpContext ctx, IReadOnlyCollection<string> kinds, Guid? libraryId)
    {
        var q = ctx.Request.Query;
        var ct = ctx.RequestAborted;
        var me = ctx.Auth().User.Id;
        IEnumerable<Collection> all = [];
        foreach (var kind in kinds.Distinct()) all = all.Concat(await collections.ListAsync(kind, me, ct));
        all = await VisibleAsync(me, all, ct);
        if (libraryId is { } requestedLib && !await ctx.CanAccessLibraryAsync(requestedLib)) return ([], 0);

        if (q.Str("searchTerm") is { } term) all = all.Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (q.Str("nameStartsWith") is { } prefix) all = all.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (q.Str("nameLessThan") is { } less) all = all.Where(c => string.Compare(c.Name, less, StringComparison.OrdinalIgnoreCase) < 0);
        if (q.Guids("ids") is { Length: > 0 } ids) all = all.Where(c => ids.Contains(c.Id));
        if (q.Guids("excludeItemIds") is { Length: > 0 } excluded) all = all.Where(c => !excluded.Contains(c.Id));
        var favorite = q.Bool("isFavorite") ?? (q.List("filters").Any(f => f.StartsWith("IsFavorite", StringComparison.OrdinalIgnoreCase)) ? true : null);
        if (favorite is { } fav)
        {
            var favorites = (await userData.FavoriteIdsAsync(me, ct)).ToHashSet();
            all = all.Where(c => favorites.Contains(c.Id) == fav);
        }
        var list = all.ToList();

        if (libraryId is { } lib && list.Count > 0)
        {
            // Box sets that hold something of that library (empty ones belong to no library).
            var entries = await collections.EntriesAsync(list.Select(c => c.Id).ToList(), ct);
            var inLibrary = (await items.GetManyAsync(entries.Values.SelectMany(e => e).Distinct().ToList(), ct))
                .Where(i => i.LibraryId == lib).Select(i => i.Id).ToHashSet();
            list = list.Where(c => entries.GetValueOrDefault(c.Id)?.Any(inLibrary.Contains) == true).ToList();
        }

        list = Sort(list, q.List("sortBy"), q.List("sortOrder"));
        var start = Math.Max(0, q.Int("startIndex") ?? 0);
        IEnumerable<Collection> page = list.Skip(start);
        if (q.Int("limit") is > 0 and var limit) page = page.Take(limit);
        return (page.ToList(), list.Count);
    }

    /// <summary>
    /// The lists a user sees: playlists as they are (their lines are filtered when read), box sets unless every item in them is hidden
    /// from the user (an empty box set stays).
    /// </summary>
    public async Task<List<Collection>> VisibleAsync(Guid userId, IEnumerable<Collection> all, CancellationToken ct)
    {
        var list = all.ToList();
        var boxSets = list.Where(c => !c.IsPlaylist).ToList();
        if (boxSets.Count == 0 || await items.VisibleIdsAsync(userId, ct) is not { } visibleIds) return list;
        var visible = visibleIds.ToHashSet();
        var entries = await collections.EntriesAsync(boxSets.Select(c => c.Id).ToList(), ct);
        var hidden = boxSets.Where(c => entries.GetValueOrDefault(c.Id) is { Count: > 0 } e && !e.Any(visible.Contains)).Select(c => c.Id).ToHashSet();
        return hidden.Count == 0 ? list : list.Where(c => !hidden.Contains(c.Id)).ToList();
    }

    private static List<Collection> Sort(List<Collection> list, string[] by, string[] order)
    {
        if (by.Any(b => b.Equals("Random", StringComparison.OrdinalIgnoreCase))) return [.. list.OrderBy(_ => Random.Shared.Next())];
        IOrderedEnumerable<Collection>? sorted = null;
        for (var i = 0; i < by.Length; i++)
        {
            var desc = (i < order.Length ? order[i] : order.LastOrDefault())?.Equals("Descending", StringComparison.OrdinalIgnoreCase) == true;
            Func<Collection, IComparable?>? key = by[i].ToLowerInvariant() switch
            {
                "sortname" or "name" => c => c.Name.ToLowerInvariant(),
                "datecreated" => c => c.DateCreated,
                "datelastcontentadded" => c => c.DateLastMediaAdded ?? c.DateCreated,
                _ => null,
            };
            if (key is null) continue;
            sorted = sorted is null
                ? desc ? list.OrderByDescending(key) : list.OrderBy(key)
                : desc ? sorted.ThenByDescending(key) : sorted.ThenBy(key);
        }
        return [.. (sorted ?? list.OrderBy(c => c.Name.ToLowerInvariant())).ThenBy(c => c.Id)];
    }
}
