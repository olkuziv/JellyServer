using System.Text.Json;
using System.Text.Json.Nodes;
using JellyServer.Settings;
using JellyServer.Identity.Contracts;
using JellyServer.Settings.Contracts;

namespace JellyServer.Api;

public static class SetupAccess
{
    /// <summary>
    /// Jellyfin's <c>FirstTimeSetupOrElevated</c> policy: open to anyone while the startup wizard is not completed
    /// (the wizard runs before any account can log in), administrators only afterwards.
    /// </summary>
    public static TBuilder RequireSetupOrAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var sp = ctx.HttpContext.RequestServices;
            var auth = await sp.GetRequiredService<AuthService>().ResolveAsync(ctx.HttpContext);
            if (!await sp.GetRequiredService<IServerConfiguration>().IsStartupCompletedAsync(ctx.HttpContext.RequestAborted))
                return await next(ctx);
            if (auth is null) return Results.Unauthorized();
            return auth.User.IsAdmin ? await next(ctx) : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

    /// <summary>The request body as a JSON object, or null when it is missing or not an object.</summary>
    public static async Task<JsonElement?> ReadObjectAsync(HttpRequest req, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? String(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static IResult Json(JsonNode node) => Results.Text(node.ToJsonString(), "application/json");
}
