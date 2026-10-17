using JellyServer.Settings;
using JellyServer.Settings.Contracts;

namespace JellyServer.Configuration;

/// <summary>
/// Configuration provider fed from settings saved on the dashboard. Added last, so dashboard values
/// override appsettings and environment variables; a reload makes <c>IOptionsMonitor</c> pick them up.
/// </summary>
public sealed class DashboardConfigurationProvider : ConfigurationProvider
{
    public void Apply(IDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

public sealed class DashboardConfigurationSource : IConfigurationSource
{
    public DashboardConfigurationProvider Provider { get; } = new();
    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

/// <summary>Loads the dashboard's encoding settings into configuration at startup and whenever any replica saves them.</summary>
public sealed class DashboardSettingsSync(INamedConfigurationStore store, DashboardConfigurationProvider provider, ILogger<DashboardSettingsSync> log)
    : BackgroundService
{
    private DateTime? _seen;

    /// <summary>Called once at startup, before anything reads transcoding options.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        _seen = await store.LastChangeAsync(ct);
        await ApplyAsync(ct);
    }

    /// <summary>Saves and applies at once, so this replica does not wait for (or repeat on) the next poll.</summary>
    public async Task SaveAsync(string key, string json, CancellationToken ct)
    {
        await store.SaveAsync(key, json, ct);
        _seen = await store.LastChangeAsync(ct);
        await ApplyAsync(ct);
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        var json = await store.GetAsync(EncodingSettings.Key, ct);
        provider.Apply(json is null ? new Dictionary<string, string?>() : EncodingSettings.ToConfiguration(EncodingSettings.Deserialize(json)));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var latest = await store.LastChangeAsync(ct);
                if (latest == _seen) continue;
                _seen = latest;
                await ApplyAsync(ct);
                log.LogInformation("Dashboard settings changed; reloaded");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Dashboard settings poll failed: {Error}", ex.Message);
            }
        }
    }
}
