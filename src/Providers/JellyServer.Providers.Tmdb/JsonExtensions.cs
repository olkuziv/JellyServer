using System.Globalization;
using System.Text.Json;

namespace JellyServer.Providers.Tmdb;

/// <summary>Null-tolerant readers for TMDB's JSON (fields are often missing, null or empty).</summary>
internal static class JsonExtensions
{
    public static JsonElement? Get(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    public static int? Int(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;

    public static double? Dbl(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) ? n : null;

    /// <summary>Numeric ids as text ("603"); also accepts ids already sent as strings.</summary>
    public static string? IdText(this JsonElement e, string name) => e.Get(name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
        { ValueKind: JsonValueKind.String } v when !string.IsNullOrWhiteSpace(v.GetString()) => v.GetString()!.Trim(),
        _ => null,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    public static DateTime? Date(this JsonElement e, string name) =>
        DateTime.TryParse(e.Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d.Date : null;
}