using JellyServer.Configuration;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace JellyServer.Api.Endpoints;

/// <summary>
/// Serves a jellyfin-web build at /web. Mobile apps (iOS/Android) and browsers load the
/// UI from the server; TV apps such as Tizen bundle their own copy and only use the API.
/// </summary>
public static class WebClient
{
    public static void Map(WebApplication app, ServerOptions options, ILogger log)
    {
        var dir = options.WebDir is { Length: > 0 } d ? Path.GetFullPath(d, AppContext.BaseDirectory) : null;
        if (dir is null || !File.Exists(Path.Combine(dir, "index.html")))
        {
            log.LogWarning("Web client not found at {Dir}; only API clients (e.g. Tizen) will work", dir ?? "(disabled)");
            app.MapGet("/", () => Results.Text($"{options.Name} - Jellyfin-compatible API. Connect with a Jellyfin client."));
            return;
        }

        var files = new PhysicalFileProvider(dir);
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".json"] = "application/json";
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.MapGet("/", () => Results.Redirect("web/"));
        // Routing treats /web and /web/ alike; relative asset URLs need the trailing slash.
        app.MapGet("/web", (HttpContext ctx) => ctx.Request.Path.Value!.EndsWith('/')
            ? Results.File(Path.Combine(dir, "index.html"), "text/html")
            : Results.Redirect("web/"));
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            RequestPath = "/web",
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = ctx =>
            {
                // Bundles carry content hashes in their names; index/config must always revalidate.
                var name = ctx.File.Name;
                ctx.Context.Response.Headers.CacheControl =
                    name is "index.html" or "config.json" or "serviceworker.js" ? "no-cache" : "public, max-age=604800";
            },
        });
        log.LogInformation("Serving web client from {Dir}", dir);
    }
}
