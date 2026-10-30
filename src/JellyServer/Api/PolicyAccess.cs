using System.Net;
using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Api.Endpoints;

namespace JellyServer.Api;

/// <summary>What the signed-in user's policy allows: content access and the feature switches of Jellyfin's user policy.</summary>
public static class PolicyAccess
{
    private const string PolicyKey = "policy";

    /// <summary>The caller's policy, parsed once per request.</summary>
    public static UserPolicy Policy(this HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(PolicyKey, out var cached) && cached is UserPolicy p) return p;
        p = UserEndpoints.ParsePolicy(ctx.Auth().User);
        ctx.Items[PolicyKey] = p;
        return p;
    }

    /// <summary>Whether the caller may see the item: its library is theirs and its rating is within their limit.</summary>
    public static Task<bool> CanAccessAsync(this HttpContext ctx, Item item) =>
        ctx.RequestServices.GetRequiredService<ItemRepository>().CanAccessAsync(ctx.Auth().User.Id, item, ctx.RequestAborted);

    public static async Task<bool> CanAccessLibraryAsync(this HttpContext ctx, Guid libraryId) =>
        (await ctx.RequestServices.GetRequiredService<ItemRepository>().RestrictionsAsync(ctx.Auth().User.Id, ctx.RequestAborted)).AllowsLibrary(libraryId);

    /// <summary>The caller's restrictions (library access and parental control).</summary>
    public static ValueTask<ItemRestrictions> RestrictionsAsync(this HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ItemRepository>().RestrictionsAsync(ctx.Auth().User.Id, ctx.RequestAborted);

    /// <summary>Of the requested libraries (all when none were), the ones the caller may open; null means every library.</summary>
    public static async Task<Guid[]?> ScopeLibrariesAsync(this HttpContext ctx, Guid[]? requested)
    {
        var allowed = (await ctx.RestrictionsAsync()).Libraries;
        if (allowed is null) return requested;
        return requested is null ? allowed : [.. requested.Where(allowed.Contains)];
    }

    // ---- network ----

    /// <summary>
    /// The address the request really comes from: behind a reverse proxy on the local network the proxy's X-Forwarded-For is trusted,
    /// from anywhere else it is not (it could be forged).
    /// </summary>
    public static IPAddress? ClientIp(this HttpContext ctx)
    {
        var peer = ctx.Connection.RemoteIpAddress;
        if (peer is not null && IsLocal(peer) && ctx.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded
            && IPAddress.TryParse(forwarded.Split(',')[0].Trim(), out var client))
            return client;
        return peer;
    }

    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fe80::/10, fec0::/10, fc00::/7
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public static bool IsLocalRequest(this HttpContext ctx) => ctx.ClientIp() is not { } ip || IsLocal(ip);

    // ---- schedule ----

    /// <summary>Jellyfin's parental schedule: with no schedules always allowed, otherwise only inside one of them.</summary>
    public static bool InSchedule(UserPolicy policy, DateTime now)
    {
        if (policy.AccessSchedules.Count == 0) return true;
        var hour = now.TimeOfDay.TotalHours;
        return policy.AccessSchedules.Any(s => DayMatches(s.DayOfWeek, now.DayOfWeek) && hour >= s.StartHour && hour <= s.EndHour);
    }

    private static bool DayMatches(string day, DayOfWeek today) => day switch
    {
        "Everyday" => true,
        "Weekday" => today is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        "Weekend" => today is DayOfWeek.Saturday or DayOfWeek.Sunday,
        _ => Enum.TryParse<DayOfWeek>(day, true, out var d) && d == today,
    };

    // ---- feature switches ----

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>Null when the caller may play media, otherwise the response to give.</summary>
    public static IResult? DenyPlayback(this HttpContext ctx) => ctx.Policy().EnableMediaPlayback ? null : Forbidden();

    public static IResult? DenyDownload(this HttpContext ctx) => ctx.Policy().EnableContentDownloading ? null : Forbidden();
}
