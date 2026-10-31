using System.Text.RegularExpressions;
using JellyServer.Plugins.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JellyServer.Plugins;

/// <summary>A configuration layer backed by a dictionary that can be swapped while the server runs.</summary>
internal sealed class DictionaryConfiguration : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public void Replace(IEnumerable<KeyValuePair<string, string?>> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

internal sealed partial class PluginContext : IPluginContext
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SettingKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9_]+(:[A-Za-z0-9_]+)*$")]
    private static partial Regex PrefixPattern();

    private readonly List<PluginTaskRegistration> _tasks = [];
    private readonly List<PluginSettingsSection> _sections = [];
    private readonly DictionaryConfiguration _defaults = new();
    private readonly DictionaryConfiguration _overrides = new();

    /// <param name="configuration">The plugin's section of the host configuration (files, environment).</param>
    public PluginContext(string pluginId, string dataDirectory, IConfiguration configuration)
    {
        PluginId = pluginId;
        DataDirectory = dataDirectory;
        // Lowest to highest: the defaults the plugin declared, files and environment, what the administrator saved in the dashboard.
        Configuration = new ConfigurationBuilder().Add(_defaults).AddConfiguration(configuration).Add(_overrides).Build();
    }

    public string PluginId { get; }
    public string DataDirectory { get; }
    public IConfiguration Configuration { get; }
    public IServiceCollection Services { get; } = new ServiceCollection();

    public IReadOnlyList<PluginTaskRegistration> Tasks => _tasks;
    public IReadOnlyList<PluginSettingsSection> Sections => _sections;

    public void AddScheduledTask<TJob>(PluginTaskInfo info) where TJob : class, IPluginJob
    {
        if (!KeyPattern().IsMatch(info.Key ?? "")) throw new ArgumentException($"Invalid task key '{info.Key}' (use A-Z, a-z, 0-9, '.', '_', '-')", nameof(info));
        if (_tasks.Any(t => t.Info.Key == info.Key)) throw new ArgumentException($"Task '{info.Key}' is already registered", nameof(info));
        Services.TryAddTransient<TJob>();
        _tasks.Add(new PluginTaskRegistration(info, typeof(TJob)));
    }

    public void AddSettingsSection(PluginSettingsSection section)
    {
        if (section.Prefix is { } prefix && !PrefixPattern().IsMatch(prefix))
            throw new ArgumentException($"Invalid settings prefix '{prefix}' (use A-Z, a-z, 0-9, '_' separated by ':')", nameof(section));
        foreach (var s in section.Settings)
        {
            if (!SettingKeyPattern().IsMatch(s.Key ?? "")) throw new ArgumentException($"Invalid setting key '{s.Key}' (use A-Z, a-z, 0-9, '_')", nameof(section));
            if (s.Type == PluginSettingType.Select && s.Options is not { Count: > 0 })
                throw new ArgumentException($"Setting '{s.Key}' is a select without options", nameof(section));
            if (_sections.Any(o => o.Settings.Any(x => FullKey(o, x) == FullKey(section, s))))
                throw new ArgumentException($"Setting '{FullKey(section, s)}' is declared twice", nameof(section));
        }
        _sections.Add(section);
        _defaults.Replace(_sections.SelectMany(sec => sec.Settings.Where(s => s.Default.Length > 0)
            .Select(s => KeyValuePair.Create(FullKey(sec, s), (string?)s.Default))));
    }

    /// <summary>The values the administrator saved, as the highest configuration layer.</summary>
    public void ApplyOverrides(IEnumerable<KeyValuePair<string, string>> values) =>
        _overrides.Replace(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value)));

    internal static string FullKey(PluginSettingsSection section, PluginSetting setting) =>
        section.Prefix is { Length: > 0 } p ? p + ":" + setting.Key : setting.Key;
}
