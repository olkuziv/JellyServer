using System.IO.Compression;
using System.Text.Json;
using JellyServer.Plugins;
using JellyServer.Serialization;

namespace JellyServer.Api.Endpoints;

/// <summary>
/// Dashboard "Plugins": what is installed, what the repositories offer, install, enable, disable and remove.
/// The same calls jellyfin-web's plugin pages make; plugins themselves load at startup, so changes wait for a restart.
/// </summary>
public static class PluginEndpoints
{
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("").RequireAdmin();
        admin.MapGet("/Plugins", (PluginManager plugins) => Results.Json(plugins.Installed().ToList(), AppJson.Default.ListPluginInfoDto));
        admin.MapPost("/Plugins/{id}/{version}/Enable", (string id, PluginManager plugins) => Change(id, plugins.Enable));
        admin.MapPost("/Plugins/{id}/{version}/Disable", (string id, PluginManager plugins) => Change(id, plugins.Disable));
        admin.MapDelete("/Plugins/{id}/{version}", (string id, PluginManager plugins) => Change(id, plugins.Uninstall));
        admin.MapDelete("/Plugins/{id}", (string id, PluginManager plugins) => Change(id, plugins.Uninstall));
        // A plugin's settings page is generated from the settings it declares; plugins have no image.
        admin.MapGet("/Plugins/{id}/Configuration", (string id, PluginManager plugins) =>
            Guid.TryParse(id, out var guid) && plugins.Settings(guid) is { } settings ? Results.Json(settings, AppJson.Default.PluginSettingsDto) : Results.NotFound());
        admin.MapPost("/Plugins/{id}/Configuration", SaveSettings);
        admin.MapGet("/Plugins/{id}/{version}/Image", () => Results.NotFound());
        admin.MapGet("/web/ConfigurationPages", (bool? enableInMainMenu, PluginManager plugins) =>
            Results.Json(plugins.ConfigurationPages(enableInMainMenu).ToList(), AppJson.Default.ListConfigurationPageInfoDto));
        admin.MapGet("/web/ConfigurationPage", (string? name, PluginManager plugins) =>
            name is not null && plugins.SettingsPage(name) is { } html ? Results.Content(html, "text/html; charset=utf-8") : Results.NotFound());

        admin.MapGet("/Packages", async (PluginManager plugins, CancellationToken ct) =>
            Results.Json((await plugins.PackagesAsync(ct)).ToList(), AppJson.Default.ListPackageInfoDto));
        admin.MapGet("/Packages/{name}", async (string name, Guid? assemblyGuid, PluginManager plugins, CancellationToken ct) =>
            await plugins.PackageAsync(name, assemblyGuid, ct) is { } package ? Results.Json(package, AppJson.Default.PackageInfoDto) : Results.NotFound());
        admin.MapPost("/Packages/Installed/{name}", Install);
        admin.MapDelete("/Packages/Installing/{id}", (string id) => Results.NoContent());

        admin.MapGet("/Repositories", async (PluginManager plugins, CancellationToken ct) =>
            Results.Json((await plugins.RepositoriesAsync(ct)).ToList(), AppJson.Default.ListRepositoryInfoDto));
        admin.MapPost("/Repositories", SetRepositories);
    }

    private static IResult Change(string id, Func<Guid, bool> action) =>
        Guid.TryParse(id, out var guid) && action(guid) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> Install(string name, Guid? assemblyGuid, string? version, string? repositoryUrl, PluginManager plugins,
        ILogger<PluginManager> log, HttpContext ctx)
    {
        try
        {
            return await plugins.InstallAsync(name, assemblyGuid, version, repositoryUrl, ctx.RequestAborted) ? Results.NoContent() : Results.NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Installing plugin {Name} failed: {Error}", name, ex.Message);
            return Results.Problem(ex.Message, statusCode: 400, title: "Plugin installation failed");
        }
    }

    private static async Task<IResult> SaveSettings(string id, HttpContext ctx, PluginManager plugins)
    {
        if (!Guid.TryParse(id, out var guid)) return Results.NotFound();
        PluginSettingsUpdateDto? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJson.Default.PluginSettingsUpdateDto, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Body must be {\"Values\": {key: value}}");
        }
        if (body is null) return Results.BadRequest("Body must be {\"Values\": {key: value}}");

        return await plugins.SaveSettingsAsync(guid, body.Values, ctx.RequestAborted) switch
        {
            null => Results.NotFound(),
            { Count: > 0 } errors => Results.Problem(string.Join("; ", errors), statusCode: 400, title: "Invalid settings"),
            _ => Results.NoContent(),
        };
    }

    private static async Task<IResult> SetRepositories(HttpContext ctx, PluginManager plugins)
    {
        RepositoryInfoDto[]? repositories;
        try
        {
            repositories = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJson.Default.RepositoryInfoDtoArray, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Body must be an array of repositories");
        }
        if (repositories is null) return Results.BadRequest("Body must be an array of repositories");
        try
        {
            await plugins.SetRepositoriesAsync(repositories, ctx.RequestAborted);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 400, title: "Invalid repository");
        }
    }
}
