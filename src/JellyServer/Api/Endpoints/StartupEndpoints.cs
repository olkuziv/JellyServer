using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JellyServer.Configuration;
using JellyServer.Identity;
using JellyServer.Settings;
using Microsoft.Extensions.Options;
using JellyServer.Identity.Contracts;
using JellyServer.Settings.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>
/// Jellyfin's first-run wizard (/Startup/*): server language, the first administrator, remote access, completion.
/// Open to everyone until the wizard is completed, administrators only afterwards (see <see cref="SetupAccess"/>).
/// </summary>
public static partial class StartupEndpoints
{
    // Jellyfin's rule for user names; the OS user name is the wizard's default account name when it fits.
    [GeneratedRegex(@"^[\w\-'._@+]+( [\w\-'._@+]+)*$")]
    private static partial Regex ValidUserName();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/Startup").RequireSetupOrAdmin();
        g.MapGet("/Configuration", GetConfiguration);
        g.MapPost("/Configuration", SaveConfiguration);
        g.MapPost("/RemoteAccess", SaveRemoteAccess);
        g.MapGet("/User", GetFirstUser);
        g.MapGet("/FirstUser", GetFirstUser);
        g.MapPost("/User", SaveFirstUser);
        g.MapPost("/Complete", Complete);
    }

    public static bool IsValidUserName(string name) => ValidUserName().IsMatch(name);

    private static async Task<IResult> GetConfiguration(IServerConfiguration config, IOptions<ServerOptions> options, CancellationToken ct)
    {
        var doc = await config.GetAsync(ct);
        return SetupAccess.Json(new JsonObject
        {
            ["ServerName"] = doc["ServerName"]?.GetValue<string>() ?? options.Value.Name,
            ["UICulture"] = doc["UICulture"]?.GetValue<string>() ?? "en-US",
            ["MetadataCountryCode"] = doc["MetadataCountryCode"]?.GetValue<string>() is { Length: > 0 } cc ? cc : "US",
            ["PreferredMetadataLanguage"] = doc["PreferredMetadataLanguage"]?.GetValue<string>() is { Length: > 0 } lang ? lang : "en",
        });
    }

    private static async Task<IResult> SaveConfiguration(HttpContext ctx, IServerConfiguration config)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is not { } body) return Results.BadRequest("Body must be a JSON object");
        await config.UpdateAsync(d =>
        {
            d["ServerName"] = body.String("ServerName") ?? "";
            d["UICulture"] = body.String("UICulture") ?? "";
            d["MetadataCountryCode"] = body.String("MetadataCountryCode") ?? "";
            d["PreferredMetadataLanguage"] = body.String("PreferredMetadataLanguage") ?? "";
        }, ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveRemoteAccess(HttpContext ctx, INamedConfigurationStore store)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is not { } body) return Results.BadRequest("Body must be a JSON object");
        var network = await store.GetAsync("network", ctx.RequestAborted) is { } json && JsonNode.Parse(json) is JsonObject o ? o : new JsonObject();
        network["EnableRemoteAccess"] = body.Bool("EnableRemoteAccess") ?? true;
        await store.SaveAsync("network", network.ToJsonString(), ctx.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>The wizard edits the first account; it is created here with an empty password when none exists.</summary>
    private static async Task<IResult> GetFirstUser(UserRepository users)
    {
        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name) || !ValidUserName().IsMatch(name)) name = "MyJellyfinUser";
        var user = await users.EnsureFirstUserAsync(name);
        return SetupAccess.Json(new JsonObject { ["Name"] = user.Name });
    }

    private static async Task<IResult> SaveFirstUser(HttpContext ctx, UserRepository users)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ctx.RequestAborted) is not { } body) return Results.BadRequest("Body must be a JSON object");
        if (await users.FirstAsync() is not { } user) return Results.NotFound();
        if (!string.IsNullOrEmpty(user.PasswordHash)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var password = body.String("Password");
        if (string.IsNullOrWhiteSpace(password)) return Results.BadRequest("Password must not be empty");

        if (body.String("Name") is { } name && !name.Equals(user.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name) || !ValidUserName().IsMatch(name.Trim())) return Results.BadRequest("Invalid user name");
            if (!await users.RenameAsync(user.Id, name.Trim())) return Results.Conflict("A user with that name already exists");
        }
        await users.SetPasswordAsync(user.Id, password);
        return Results.NoContent();
    }

    private static async Task<IResult> Complete(IServerConfiguration config, CancellationToken ct)
    {
        await config.CompleteStartupAsync(ct);
        return Results.NoContent();
    }
}
