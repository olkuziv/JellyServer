using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>
/// Lays provider metadata over an item. Local data (NFO values, artwork files) wins; the provider fills what is empty. A value that is
/// merely the previous provider answer counts as empty, so a newer answer replaces it. A refresh with <c>replaceAll</c> lets the provider win.
/// </summary>
public static class RemoteMetadataOverlay
{
    /// <summary>Returns true when the item changed.</summary>
    /// <param name="previous">What was laid over the item before (null when the item was just rewritten by a scan).</param>
    public static bool Apply(Item item, RemoteMetadata? previous, RemoteMetadata next, bool replaceAll, bool? replaceImages = null)
    {
        var changed = false;
        var replacePictures = replaceImages ?? replaceAll;

        // Title: the provider's (localised) one beats a name guessed from the folder, unless an NFO gave the title.
        if (!item.NameLocked && NonEmpty(next.Name) is { } name && name != item.Name)
        {
            item.Name = name;
            if (item.Type is ItemTypes.Movie or ItemTypes.Series) item.SortName = ItemNaming.SortName(name);
            changed = true;
        }

        changed |= Text(item.OriginalTitle, previous?.OriginalTitle, next.OriginalTitle, replaceAll, v => item.OriginalTitle = v);
        changed |= Text(item.Overview, previous?.Overview, next.Overview, replaceAll, v => item.Overview = v);
        changed |= Text(item.Tagline, previous?.Tagline, next.Tagline, replaceAll, v => item.Tagline = v);
        changed |= Text(item.OfficialRating, previous?.OfficialRating, next.OfficialRating, replaceAll, v => item.OfficialRating = v);
        changed |= Text(item.CollectionName, previous?.CollectionName, next.CollectionName, replaceAll, v => item.CollectionName = v);
        changed |= Value(item.ProductionYear, previous?.ProductionYear, next.ProductionYear, replaceAll, v => item.ProductionYear = v);
        changed |= Value(item.PremiereDate, previous?.PremiereDate, next.PremiereDate, replaceAll, v => item.PremiereDate = v);
        changed |= Value(item.EndDate, previous?.EndDate, next.EndDate, replaceAll, v => item.EndDate = v);
        changed |= Value(item.CommunityRating, previous?.CommunityRating, next.CommunityRating, replaceAll, v => item.CommunityRating = v);
        changed |= Value(item.CriticRating, previous?.CriticRating, next.CriticRating, replaceAll, v => item.CriticRating = v);
        if (next.RuntimeTicks is > 0 && (replaceAll || item.RuntimeTicks == 0 || item.RuntimeTicks == previous?.RuntimeTicks) && item.RuntimeTicks != next.RuntimeTicks)
        {
            item.RuntimeTicks = next.RuntimeTicks.Value;
            changed = true;
        }
        changed |= Array(item.Genres, previous?.Genres, next.Genres, replaceAll, v => item.Genres = v);
        changed |= Array(item.Studios, previous?.Studios, next.Studios, replaceAll, v => item.Studios = v);

        // Ids: the provider adds what the item lacks; ids written by the user (name or NFO) win.
        var ids = new Dictionary<string, string>(next.ProviderIds);
        foreach (var (key, value) in item.ProviderIds)
        {
            var fromPrevious = previous is not null && previous.ProviderIds.TryGetValue(key, out var old) && old == value;
            if (replaceAll && ids.ContainsKey(key)) continue;
            if (!fromPrevious || !ids.ContainsKey(key)) ids[key] = value;
        }
        if (!ids.OrderBy(k => k.Key).SequenceEqual(item.ProviderIds.OrderBy(k => k.Key)))
        {
            item.ProviderIds = ids;
            changed = true;
        }

        if (next.People is { Count: > 0 } people
            && (replaceAll || item.People is not { Count: > 0 } || SamePeople(item.People, previous?.People))
            && !SamePeople(item.People, people))
        {
            item.People = people;
            changed = true;
        }

        changed |= Image(item.Images.Primary, previous?.Images.Primary, next.Images.Primary, replacePictures, v => item.Images.Primary = v);
        changed |= Image(item.Images.Logo, previous?.Images.Logo, next.Images.Logo, replacePictures, v => item.Images.Logo = v);
        changed |= Image(item.Images.Thumb, previous?.Images.Thumb, next.Images.Thumb, replacePictures, v => item.Images.Thumb = v);
        changed |= Image(item.Images.Banner, previous?.Images.Banner, next.Images.Banner, replacePictures, v => item.Images.Banner = v);
        if (next.Images.Backdrops.Count > 0
            && (replacePictures || item.Images.Backdrops.Count == 0 || item.Images.Backdrops.SequenceEqual(previous?.Images.Backdrops ?? []))
            && !item.Images.Backdrops.SequenceEqual(next.Images.Backdrops))
        {
            item.Images.Backdrops = [.. next.Images.Backdrops];
            changed = true;
        }

        if (changed) item.SearchText = ItemNaming.SearchText(item.Name, item.OriginalTitle);
        return changed;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool Text(string? current, string? previous, string? remote, bool replaceAll, Action<string> set)
    {
        if (NonEmpty(remote) is not { } r || r == current) return false;
        if (!replaceAll && !string.IsNullOrWhiteSpace(current) && current != previous) return false;
        set(r);
        return true;
    }

    private static bool Image(string? current, string? previous, string? remote, bool replaceAll, Action<string> set) => Text(current, previous, remote, replaceAll, set);

    private static bool Value<T>(T? current, T? previous, T? remote, bool replaceAll, Action<T?> set) where T : struct
    {
        if (remote is null || EqualityComparer<T?>.Default.Equals(remote, current)) return false;
        if (!replaceAll && current is not null && !EqualityComparer<T?>.Default.Equals(current, previous)) return false;
        set(remote);
        return true;
    }

    private static bool Array(string[] current, string[]? previous, string[] remote, bool replaceAll, Action<string[]> set)
    {
        if (remote.Length == 0 || current.SequenceEqual(remote)) return false;
        if (!replaceAll && current.Length > 0 && !current.SequenceEqual(previous ?? [])) return false;
        set(remote);
        return true;
    }

    private static bool SamePeople(List<Person>? a, List<Person>? b) =>
        (a ?? []).Select(p => (p.Name, p.Role, p.Type)).SequenceEqual((b ?? []).Select(p => (p.Name, p.Role, p.Type)));
}