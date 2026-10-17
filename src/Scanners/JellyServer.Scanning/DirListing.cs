using System.Security.Cryptography;
using System.Text;
using System.IO.Enumeration;
using System.Threading.Channels;
using JellyServer.Catalog.Contracts;
using JellyServer.Media.Contracts;

namespace JellyServer.Scanning;

public sealed record FileEntry(string Name, long Size, long MTime);

/// <summary>
/// One directory read with case-insensitive lookup. Each file is stat'ed exactly
/// once per scan, which dominates scan cost on network shares.
/// </summary>
public sealed class DirListing
{
    public string Path { get; }
    public List<FileEntry> Files { get; } = [];
    public List<string> Dirs { get; } = [];
    private readonly Dictionary<string, FileEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    private DirListing(string path) => Path = path;

    private static readonly EnumerationOptions EnumOptions = new()
    {
        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false,
    };

    private readonly record struct RawEntry(string Name, bool IsDir, bool IsLink, long Size, long MTime);

    public static DirListing Read(string path)
    {
        var listing = new DirListing(path);
        // FileSystemEnumerable reads type/size/mtime straight from the directory entry:
        // no FileInfo allocations and no extra syscalls per file (matters on network mounts).
        var entries = new FileSystemEnumerable<RawEntry>(path, (ref FileSystemEntry e) => new RawEntry(
            e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0,
            e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.ToUnixTimeSeconds()), EnumOptions);
        foreach (var raw in entries)
        {
            var e = raw;
            if (e.IsLink && !e.IsDir)
            {
                // Follow symlinked files/dirs (rare): resolve once.
                var target = new FileInfo(System.IO.Path.Combine(path, e.Name)).ResolveLinkTarget(true);
                if (target is DirectoryInfo) { listing.Dirs.Add(e.Name); continue; }
                if (target is FileInfo { Exists: true } fi)
                    e = e with { Size = fi.Length, MTime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() };
                else continue;
            }
            if (e.IsDir)
            {
                listing.Dirs.Add(e.Name);
                continue;
            }
            var entry = new FileEntry(e.Name, e.Size, e.MTime);
            listing.Files.Add(entry);
            listing._byName[e.Name] = entry;
        }
        listing.Files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        listing.Dirs.Sort(StringComparer.Ordinal);
        return listing;
    }

    public FileEntry? File(string name) => _byName.GetValueOrDefault(name);

    public string Full(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Main video files and .strm files, excluding samples/trailers.</summary>
    public List<FileEntry> Videos() => Files
        .Where(f => Naming.VideoExtensions.ContainsKey(System.IO.Path.GetExtension(f.Name))
                    && !Naming.IsSampleFile(System.IO.Path.GetFileNameWithoutExtension(f.Name)))
        .ToList();

    public IEnumerable<string> SubDirs() =>
        Dirs.Where(d => !Naming.IsIgnoredDir(d)).Select(d => System.IO.Path.Combine(Path, d));

    /// <summary>First existing image among the candidate base names.</summary>
    public string? FindImage(Fingerprint fp, string tag, params string[] baseNames)
    {
        foreach (var b in baseNames)
        {
            foreach (var ext in Naming.ImageExtensions)
            {
                if (File(b + ext) is { } e)
                {
                    fp.Add(tag, e);
                    return Full(e.Name);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Artwork for one media file. Folder-level names (folder.jpg, fanart.jpg…) apply
    /// only when the folder belongs to a single item.
    /// </summary>
    public ItemImages ItemImages(Fingerprint fp, string? baseName, bool folderLevel)
    {
        var b = baseName;
        List<string> prim = [], back = [], logo = [], thumb = [], banner = [];
        if (b is not null)
        {
            prim.AddRange([b + "-poster", b, b + "-cover"]);
            back.AddRange([b + "-fanart", b + "-backdrop"]);
            logo.AddRange([b + "-clearlogo", b + "-logo"]);
            thumb.AddRange([b + "-landscape", b + "-thumb"]);
            banner.Add(b + "-banner");
        }
        if (folderLevel)
        {
            prim.AddRange(["poster", "folder", "cover", "default", "movie", "show"]);
            back.AddRange(["fanart", "backdrop", "background", "art"]);
            logo.AddRange(["clearlogo", "logo"]);
            thumb.AddRange(["landscape", "thumb"]);
            banner.Add("banner");
        }
        var images = new ItemImages
        {
            Primary = FindImage(fp, "p", [.. prim]),
            Logo = FindImage(fp, "l", [.. logo]),
            Thumb = FindImage(fp, "t", [.. thumb]),
            Banner = FindImage(fp, "bn", [.. banner]),
        };
        if (FindImage(fp, "b", [.. back]) is { } bd) images.Backdrops.Add(bd);
        if (folderLevel)
        {
            foreach (var extra in new[] { "backdrop1", "fanart1", "backdrop2", "fanart2", "backdrop3", "fanart3" })
            {
                if (images.Backdrops.Count < 4 && FindImage(fp, "b+", extra) is { } more) images.Backdrops.Add(more);
            }
        }
        return images;
    }

    public ItemImages EpisodeImages(Fingerprint fp, string baseName) =>
        new() { Primary = FindImage(fp, "p", baseName + "-thumb", baseName) };

    private static readonly Dictionary<string, string> SubtitleLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = "ukr", ["ukr"] = "ukr", ["ua"] = "ukr", ["en"] = "eng", ["eng"] = "eng", ["ru"] = "rus",
        ["rus"] = "rus", ["de"] = "ger", ["ger"] = "ger", ["deu"] = "ger", ["fr"] = "fre", ["fre"] = "fre",
        ["fra"] = "fre", ["es"] = "spa", ["spa"] = "spa", ["it"] = "ita", ["ita"] = "ita", ["pl"] = "pol",
        ["pol"] = "pol", ["ja"] = "jpn", ["jpn"] = "jpn", ["ukrainian"] = "ukr", ["english"] = "eng",
        ["russian"] = "rus",
    };

    /// <summary>Sidecar subtitles: "&lt;base&gt;.srt", "&lt;base&gt;.uk.forced.srt" …</summary>
    public List<SubtitleFile>? Subtitles(Fingerprint fp, string baseName)
    {
        List<SubtitleFile>? subs = null;
        foreach (var f in Files)
        {
            if (!Naming.SubtitleExtensions.TryGetValue(System.IO.Path.GetExtension(f.Name), out var format)) continue;
            var stem = System.IO.Path.GetFileNameWithoutExtension(f.Name);
            if (!stem.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                && !stem.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) continue;
            fp.Add("sub", f);
            string? lang = null;
            var forced = false;
            foreach (var token in stem[baseName.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Equals("forced", StringComparison.OrdinalIgnoreCase)) forced = true;
                else if (SubtitleLanguages.TryGetValue(token, out var l)) lang = l;
            }
            (subs ??= []).Add(new SubtitleFile(Full(f.Name), format, lang, forced));
        }
        return subs;
    }
}

/// <summary>
/// Accumulates every input that contributed to an item (name+size+mtime). Equal
/// fingerprints mean the item can be skipped without opening any file.
/// </summary>
public sealed class Fingerprint
{
    private readonly StringBuilder _sb = new();

    public void Add(string tag, FileEntry? e)
    {
        _sb.Append(tag).Append('=');
        if (e is not null) _sb.Append(e.Name).Append(':').Append(e.Size).Append(':').Append(e.MTime);
        _sb.Append(';');
    }

    public void Add(string tag, string value) => _sb.Append(tag).Append('=').Append(value).Append(';');

    public override string ToString() => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(_sb.ToString())));
}

public static class ParallelWalker
{
    /// <summary>
    /// Processes directories with <paramref name="workers"/> concurrent workers. <paramref name="process"/>
    /// returns subdirectories to enqueue; the queue is unbounded so workers never block each other.
    /// </summary>
    public static async Task WalkAsync(IEnumerable<string> roots, int workers, Func<string, IEnumerable<string>> process, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>();
        long pending = 0;
        foreach (var r in roots)
        {
            Interlocked.Increment(ref pending);
            channel.Writer.TryWrite(r);
        }
        if (Interlocked.Read(ref pending) == 0) return;

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var dir in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                try
                {
                    if (!ct.IsCancellationRequested)
                    {
                        foreach (var sub in process(dir))
                        {
                            Interlocked.Increment(ref pending);
                            channel.Writer.TryWrite(sub);
                        }
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0) channel.Writer.TryComplete();
                }
            }
        }, CancellationToken.None));
        await Task.WhenAll(tasks);
    }
}

public static class ImageRefs
{
    /// <summary>Remote URLs are kept; local paths only when the file exists in this container.</summary>
    public static string? Usable(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return reference;
        return System.IO.Path.IsPathRooted(reference) && System.IO.File.Exists(reference) ? reference : null;
    }

    /// <summary>Fills gaps in locally found artwork with NFO references.</summary>
    public static void MergeFrom(ItemImages images, Nfo? nfo)
    {
        if (nfo is null) return;
        var (poster, fanart, landscape, logo) = nfo.ImageRefs();
        images.Primary ??= Usable(poster);
        if (images.Backdrops.Count == 0 && Usable(fanart) is { } f) images.Backdrops.Add(f);
        images.Thumb ??= Usable(landscape);
        images.Logo ??= Usable(logo);
    }

    public static string? ReadStrm(string path)
    {
        try
        {
            foreach (var raw in System.IO.File.ReadLines(path))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length > 0 && !line.StartsWith('#')) return line;
            }
        }
        catch (IOException) { }
        return null;
    }
}
