using System.Text.Json.Nodes;
using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Metadata;
using JellyServer.Metadata.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>What the dashboard offers for a library's metadata, and "Refresh metadata" for an item.</summary>
public static class MetadataEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGroup("/Libraries").RequireSetupOrAdmin().MapGet("/AvailableOptions", AvailableOptions);
        app.MapGroup("/Items").RequireAdmin().MapPost("/{itemId}/Refresh", Refresh);
    }

    /// <summary>The item types a library of this content type holds (Jellyfin's representative types).</summary>
    private static string[] TypesOf(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "movies" => [ItemTypes.Movie],
        "tvshows" => [ItemTypes.Series, ItemTypes.Season, ItemTypes.Episode],
        _ => [ItemTypes.Series, ItemTypes.Season, ItemTypes.Episode, ItemTypes.Movie],
    };

    private static JsonObject Info(string name) => new() { ["Name"] = name, ["DefaultEnabled"] = true };

    private static IResult AvailableOptions(HttpContext ctx, IMetadataProviders providers)
    {
        var contentType = ctx.Request.Query.Str("libraryContentType");
        var typeOptions = new JsonArray();
        foreach (var type in TypesOf(contentType))
        {
            var forType = providers.Available.Where(p => p.ItemTypes.Contains(type)).ToList();
            // Seasons and episodes only have a poster (the episode still).
            var images = forType.SelectMany(p => p.ImageTypes).Distinct().Where(t => type is ItemTypes.Movie or ItemTypes.Series || t == "Primary").ToList();
            var defaults = new JsonArray();
            foreach (var (kind, limit) in MetadataPlanner.DefaultImageLimits(type))
            {
                var option = new JsonObject { ["Type"] = kind, ["Limit"] = limit };
                if (kind == "Backdrop") option["MinWidth"] = 1280;
                defaults.Add(option);
            }
            typeOptions.Add(new JsonObject
            {
                ["Type"] = type,
                ["MetadataFetchers"] = new JsonArray(forType.Select(p => (JsonNode)Info(p.Name)).ToArray()),
                ["ImageFetchers"] = new JsonArray(forType.Where(p => p.ImageTypes.Count > 0).Select(p => (JsonNode)Info(p.Name)).ToArray()),
                ["SimilarItemProviders"] = new JsonArray(),
                ["SupportedImageTypes"] = new JsonArray(images.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["DefaultImageOptions"] = defaults,
            });
        }
        return SetupAccess.Json(new JsonObject
        {
            ["MetadataSavers"] = new JsonArray(),
            ["MetadataReaders"] = new JsonArray(Info("Nfo")),
            ["SubtitleFetchers"] = new JsonArray(),
            ["LyricFetchers"] = new JsonArray(),
            ["MediaSegmentProviders"] = new JsonArray(),
            ["TypeOptions"] = typeOptions,
        });
    }

    /// <summary>
    /// POST /Items/{itemId}/Refresh. The modes follow Jellyfin: None does nothing, anything else looks the item up again
    /// (folders include everything below them); the replaceAll flags let the provider overwrite what is there.
    /// </summary>
    private static async Task<IResult> Refresh(HttpContext ctx, Guid itemId, ItemRepository items, IMetadataRefresh refresh)
    {
        if (await items.GetAsync(itemId, ctx.RequestAborted) is null) return Results.NotFound();
        var q = ctx.Request.Query;
        bool Active(string? mode) => !string.IsNullOrEmpty(mode) && !mode.Equals("None", StringComparison.OrdinalIgnoreCase);
        var metadata = Active(q.Str("metadataRefreshMode"));
        var images = Active(q.Str("imageRefreshMode"));
        var replaceMetadata = q.Bool("replaceAllMetadata") ?? false;
        var replaceImages = q.Bool("replaceAllImages") ?? false;
        if (metadata || images || replaceMetadata || replaceImages)
            await refresh.RefreshAsync(new MetadataRefreshRequest(itemId, Recursive: true, replaceMetadata, replaceImages), ctx.RequestAborted);
        return Results.NoContent();
    }
}