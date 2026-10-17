using JellyServer.People.Contracts;
using JellyServer.Serialization;
using JellyServer.UserData.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Jellyfin's PersonsController: cast and crew as items of their own (search, favourites, person pages).</summary>
public static class PersonEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("").RequireToken();
        g.MapGet("/Persons", GetPersons);
        g.MapGet("/Persons/{name}", GetPerson);
    }

    private static async Task<IResult> GetPersons(HttpContext ctx, IPeopleStore people, IUserDataStore userData, DtoBuilder dto)
    {
        var q = ctx.Request.Query;
        var ct = ctx.RequestAborted;
        var query = new PersonQuery
        {
            SearchTerm = q.Str("searchTerm"), NameStartsWith = q.Str("nameStartsWith"), NameLessThan = q.Str("nameLessThan"),
            NameStartsWithOrGreater = q.Str("nameStartsWithOrGreater"),
            PersonTypes = q.List("personTypes") is { Length: > 0 } types ? types : null,
            ExcludePersonTypes = q.List("excludePersonTypes") is { Length: > 0 } excluded ? excluded : null,
            AppearsInItemIds = q.Guid("appearsInItemId") is { } item ? [item] : null,
            StartIndex = Math.Max(0, q.Int("startIndex") ?? 0), Limit = q.Int("limit"),
        };
        var favorite = q.Bool("isFavorite") ?? (q.List("filters").Contains("IsFavorite", StringComparer.OrdinalIgnoreCase) ? true : null);
        if (favorite is { } fav)
        {
            var favorites = await userData.FavoriteIdsAsync(ctx.Auth().User.Id, ct);
            if (fav) query.Ids = [.. favorites];
            else
            {
                // Not favourites: whatever is left after taking them out (rarely asked; done on the page).
                var (all, _) = await people.QueryAsync(new PersonQuery
                {
                    SearchTerm = query.SearchTerm, NameStartsWith = query.NameStartsWith, PersonTypes = query.PersonTypes,
                    ExcludePersonTypes = query.ExcludePersonTypes, AppearsInItemIds = query.AppearsInItemIds, Limit = 100_000,
                }, ct);
                var rest = all.Where(p => !favorites.Contains(p.Id)).ToList();
                IEnumerable<PersonInfo> slice = rest.Skip(query.StartIndex);
                if (query.Limit is > 0 and var l) slice = slice.Take(l);
                return Page(await dto.PersonsAsync(slice.ToList(), ctx.Auth(), ct), rest.Count, query.StartIndex);
            }
        }
        var (page, total) = await people.QueryAsync(query, ct);
        return Page(await dto.PersonsAsync(page, ctx.Auth(), ct), total, query.StartIndex);
    }

    private static IResult Page(List<BaseItemDto> items, int total, int start) =>
        Results.Json(new QueryResult<BaseItemDto> { Items = items, TotalRecordCount = total, StartIndex = start }, AppJson.Default.QueryResultBaseItemDto);

    private static async Task<IResult> GetPerson(HttpContext ctx, IPeopleStore people, DtoBuilder dto)
    {
        var name = Uri.UnescapeDataString(ctx.Request.RouteValues["name"]?.ToString() ?? "");
        if ((await people.GetPersonsAsync([PersonIds.Of(name)], ctx.RequestAborted)).FirstOrDefault() is not { } person)
            return Results.NotFound("Person not found");
        return Results.Json((await dto.PersonsAsync([person], ctx.Auth(), ctx.RequestAborted))[0], AppJson.Default.BaseItemDto);
    }
}
