using System.Globalization;
using JellyServer.Plugins.Contracts;

namespace JellyServer.Plugins;

/// <summary>
/// Settings the administrator changes on a plugin's dashboard page. They are stored in the plugins database and put on top of the
/// plugin's configuration, so the plugin reads them like any other setting (and sees a change at its next run, without a restart).
/// Only values that differ from the plugin's declared default are stored.
/// </summary>
internal sealed class PluginSettingsService(PluginLoadReport report, PluginStore store, ILogger<PluginSettingsService> log) : IHostedService
{
    public const string Mask = "********";

    // "_host" is not a valid plugin id, so no plugin can read these documents.
    private const string HostId = "_host";
    private const string Collection = "settings";
    private const int MaxLength = 2000;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, string>> _saved = new(StringComparer.Ordinal);

    /// <summary>Completes once the saved values are applied; plugin jobs wait for it so their first run already sees them.</summary>
    public Task Ready => report.Loaded.Any(p => p.Context.Sections.Count > 0) ? _ready.Task : Task.CompletedTask;

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            foreach (var plugin in report.Loaded.Where(p => p.Context.Sections.Count > 0))
            {
                try
                {
                    var values = await store.GetAsync<Dictionary<string, string>>(HostId, Collection, plugin.Plugin.Id, ct) ?? [];
                    _saved[plugin.Plugin.Id] = values;
                    plugin.Context.ApplyOverrides(values);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Saved settings of plugin {Id} could not be read; its defaults apply", plugin.Plugin.Id);
                }
            }
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private LoadedPlugin? Find(Guid id) =>
        report.Loaded.FirstOrDefault(p => p.Meta.Guid == id && p.Context.Sections.Count > 0);

    public IReadOnlyList<ConfigurationPageInfoDto> Pages(bool? enableInMainMenu) =>
        enableInMainMenu == false ? [] : report.Loaded.Where(p => p.Context.Sections.Count > 0).Select(p => new ConfigurationPageInfoDto
        {
            Name = p.Plugin.Name, DisplayName = p.Plugin.Name, EnableInMainMenu = true, MenuSection = "server", MenuIcon = "settings",
            PluginId = p.Meta.Guid.ToString("N"),
        }).ToList();

    public (string Name, Guid Id)? FindPageByName(string name) =>
        report.Loaded.Where(p => p.Context.Sections.Count > 0)
            .Where(p => string.Equals(p.Plugin.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(p => ((string Name, Guid Id)?)(p.Plugin.Name, p.Meta.Guid)).FirstOrDefault();

    public PluginSettingsDto? Get(Guid id)
    {
        if (Find(id) is not { } plugin) return null;
        var saved = _saved.GetValueOrDefault(plugin.Plugin.Id) ?? [];
        return new PluginSettingsDto
        {
            Name = plugin.Plugin.Name,
            Sections = plugin.Context.Sections.Select(section => new PluginSettingsSectionDto
            {
                Title = section.Title, Description = section.Description,
                Fields = section.Settings.Select(s =>
                {
                    var key = PluginContext.FullKey(section, s);
                    var value = plugin.Context.Configuration[key] ?? s.Default;
                    if (s.Type == PluginSettingType.Toggle) value = bool.TryParse(value, out var b) && b ? "true" : "false";
                    return new PluginSettingFieldDto
                    {
                        Key = key, Label = s.Label, Type = s.Type.ToString(), Default = s.Type == PluginSettingType.Secret ? "" : s.Default,
                        Description = s.Description, Options = s.Options?.ToList(), Min = s.Min, Max = s.Max, RestartRequired = s.RestartRequired,
                        Value = s.Type == PluginSettingType.Secret && value.Length > 0 ? Mask : value,
                        Overridden = saved.ContainsKey(key),
                    };
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Validates and stores the new values. Returns null when the plugin has no settings page, else the problems (empty = saved).</summary>
    public async Task<IReadOnlyList<string>?> SaveAsync(Guid id, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        if (Find(id) is not { } plugin) return null;
        var known = plugin.Context.Sections.SelectMany(sec => sec.Settings.Select(s => (Key: PluginContext.FullKey(sec, s), Setting: s)))
            .ToDictionary(x => x.Key, x => x.Setting, StringComparer.OrdinalIgnoreCase);

        await _save.WaitAsync(ct);
        try
        {
            var next = new Dictionary<string, string>(_saved.GetValueOrDefault(plugin.Plugin.Id) ?? [], StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var (key, raw) in values)
            {
                if (!known.TryGetValue(key, out var setting)) { errors.Add($"{key}: unknown setting"); continue; }
                if (setting.Type == PluginSettingType.Secret && raw == Mask) continue; // the page never saw the real value
                if (raw is null) { next.Remove(key); continue; }

                var value = raw.Trim();
                if (value.Length > MaxLength) { errors.Add($"{setting.Label}: longer than {MaxLength} characters"); continue; }
                if (Validate(setting, value, out var normalized) is { } problem) { errors.Add($"{setting.Label}: {problem}"); continue; }
                if (normalized == setting.Default) next.Remove(key); else next[key] = normalized;
            }
            if (errors.Count > 0) return errors;

            await store.SetAsync(HostId, Collection, plugin.Plugin.Id, next, ct);
            _saved[plugin.Plugin.Id] = next;
            plugin.Context.ApplyOverrides(next);
            log.LogInformation("Settings of plugin {Id} saved ({Count} changed from the defaults)", plugin.Plugin.Id, next.Count);
            return errors;
        }
        finally
        {
            _save.Release();
        }
    }

    /// <summary>Null when the value is acceptable; <paramref name="normalized"/> is the form that is stored.</summary>
    internal static string? Validate(PluginSetting setting, string value, out string normalized)
    {
        normalized = value;
        if (value.Length == 0)
            return setting.Default.Length == 0 || setting.Type is PluginSettingType.Text or PluginSettingType.Secret ? null : "a value is required";

        switch (setting.Type)
        {
            case PluginSettingType.Number:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return "not a whole number";
                if (setting.Min is { } min && n < min) return $"at least {min}";
                if (setting.Max is { } max && n > max) return $"at most {max}";
                normalized = n.ToString(CultureInfo.InvariantCulture);
                return null;
            case PluginSettingType.Toggle:
                if (!bool.TryParse(value, out var b)) return "must be true or false";
                normalized = b ? "true" : "false";
                return null;
            case PluginSettingType.Select:
                var option = setting.Options!.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                if (option is null) return "not one of the allowed values";
                normalized = option;
                return null;
            default:
                return null;
        }
    }
}
