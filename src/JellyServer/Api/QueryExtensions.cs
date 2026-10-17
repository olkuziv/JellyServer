using System.Text.Json;
using JellyServer.Serialization;

namespace JellyServer.Api;

/// <summary>Typed access to (already case-insensitive) query parameters.</summary>
public static class QueryExtensions
{
    public static string? Str(this IQueryCollection q, string key) =>
        q.TryGetValue(key, out var v) && v.Count > 0 && !string.IsNullOrWhiteSpace(v[0]) ? v[0]!.Trim() : null;

    public static int? Int(this IQueryCollection q, string key) => int.TryParse(q.Str(key), out var i) ? i : null;

    public static long? Long(this IQueryCollection q, string key) => long.TryParse(q.Str(key), out var i) ? i : null;

    public static double? Double(this IQueryCollection q, string key) =>
        double.TryParse(q.Str(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static bool? Bool(this IQueryCollection q, string key) => q.Str(key)?.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    /// <summary>Comma/pipe separated values, also accepting repeated keys.</summary>
    public static string[] List(this IQueryCollection q, string key) =>
        q.TryGetValue(key, out var values)
            ? values.SelectMany(v => (v ?? "").Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray()
            : [];

    public static Guid? Guid(this IQueryCollection q, string key) => System.Guid.TryParse(q.Str(key), out var g) ? g : null;

    public static Guid[] Guids(this IQueryCollection q, string key) =>
        q.List(key).Select(s => System.Guid.TryParse(s, out var g) ? g : (Guid?)null).OfType<Guid>().ToArray();

    public static Guid? RouteGuid(this HttpContext ctx, string name) =>
        System.Guid.TryParse(ctx.Request.RouteValues[name]?.ToString(), out var g) ? g : null;

    /// <summary>Reads a JSON body leniently: empty or invalid bodies yield null instead of a 400.</summary>
    public static async Task<T?> ReadBodyAsync<T>(this HttpRequest req, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (req.ContentLength == 0) return null;
        try
        {
            return await JsonSerializer.DeserializeAsync(req.Body, type, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<string?> ReadRawBodyAsync(this HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var s = await reader.ReadToEndAsync(req.HttpContext.RequestAborted);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static IResult Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => Results.Json(value, type);

    public static AppJson J => AppJson.Default;
}
