using JellyServer.Catalog.Contracts;
using JellyServer.Media.Contracts;
using JellyServer.People.Contracts;

namespace JellyServer.Catalog;

/// <summary>
/// An item is kept in three modules: the catalog row (what lists need), its files (Media) and its credits (People).
/// This is where it is taken apart for writing and put together for reading.
/// </summary>
public static class ItemParts
{
    /// <summary>The files of an item: the main one (same id as the item) and its versions. Folders have none.</summary>
    public static List<MediaSource> Sources(Item it)
    {
        if (it.IsFolder) return [];
        var list = new List<MediaSource>(1 + (it.Versions?.Count ?? 0))
        {
            new()
            {
                Id = it.Id, ItemId = it.Id, Ordinal = 0, Path = it.Path, StreamUrl = it.StreamUrl, Container = it.Container, Size = it.Size,
                RuntimeTicks = it.RuntimeTicks, MediaInfo = it.MediaInfo, Subtitles = it.Subtitles, Fingerprint = it.Fingerprint,
            },
        };
        var ordinal = 1;
        foreach (var v in it.Versions ?? [])
        {
            list.Add(new MediaSource
            {
                Id = v.Id, ItemId = it.Id, Ordinal = ordinal++, Path = v.Path, StreamUrl = v.StreamUrl, Container = v.Container, Size = v.Size,
                RuntimeTicks = v.RuntimeTicks, MediaInfo = v.MediaInfo, Subtitles = v.Subtitles, Fingerprint = v.Fingerprint,
            });
        }
        return list;
    }

    /// <summary>Puts the files and credits back on catalog rows.</summary>
    public static void Attach(IEnumerable<Item> items, Dictionary<Guid, List<MediaSource>> sources, Dictionary<Guid, List<Person>> credits)
    {
        foreach (var it in items)
        {
            it.People = credits.GetValueOrDefault(it.Id);
            if (!sources.TryGetValue(it.Id, out var files)) continue;
            foreach (var f in files)
            {
                if (f.Ordinal == 0)
                {
                    it.StreamUrl = f.StreamUrl;
                    it.Container = f.Container;
                    it.MediaInfo = f.MediaInfo;
                    it.Subtitles = f.Subtitles;
                    continue;
                }
                (it.Versions ??= []).Add(new ItemVersion
                {
                    Id = f.Id, Path = f.Path, StreamUrl = f.StreamUrl, Container = f.Container, Size = f.Size, RuntimeTicks = f.RuntimeTicks,
                    MediaInfo = f.MediaInfo, Subtitles = f.Subtitles, Fingerprint = f.Fingerprint,
                });
            }
        }
    }
}
