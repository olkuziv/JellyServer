using System.Text.Json;
using System.Text.Json.Nodes;
using JellyServer.Settings.Contracts;

namespace JellyServer.Settings;

/// <summary>
/// Jellyfin's ServerConfiguration document (the <c>system</c> named configuration): server name, UI culture,
/// metadata defaults and the "startup wizard completed" flag. Reads are cached for a moment; a completed wizard
/// never reverts, so that answer is cached for good.
/// </summary>
public sealed class ServerConfiguration(INamedConfigurationStore store) : IServerConfiguration
{
    public const string Key = "system";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    private volatile Snapshot? _snapshot;
    private volatile bool _completed;

    private sealed record Snapshot(JsonObject Doc, DateTime LoadedAt);

    /// <summary>The stored document (a clone: callers may edit it freely).</summary>
    public async Task<JsonObject> GetAsync(CancellationToken ct = default)
    {
        var snap = _snapshot;
        if (snap is null || DateTime.UtcNow - snap.LoadedAt > Ttl)
        {
            var json = await store.GetAsync(Key, ct);
            var doc = json is not null && JsonNode.Parse(json) is JsonObject o ? o : new JsonObject();
            _snapshot = snap = new Snapshot(doc, DateTime.UtcNow);
        }
        return (JsonObject)snap.Doc.DeepClone();
    }

    public async Task<bool> IsStartupCompletedAsync(CancellationToken ct = default)
    {
        if (_completed) return true;
        var doc = await GetAsync(ct);
        if (doc["IsStartupWizardCompleted"] is { } flag && flag.GetValueKind() == JsonValueKind.True) _completed = true;
        return _completed;
    }

    /// <summary>Applies an edit to the document and saves it.</summary>
    public async Task UpdateAsync(Action<JsonObject> edit, CancellationToken ct = default)
    {
        _snapshot = null; // re-read: another replica may have saved since
        var doc = await GetAsync(ct);
        edit(doc);
        await SaveAsync(doc, ct);
    }

    /// <summary>Replaces the whole document (POST /System/Configuration).</summary>
    public async Task ReplaceAsync(JsonElement body, CancellationToken ct = default) =>
        await SaveAsync(JsonNode.Parse(body.GetRawText())!.AsObject(), ct);

    private async Task SaveAsync(JsonObject doc, CancellationToken ct)
    {
        await store.SaveAsync(Key, doc.ToJsonString(), ct);
        _snapshot = new Snapshot((JsonObject)doc.DeepClone(), DateTime.UtcNow);
        _completed = doc["IsStartupWizardCompleted"] is { } flag && flag.GetValueKind() == JsonValueKind.True;
    }

    public async Task CompleteStartupAsync(CancellationToken ct = default) =>
        await UpdateAsync(d => d["IsStartupWizardCompleted"] = true, ct);

    public async Task<string?> ServerNameAsync(CancellationToken ct = default) =>
        (await GetAsync(ct))["ServerName"] is { } n && n.GetValueKind() == JsonValueKind.String && n.GetValue<string>() is { Length: > 0 } s ? s : null;
}
