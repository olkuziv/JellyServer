using System.Collections.Concurrent;
using System.Threading.Channels;
using JellyServer.Kernel;

namespace JellyServer.Catalog.Contracts;

/// <summary>
/// Turns the files of one kind of library (movies, TV shows, ...) into catalog items. A scanner only reads the file
/// system and emits <see cref="Item"/>s through the <see cref="ScanContext"/>; it never touches a database or cache:
/// the catalog persists what scanners emit, removes what disappeared and invalidates caches.
/// </summary>
public interface ILibraryScanner
{
    /// <summary>The library collection types this scanner handles (see <see cref="LibraryTypes"/>).</summary>
    IReadOnlyCollection<string> CollectionTypes { get; }

    /// <summary>Scans one root folder of the library; roots are scanned one after another.</summary>
    Task ScanRootAsync(ScanContext context, string root, CancellationToken ct);
}

/// <summary>State of one library scan, shared by all walker threads of its scanner.</summary>
public sealed class ScanContext(Library library, IReadOnlyDictionary<Guid, string> existing, ChannelWriter<Item> output, int workers, ILogger log)
{
    private long _dirs, _files, _unchanged;

    public Library Library { get; } = library;

    /// <summary>How many folders may be read in parallel.</summary>
    public int Workers { get; } = workers;

    public ILogger Log { get; } = log;

    /// <summary>The root being scanned, as a full path.</summary>
    public string Root { get; private set; } = "";

    private string _rootTag = "";

    /// <summary>Ids of every item still on disk: whatever is missing afterwards is deleted.</summary>
    public ConcurrentDictionary<Guid, byte> Seen { get; } = new();

    /// <summary>A root was unreadable or empty: nothing may be deleted after this scan.</summary>
    public bool RootError { get; private set; }

    public long Dirs => Interlocked.Read(ref _dirs);
    public long FilesSeen => Interlocked.Read(ref _files);
    public long Unchanged => Interlocked.Read(ref _unchanged);

    public void SetRoot(string root)
    {
        Root = Path.GetFullPath(root);
        _rootTag = Path.GetFileName(Path.TrimEndingDirectorySeparator(Root));
    }

    public void MarkRootError() => RootError = true;

    public void CountDirectory(int fileCount)
    {
        Interlocked.Increment(ref _dirs);
        Interlocked.Add(ref _files, fileCount);
    }

    /// <summary>Marks the item as seen; true when its fingerprint matches the database (nothing to re-read).</summary>
    public bool IsUnchanged(Guid id, string fingerprint)
    {
        Seen[id] = 0;
        if (existing.TryGetValue(id, out var old) && old == fingerprint)
        {
            Interlocked.Increment(ref _unchanged);
            return true;
        }
        return false;
    }

    /// <summary>Queues an item for saving; blocks on the bounded channel for back-pressure (walker threads are synchronous).</summary>
    public void Emit(Item item, string fingerprint, CancellationToken ct)
    {
        item.LibraryId = Library.Id;
        item.Fingerprint = fingerprint;
        item.DateModified = DateTime.UtcNow;
        if (string.IsNullOrEmpty(item.SortName)) item.SortName = ItemNaming.SortName(item.Name);
        item.SearchText = ItemNaming.SearchText(item.Name, item.OriginalTitle);
        if (!output.TryWrite(item)) output.WriteAsync(item, ct).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Stable id from the path relative to the library root (survives remounts).</summary>
    public Guid RelId(string kind, string path, params string[] extra)
    {
        var rel = Path.GetRelativePath(Root, path).Replace('\\', '/');
        return Ids.FromKey([Library.Id.N(), kind, _rootTag, rel, .. extra]);
    }
}