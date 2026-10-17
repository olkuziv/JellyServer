using System.Text.Json.Nodes;
using JellyServer.Catalog;
using JellyServer.Services;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Library/VirtualFolders: how libraries are added, changed and removed (dashboard and startup wizard).</summary>
public static class LibraryStructureEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/Library/VirtualFolders").RequireSetupOrAdmin();
        g.MapGet("", List);
        g.MapPost("", Add);
        g.MapDelete("", Remove);
        g.MapPost("/Name", Rename);
        g.MapPost("/Paths", AddPath);
        g.MapPost("/Paths/Update", UpdatePath);
        g.MapDelete("/Paths", RemovePath);
        g.MapPost("/LibraryOptions", UpdateOptions);
    }

    private static IResult ToResult(LibraryResult r) => r.Status switch
    {
        LibraryStatus.Ok => Results.NoContent(),
        LibraryStatus.NotFound => Results.NotFound(r.Message),
        LibraryStatus.Conflict => Results.Conflict(r.Message),
        _ => Results.BadRequest(r.Message),
    };

    private static IResult List(LibraryRegistry libs)
    {
        var array = new JsonArray();
        foreach (var l in libs.All)
            array.Add(new JsonObject
            {
                ["Name"] = l.Name,
                ["Locations"] = new JsonArray(l.Paths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["CollectionType"] = LibraryTypes.ForClient(l.CollectionType),
                ["LibraryOptions"] = LibraryOptionsJson.WithPaths(l.OptionsJson, l.Paths),
                ["ItemId"] = l.Id.N(),
                ["PrimaryImageItemId"] = l.Images.Primary is { } ? l.Id.N() : null,
                ["RefreshProgress"] = null,
                ["RefreshStatus"] = "Idle",
            });
        return SetupAccess.Json(array);
    }

    private static async Task<IResult> Add(HttpContext ctx, LibraryManager libs)
    {
        var q = ctx.Request.Query;
        // Paths come comma separated and/or repeated; the dashboard also lists them in the options body.
        var paths = q.List("paths").SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
        System.Text.Json.JsonElement? options = null;
        if (ctx.Request.ContentLength is not 0 && await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is { } body
            && body.TryGetProperty("LibraryOptions", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Object)
            options = o;
        return ToResult(await libs.AddAsync(q.Str("name"), q.Str("collectionType"), paths, options, q.Bool("refreshLibrary") ?? false, ctx.RequestAborted));
    }

    private static async Task<IResult> Remove(HttpContext ctx, LibraryManager libs) =>
        string.IsNullOrWhiteSpace(ctx.Request.Query.Str("name"))
            ? Results.BadRequest("name is required")
            : ToResult(await libs.RemoveAsync(ctx.Request.Query.Str("name")!, ctx.Request.Query.Bool("refreshLibrary") ?? false, ctx.RequestAborted));

    private static async Task<IResult> Rename(HttpContext ctx, LibraryManager libs)
    {
        var q = ctx.Request.Query;
        return ToResult(await libs.RenameAsync(q.Str("name") ?? "", q.Str("newName") ?? "", q.Bool("refreshLibrary") ?? false, ctx.RequestAborted));
    }

    private static async Task<IResult> AddPath(HttpContext ctx, LibraryManager libs)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is not { } body) return Results.BadRequest("Body must be a JSON object");
        var path = body.String("Path") ?? (body.TryGetProperty("PathInfo", out var info) ? info.String("Path") : null);
        return ToResult(await libs.AddPathAsync(body.String("Name") ?? "", path, ctx.Request.Query.Bool("refreshLibrary") ?? false, ctx.RequestAborted));
    }

    private static async Task<IResult> UpdatePath(HttpContext ctx, LibraryManager libs)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is not { } body || string.IsNullOrWhiteSpace(body.String("Name")))
            return Results.BadRequest("Name must not be null or empty");
        return ToResult(await libs.UpdatePathAsync(body.String("Name")!));
    }

    private static async Task<IResult> RemovePath(HttpContext ctx, LibraryManager libs)
    {
        var q = ctx.Request.Query;
        if (string.IsNullOrWhiteSpace(q.Str("name")) || string.IsNullOrWhiteSpace(q.Str("path"))) return Results.BadRequest("name and path are required");
        return ToResult(await libs.RemovePathAsync(q.Str("name")!, q.Str("path")!, q.Bool("refreshLibrary") ?? false, ctx.RequestAborted));
    }

    private static async Task<IResult> UpdateOptions(HttpContext ctx, LibraryManager libs)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is not { } body
            || !Guid.TryParse(body.String("Id"), out var id)
            || !body.TryGetProperty("LibraryOptions", out var options) || options.ValueKind != System.Text.Json.JsonValueKind.Object)
            return Results.BadRequest("Id and LibraryOptions are required");
        return ToResult(await libs.UpdateOptionsAsync(id, options, ctx.RequestAborted));
    }
}
