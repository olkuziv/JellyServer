using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using Microsoft.Extensions.Caching.Memory;
using JellyServer.Identity.Contracts;

namespace JellyServer.Api;

/// <summary>Values from the <c>Authorization: MediaBrowser Client="…", Device="…", DeviceId="…", Version="…", Token="…"</c> header.</summary>
public sealed record AuthHeader(string Client, string Device, string DeviceId, string Version, string? Token)
{
    public static AuthHeader Parse(HttpRequest req)
    {
        string client = "", device = "", deviceId = "", version = "";
        string? token = null;
        var raw = req.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw)) raw = req.Headers["X-Emby-Authorization"].ToString();
        var space = raw.IndexOf(' ');
        if (space > 0 && raw[..space] is var scheme &&
            (scheme.Equals("MediaBrowser", StringComparison.OrdinalIgnoreCase) || scheme.Equals("Emby", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var part in SplitParams(raw[(space + 1)..]))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq].Trim();
                var value = Uri.UnescapeDataString(part[(eq + 1)..].Trim().Trim('"'));
                switch (key.ToLowerInvariant())
                {
                    case "client": client = value; break;
                    case "device": device = value; break;
                    case "deviceid": deviceId = value; break;
                    case "version": version = value; break;
                    case "token": token = value; break;
                }
            }
        }
        token ??= NonEmpty(req.Headers["X-Emby-Token"]) ?? NonEmpty(req.Headers["X-MediaBrowser-Token"])
                  ?? NonEmpty(req.Query["api_key"]) ?? NonEmpty(req.Query["ApiKey"]);
        if (deviceId.Length == 0) deviceId = req.Query["deviceId"].ToString();
        return new AuthHeader(client, device, deviceId, version, token);
    }

    private static string? NonEmpty(Microsoft.Extensions.Primitives.StringValues v) => v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;

    private static IEnumerable<string> SplitParams(string s)
    {
        var start = 0;
        var inQuote = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inQuote = !inQuote;
            else if (s[i] == ',' && !inQuote)
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        if (start < s.Length) yield return s[start..];
    }
}

public sealed record AuthInfo(User User, AuthSession Session, AuthHeader Header);

/// <summary>Resolves tokens with a short in-process cache in front of Redis/PostgreSQL.</summary>
public sealed class AuthService(UserRepository users, IMemoryCache memory, SessionRegistry sessions, ILogger<AuthService> log)
{
    private const string ItemKey = "auth";

    public async Task<AuthInfo?> ResolveAsync(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(ItemKey, out var existing)) return existing as AuthInfo;
        var header = AuthHeader.Parse(ctx.Request);
        AuthInfo? info = null;
        if (!string.IsNullOrEmpty(header.Token))
        {
            try
            {
                info = await ResolveTokenAsync(header);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Token lookup failed");
            }
        }
        ctx.Items[ItemKey] = info;
        if (info is not null) sessions.Touch(info);
        return info;
    }

    private async Task<AuthInfo?> ResolveTokenAsync(AuthHeader header)
    {
        var cacheKey = "auth:" + header.Token;
        if (!memory.TryGetValue(cacheKey, out (AuthSession Session, User User)? entry))
        {
            var session = await users.SessionByTokenAsync(header.Token!);
            var user = session is null ? null : await users.ByIdAsync(session.UserId);
            entry = session is null || user is null ? null : (session, user);
            memory.Set(cacheKey, entry, TimeSpan.FromSeconds(entry is null ? 5 : 60));
        }
        if (entry is not { } e) return null;
        var h = header with
        {
            DeviceId = header.DeviceId.Length > 0 ? header.DeviceId : e.Session.DeviceId,
            Client = header.Client.Length > 0 ? header.Client : e.Session.Client,
            Device = header.Device.Length > 0 ? header.Device : e.Session.DeviceName,
        };
        return new AuthInfo(e.User, e.Session, h);
    }

    public void Forget(string token) => memory.Remove("auth:" + token);
}

public static class AuthExtensions
{
    public static AuthInfo Auth(this HttpContext ctx) =>
        ctx.Items.TryGetValue("auth", out var a) && a is AuthInfo info ? info : throw new InvalidOperationException("Endpoint is missing RequireAuth()");

    public static AuthInfo? AuthOrNull(this HttpContext ctx) =>
        ctx.Items.TryGetValue("auth", out var a) ? a as AuthInfo : null;

    /// <summary>Rejects requests without a valid access token.</summary>
    public static TBuilder RequireToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var auth = await ctx.HttpContext.RequestServices.GetRequiredService<AuthService>().ResolveAsync(ctx.HttpContext);
            return auth is null ? Results.Unauthorized() : await next(ctx);
        });

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var auth = await ctx.HttpContext.RequestServices.GetRequiredService<AuthService>().ResolveAsync(ctx.HttpContext);
            if (auth is null) return Results.Unauthorized();
            return auth.User.IsAdmin ? await next(ctx) : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
}
