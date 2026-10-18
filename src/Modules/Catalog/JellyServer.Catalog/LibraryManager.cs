using JellyServer.Tasks.Contracts;
using System.Text.Json;
using JellyServer.Kernel.Events;
using JellyServer.Kernel.Caching;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>Published after a library and all of its items were deleted; other modules drop what they keep per item.</summary>


public enum LibraryStatus { Ok, NotFound, Conflict, Invalid }

public readonly record struct LibraryResult(LibraryStatus Status, string? Message = null)
{
    public static readonly LibraryResult Ok = new(LibraryStatus.Ok);
    public static LibraryResult NotFound(string message) => new(LibraryStatus.NotFound, message);
    public static LibraryResult Invalid(string message) => new(LibraryStatus.Invalid, message);
}

/// <summary>
/// Library administration with Jellyfin's semantics (Library/VirtualFolders): libraries live in the database and are
/// managed from the dashboard or the startup wizard; nothing is configured through files or environment variables.
/// </summary>
public sealed class LibraryManager(LibraryStore store, LibraryRegistry registry, RedisCache cache, ITaskManager tasks, IEventBus events,
    ILogger<LibraryManager> log)
{
    /// <summary>Content types the scanner understands. Jellyfin has more (music, books, …): those are rejected, not faked.</summary>

    // Library changes are rare and admin-driven; one at a time keeps the registry and the database consistent.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LibraryResult> AddAsync(string? name, string? collectionType, IReadOnlyList<string> paths, JsonElement? options,
        bool refresh, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return Rejected("Library name cannot be empty");
        if (LibraryTypes.Normalize(collectionType) is not { } type)
            return Rejected($"Content type '{collectionType}' is not supported yet (movies, tvshows and mixed are)");

        var all = paths.Concat(options is { } o ? LibraryOptionsJson.PathsOf(o) : []).Select(p => p.Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (all.FirstOrDefault(p => !Directory.Exists(p)) is { } missing)
            return Rejected($"The specified path does not exist: {missing}.");

        await _gate.WaitAsync(ct);
        try
        {
            // Jellyfin never refuses a duplicate name: it numbers it ("Movies2").
            var unique = name.Trim();
            for (var n = 2; registry.All.Any(l => l.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); n++)
                unique = name.Trim() + n;

            var lib = await store.CreateLibraryAsync(unique, type, all, LibraryOptionsJson.Create(options), ct);
            log.LogInformation("Library {Name} ({Type}) added with {Count} paths", lib.Name, lib.CollectionType, all.Length);
            await ChangedAsync(refresh, ct);
        }
        finally
        {
            _gate.Release();
        }
        return LibraryResult.Ok;
    }

    private LibraryResult Rejected(string reason)
    {
        log.LogWarning("Library not added: {Reason}", reason);
        return LibraryResult.Invalid(reason);
    }

    public async Task<LibraryResult> RemoveAsync(string name, bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Find(name) is not { } lib) return LibraryResult.NotFound("The media folder does not exist");
            var items = await store.DeleteLibraryAsync(lib.Id, ct);
            await cache.DeleteAsync(items.Select(id => "item:" + id.N()));
            await events.PublishAsync(new LibraryRemoved(lib.Id, items), ct);
            log.LogInformation("Library {Name} removed with {Count} items", lib.Name, items.Count);
            await ChangedAsync(false, ct);
        }
        finally
        {
            _gate.Release();
        }
        return LibraryResult.Ok;
    }

    public async Task<LibraryResult> RenameAsync(string name, string newName, bool refresh, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(newName)) return LibraryResult.Invalid("name and newName are required");
        await _gate.WaitAsync(ct);
        try
        {
            if (Find(name) is not { } lib) return LibraryResult.NotFound("The media collection does not exist.");
            newName = newName.Trim();
            if (!lib.Name.Equals(newName, StringComparison.OrdinalIgnoreCase) && Find(newName) is not null)
                return new LibraryResult(LibraryStatus.Conflict, $"The media library already exists at {newName}.");
            if (!await store.RenameLibraryAsync(lib.Id, newName, ct)) return new LibraryResult(LibraryStatus.Conflict, $"The media library already exists at {newName}.");
            await ChangedAsync(refresh, ct);
        }
        finally
        {
            _gate.Release();
        }
        return LibraryResult.Ok;
    }

    public async Task<LibraryResult> AddPathAsync(string name, string? path, bool refresh, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return LibraryResult.Invalid("PathInfo and Path can't both be null.");
        path = path.Trim();
        if (!Directory.Exists(path)) return LibraryResult.NotFound("The path does not exist.");
        return await EditPathsAsync(name, paths => paths.Contains(path, StringComparer.Ordinal) ? paths : [.. paths, path], refresh, ct);
    }

    public async Task<LibraryResult> RemovePathAsync(string name, string path, bool refresh, CancellationToken ct) =>
        await EditPathsAsync(name, paths => paths.Where(p => !string.Equals(p, path, StringComparison.Ordinal)).ToArray(), refresh, ct);

    /// <summary>Jellyfin's UpdateMediaPath only re-saves the options (paths carry nothing else any more).</summary>
    public Task<LibraryResult> UpdatePathAsync(string name) =>
        Task.FromResult(Find(name) is null ? LibraryResult.NotFound($"The media collection {name} does not exist") : LibraryResult.Ok);

    public async Task<LibraryResult> UpdateOptionsAsync(Guid id, JsonElement options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (registry.Find(id) is not { } lib) return LibraryResult.NotFound("Library not found");
            // New folders listed in the options are added like the dashboard's "add folder" does.
            var added = LibraryOptionsJson.PathsOf(options).Where(p => !lib.Paths.Contains(p, StringComparer.Ordinal)).ToArray();
            if (added.FirstOrDefault(p => !Directory.Exists(p)) is { } missing) return LibraryResult.Invalid($"The specified path does not exist: {missing}.");
            var languageChanged = MetadataLocale(lib.OptionsJson) != MetadataLocale(options);
            await store.SetOptionsAsync(id, LibraryOptionsJson.ForUpdate(options), ct);
            if (added.Length > 0) await store.SetPathsAsync(id, [.. lib.Paths, .. added], ct);
            await ChangedAsync(false, ct);
            await events.PublishAsync(new LibraryOptionsChanged(id, languageChanged), ct);
        }
        finally
        {
            _gate.Release();
        }
        return LibraryResult.Ok;
    }

    private static (string, string) MetadataLocale(string optionsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson);
            return MetadataLocale(doc.RootElement);
        }
        catch (JsonException) { return ("", ""); }
    }

    /// <summary>Language and country the library asks providers for (the dashboard's "Preferred download language" and "Country").</summary>
    private static (string, string) MetadataLocale(JsonElement options)
    {
        static string Text(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        return (Text(options, "PreferredMetadataLanguage"), Text(options, "MetadataCountryCode"));
    }

    private async Task<LibraryResult> EditPathsAsync(string name, Func<string[], string[]> edit, bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Find(name) is not { } lib) return LibraryResult.NotFound($"The media collection {name} does not exist");
            await store.SetPathsAsync(lib.Id, edit(lib.Paths), ct);
            await ChangedAsync(refresh, ct);
        }
        finally
        {
            _gate.Release();
        }
        return LibraryResult.Ok;
    }

    private Library? Find(string name) => registry.All.FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Reloads the in-memory list and drops cached query results; optionally scans right away.</summary>
    private async Task ChangedAsync(bool scan, CancellationToken ct)
    {
        await registry.ReloadAsync(ct);
        await cache.BumpGenerationAsync();
        if (scan) tasks.Start(ScanLibraryTask.TaskId);
    }
}
