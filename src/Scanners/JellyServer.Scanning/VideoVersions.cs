using System.Text.RegularExpressions;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Scanning;

/// <summary>
/// Files that are one movie or episode in several versions (resolutions, voice-overs, ...), found the way Jellyfin finds them:
/// a movie folder's files all start with the folder's name; episodes share a season and episode number.
/// </summary>
public static partial class VideoVersions
{
    [GeneratedRegex("[0-9]{2}[0-9]+[ip]", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"^\[([^]]*)\]")]
    private static partial Regex LeadingBrackets();

    /// <summary>
    /// Groups the videos of a movie folder: when every file is "FolderName" followed by nothing, "-", "_", "." or "[...]", they are
    /// versions of one movie, otherwise each file is a movie of its own. The first file of a group is the main one.
    /// </summary>
    public static List<List<FileEntry>> ForMovieFolder(string folderName, IReadOnlyList<FileEntry> videos)
    {
        var alone = () => videos.Select(v => new List<FileEntry> { v }).ToList();
        if (videos.Count < 2 || folderName.Length <= 1) return alone();
        if (!videos.All(v => IsVersionOf(folderName, Path.GetFileNameWithoutExtension(v.Name)))) return alone();
        return [Order(videos, v => Path.GetFileNameWithoutExtension(v.Name), v => Path.GetFileNameWithoutExtension(v.Name) == folderName)];
    }

    /// <summary>Groups items that name the same episode (key null = not an episode we can place: stays alone). Keeps first-seen order.</summary>
    public static List<List<T>> ByKey<T>(IEnumerable<T> items, Func<T, string?> key, Func<T, string> baseName)
    {
        var groups = new List<List<T>>();
        var byKey = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (key(item) is not { } k)
            {
                groups.Add([item]);
            }
            else if (byKey.TryGetValue(k, out var group))
            {
                group.Add(item);
            }
            else
            {
                byKey[k] = [item];
                groups.Add(byKey[k]);
            }
        }
        foreach (var group in groups.Where(g => g.Count > 1))
        {
            var ordered = Order(group, baseName);
            group.Clear();
            group.AddRange(ordered);
        }
        return groups;
    }

    /// <summary>The version record of one more file: its own id (what clients call the MediaSourceId), file, container and sidecar subtitles.</summary>
    public static ItemVersion CreateVersion(ScanContext ctx, string kind, DirListing dir, FileEntry file, Fingerprint fp)
    {
        var full = dir.Full(file.Name);
        fp.Add("alt", file);
        var version = new ItemVersion
        {
            Id = ctx.RelId(kind, full), Path = full, Size = file.Size,
            Subtitles = dir.Subtitles(fp, Path.GetFileNameWithoutExtension(file.Name)),
        };
        if (file.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            version.StreamUrl = ImageRefs.ReadStrm(full);
            version.Size = 0;
        }
        version.Container = Naming.ContainerFor(full, version.StreamUrl);
        return version;
    }

    public static bool IsVersionOf(string folderName, string baseName)
    {
        if (!baseName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = baseName[folderName.Length..].Trim();
        return rest.Length == 0 || rest[0] is '-' or '_' or '.' || LeadingBrackets().IsMatch(rest);
    }

    /// <summary>Files with a resolution in their name come first (highest first), then the others by name; <paramref name="isMain"/> overrides the choice.</summary>
    public static List<T> Order<T>(IEnumerable<T> files, Func<T, string> baseName, Func<T, bool>? isMain = null)
    {
        var list = files.Select(f => (File: f, Name: baseName(f), Resolution: ResolutionRegex().Match(baseName(f)))).ToList();
        var withResolution = list.Where(x => x.Resolution.Success)
            .OrderByDescending(x => ResolutionNumber(x.Resolution.Value)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var others = list.Where(x => !x.Resolution.Success).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = withResolution.Concat(others).Select(x => x.File).ToList();
        if (isMain is not null && ordered.FindIndex(f => isMain(f)) is var i and > 0)
        {
            var main = ordered[i];
            ordered.RemoveAt(i);
            ordered.Insert(0, main);
        }
        return ordered;
    }

    private static int ResolutionNumber(string value) => int.TryParse(value[..^1], out var n) ? n : 0;
}