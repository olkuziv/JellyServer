using System.Text.Json;
using System.Text.Json.Nodes;

namespace JellyServer.Catalog;

/// <summary>
/// Jellyfin's <c>LibraryOptions</c> as the dashboard sends and expects it. The document is stored as sent and returned
/// whole, so every setting round-trips; <c>PathInfos</c> is not stored but derived from the library's paths.
/// </summary>
public static class LibraryOptionsJson
{
    /// <summary>The defaults of Jellyfin's LibraryOptions constructor.</summary>
    private const string Defaults = """
        {"Enabled":true,"EnablePhotos":true,"EnableRealtimeMonitor":false,"EnableLUFSScan":false,
         "EnableChapterImageExtraction":false,"ExtractChapterImagesDuringLibraryScan":false,
         "EnableTrickplayImageExtraction":false,"ExtractTrickplayImagesDuringLibraryScan":false,"PathInfos":[],
         "SaveLocalMetadata":false,"EnableInternetProviders":false,"EnableAutomaticSeriesGrouping":true,
         "EnableEmbeddedTitles":false,"EnableEmbeddedExtrasTitles":false,"EnableEmbeddedEpisodeInfos":false,
         "AutomaticRefreshIntervalDays":0,"SeasonZeroDisplayName":"Specials","DisabledLocalMetadataReaders":[],
         "DisabledSubtitleFetchers":[],"SubtitleFetcherOrder":[],"DisabledMediaSegmentProviders":[],
         "MediaSegmentProviderOrder":[],"SkipSubtitlesIfEmbeddedSubtitlesPresent":false,
         "SkipSubtitlesIfAudioTrackMatches":true,"RequirePerfectSubtitleMatch":true,"SaveSubtitlesWithMedia":true,
         "SaveLyricsWithMedia":false,"SaveTrickplayWithMedia":false,"DisabledLyricFetchers":[],"LyricFetcherOrder":[],
         "PreferNonstandardArtistsTag":false,"UseCustomTagDelimiters":false,"CustomTagDelimiters":["/","|",";","\\"],
         "DelimiterWhitelist":[],"AutomaticallyAddToCollection":false,"AllowEmbeddedSubtitles":"AllowAll","TypeOptions":[]}
        """;

    /// <summary>Defaults overlaid with what the client sent (top-level properties replace the defaults).</summary>
    public static string Create(JsonElement? sent)
    {
        var doc = JsonNode.Parse(Defaults)!.AsObject();
        if (sent is { ValueKind: JsonValueKind.Object } s)
            foreach (var p in s.EnumerateObject())
                if (!p.NameEquals("PathInfos")) doc[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        doc.Remove("PathInfos");
        return doc.ToJsonString();
    }

    /// <summary>The stored options with <c>PathInfos</c> built from the library's paths.</summary>
    public static JsonObject WithPaths(string stored, IEnumerable<string> paths)
    {
        var doc = string.IsNullOrEmpty(stored) || stored == "{}" ? JsonNode.Parse(Defaults)!.AsObject() : JsonNode.Parse(stored)!.AsObject();
        var infos = new JsonArray();
        foreach (var p in paths) infos.Add(new JsonObject { ["Path"] = p });
        doc["PathInfos"] = infos;
        return doc;
    }

    /// <summary>Options for an update: the client's document without <c>PathInfos</c> (paths have their own endpoints).</summary>
    public static string ForUpdate(JsonElement sent)
    {
        var doc = JsonNode.Parse(sent.GetRawText())!.AsObject();
        doc.Remove("PathInfos");
        return doc.ToJsonString();
    }

    /// <summary>Paths from a client's <c>PathInfos</c> array (used when an update also lists new folders).</summary>
    public static List<string> PathsOf(JsonElement options)
    {
        var list = new List<string>();
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("PathInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            foreach (var i in infos.EnumerateArray())
                if (i.TryGetProperty("Path", out var p) && p.GetString() is { Length: > 0 } path) list.Add(path);
        return list;
    }
}
