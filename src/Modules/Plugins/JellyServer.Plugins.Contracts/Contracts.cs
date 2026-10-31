using JellyServer.Tasks.Contracts;

namespace JellyServer.Plugins.Contracts;

/// <summary>
/// The entry point of a plugin: a public class with a parameterless constructor in the plugin's main assembly
/// (<c>plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c> or a single <c>plugins/&lt;Name&gt;.dll</c>). These plugins are JellyServer's own
/// and are not compatible with Jellyfin plugins.
/// </summary>
public interface IServerPlugin
{
    /// <summary>Stable lower-case identifier (<c>[a-z0-9._-]</c>, up to 64 characters): prefixes the plugin's tasks, storage and settings.</summary>
    string Id { get; }

    string Name { get; }
    string Version { get; }

    /// <summary>Called once at startup, before the server is built: registers the plugin's services and scheduled tasks.</summary>
    void Register(IPluginContext context);
}

/// <summary>What a plugin can ask the host for while it registers.</summary>
public interface IPluginContext
{
    string PluginId { get; }

    /// <summary>A directory only this plugin writes to (exists already).</summary>
    string DataDirectory { get; }

    /// <summary>The plugin's own settings: the <c>Plugins:Settings:&lt;id&gt;</c> section (environment: <c>Plugins__Settings__&lt;id&gt;__Key</c>).</summary>
    IConfiguration Configuration { get; }

    /// <summary>
    /// The plugin's own service container, separate from the host's. Besides what the plugin registers it has
    /// <see cref="ILogger{T}"/>, <see cref="IHttpClientFactory"/>, <see cref="IPluginStorage"/>, <see cref="PluginEnvironment"/>
    /// and <see cref="IConfiguration"/> (the plugin's section).
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>Adds a job to the dashboard's Scheduled Tasks. <typeparamref name="TJob"/> is created by the plugin's container for every run.</summary>
    void AddScheduledTask<TJob>(PluginTaskInfo info) where TJob : class, IPluginJob;

    /// <summary>
    /// Describes settings the administrator can change in the dashboard. The server builds the settings page from it, shows the
    /// <see cref="PluginSetting.Default"/> values and stores what was changed; <see cref="Configuration"/> then returns the saved value,
    /// else the one from configuration files or environment, else the default.
    /// </summary>
    void AddSettingsSection(PluginSettingsSection section);
}

public enum PluginSettingType
{
    Text,

    /// <summary>Shown masked in the dashboard (API keys).</summary>
    Secret,
    Number,
    Toggle,
    Select,
}

/// <summary>One setting. Values are always strings in <see cref="IPluginContext.Configuration"/>; toggles are <c>true</c>/<c>false</c>.</summary>
/// <param name="Key">The configuration key below the section's prefix (<c>[A-Za-z0-9_]</c>).</param>
/// <param name="Default">What applies until the administrator changes it; empty means "not set".</param>
/// <param name="Options">The allowed values of a <see cref="PluginSettingType.Select"/>.</param>
/// <param name="Min">Bounds of a <see cref="PluginSettingType.Number"/> (whole numbers).</param>
/// <param name="RestartRequired">The plugin reads this setting once at startup, so a change applies after a restart.</param>
public sealed record PluginSetting(
    string Key, string Label, PluginSettingType Type = PluginSettingType.Text, string Default = "", string? Description = null,
    IReadOnlyList<string>? Options = null, long? Min = null, long? Max = null, bool RestartRequired = false);

/// <summary>A titled group of settings on the plugin's page.</summary>
/// <param name="Prefix">Configuration path the keys live under (<c>Profiles:anime</c>), or null for the plugin's root.</param>
public sealed record PluginSettingsSection(string Title, IReadOnlyList<PluginSetting> Settings, string? Prefix = null, string? Description = null);

/// <summary>A scheduled job of a plugin. The host adds the task id and runs it under the plugin's container.</summary>
public interface IPluginJob
{
    /// <summary>Does the work; <paramref name="progress"/> takes percent values (0-100). Cancelled through <paramref name="ct"/>.</summary>
    Task ExecuteAsync(IProgress<double> progress, CancellationToken ct);
}

/// <summary>How a plugin job appears in the dashboard.</summary>
/// <param name="Key">Unique within the plugin (<c>[A-Za-z0-9._-]</c>); clients see it as <c>&lt;plugin id&gt;.&lt;Key&gt;</c>.</param>
/// <param name="DefaultTriggers">Used until an administrator changes them; none means the task only runs by hand.</param>
public sealed record PluginTaskInfo(
    string Key, string Name, string Description, string Category = "Plugins", IReadOnlyList<TaskTrigger>? DefaultTriggers = null);

/// <summary>Who the plugin is and where it may write files.</summary>
public sealed record PluginEnvironment(string PluginId, string DataDirectory);

/// <summary>
/// A plugin's persistent storage in the server's PostgreSQL: JSON documents grouped into collections,
/// invisible to other plugins. Values are serialized with System.Text.Json (web defaults).
/// </summary>
public interface IPluginStorage
{
    Task<T?> GetAsync<T>(string collection, string key, CancellationToken ct = default);
    Task SetAsync<T>(string collection, string key, T value, CancellationToken ct = default);

    /// <summary>Writes many documents in one transaction.</summary>
    Task SetManyAsync<T>(string collection, IEnumerable<KeyValuePair<string, T>> items, CancellationToken ct = default);

    Task<bool> DeleteAsync(string collection, string key, CancellationToken ct = default);

    /// <summary>All documents of the collection (optionally only keys starting with <paramref name="keyPrefix"/>), ordered by key.</summary>
    Task<IReadOnlyDictionary<string, T>> ListAsync<T>(string collection, string? keyPrefix = null, CancellationToken ct = default);

    /// <summary>Removes the whole collection; returns how many documents went.</summary>
    Task<int> DeleteCollectionAsync(string collection, CancellationToken ct = default);
}
