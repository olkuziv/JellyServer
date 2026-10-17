using System.Text.Json.Nodes;
using JellyServer.Catalog.Contracts;
using JellyServer.Metadata.Contracts;

namespace JellyServer.Metadata;

public sealed record ProviderPick(IMetadataProvider Provider, bool UseMetadata, bool UseImages);

/// <param name="ImageLimits">How many images of each kind (Primary, Backdrop, Logo, Thumb, Banner) the library wants; 0 switches a kind off.</param>
public sealed record MetadataPlan(MetadataContext Context, IReadOnlyList<ProviderPick> Providers, IReadOnlyDictionary<string, int> ImageLimits)
{
    public int BackdropLimit => ImageLimits.GetValueOrDefault("Backdrop", 1);
}

/// <summary>Reads a library's options (Jellyfin's LibraryOptions JSON) and decides what runs for an item type.</summary>
public static class MetadataPlanner
{
    /// <summary>Jellyfin's default image options per item type (the dashboard offers and stores them).</summary>
    public static IReadOnlyDictionary<string, int> DefaultImageLimits(string itemType) => itemType switch
    {
        ItemTypes.Movie => new Dictionary<string, int> { ["Backdrop"] = 1, ["Art"] = 0, ["Disc"] = 0, ["Primary"] = 1, ["Banner"] = 0, ["Thumb"] = 1, ["Logo"] = 1 },
        ItemTypes.Series => new Dictionary<string, int> { ["Backdrop"] = 1, ["Art"] = 0, ["Disc"] = 0, ["Primary"] = 1, ["Banner"] = 1, ["Thumb"] = 1, ["Logo"] = 1 },
        ItemTypes.Season => new Dictionary<string, int> { ["Backdrop"] = 0, ["Primary"] = 1, ["Banner"] = 0, ["Thumb"] = 0 },
        ItemTypes.Episode => new Dictionary<string, int> { ["Backdrop"] = 0, ["Primary"] = 1 },
        _ => new Dictionary<string, int> { ["Backdrop"] = 1, ["Primary"] = 1 },
    };

    public static MetadataPlan Create(Library library, string itemType, IEnumerable<IMetadataProvider> providers, string serverLanguage, string serverCountry)
    {
        JsonObject? options = null;
        try { options = JsonNode.Parse(string.IsNullOrWhiteSpace(library.OptionsJson) ? "{}" : library.OptionsJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { /* unreadable options: defaults */ }

        var language = NonEmpty(options?["PreferredMetadataLanguage"]) ?? NonEmpty(serverLanguage) ?? "en";
        var country = NonEmpty(options?["MetadataCountryCode"]) ?? NonEmpty(serverCountry) ?? "US";

        var typeOptions = (options?["TypeOptions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(t => string.Equals((string?)t["Type"], itemType, StringComparison.OrdinalIgnoreCase));
        var metadataFetchers = Names(typeOptions?["MetadataFetchers"]);
        var metadataOrder = Names(typeOptions?["MetadataFetcherOrder"]);
        var imageFetchers = Names(typeOptions?["ImageFetchers"]);
        var imageOrder = Names(typeOptions?["ImageFetcherOrder"]);

        var limits = new Dictionary<string, int>(DefaultImageLimits(itemType), StringComparer.OrdinalIgnoreCase);
        if (typeOptions?["ImageOptions"] is JsonArray imageOptions)
            foreach (var o in imageOptions.OfType<JsonObject>())
                if ((string?)o["Type"] is { Length: > 0 } kind && o["Limit"] is { } limit && int.TryParse(limit.ToString(), out var n))
                    limits[kind] = Math.Max(0, n);

        var registered = providers.Where(p => p.IsAvailable && p.ItemTypes.Contains(itemType)).ToList();
        var picks = new List<(ProviderPick Pick, int Order)>();
        for (var i = 0; i < registered.Count; i++)
        {
            var p = registered[i];
            var useMetadata = metadataFetchers is null || metadataFetchers.Contains(p.Name, StringComparer.OrdinalIgnoreCase);
            var useImages = p.ImageTypes.Count > 0 && (imageFetchers is null || imageFetchers.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
            if (!useMetadata && !useImages) continue;
            var rank = IndexOf(metadataOrder, p.Name) ?? IndexOf(imageOrder, p.Name) ?? 1000 + i;
            picks.Add((new ProviderPick(p, useMetadata, useImages), rank));
        }
        return new MetadataPlan(new MetadataContext(language, country), picks.OrderBy(p => p.Order).Select(p => p.Pick).ToList(), limits);
    }

    private static string? NonEmpty(JsonNode? node) => NonEmpty(node?.GetValue<string>());

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string>? Names(JsonNode? node) => node is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() : null;

    private static int? IndexOf(List<string>? list, string name)
    {
        var i = list?.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? -1;
        return i >= 0 ? i : null;
    }
}