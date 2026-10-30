using System.Text.RegularExpressions;
using JellyServer.Plugins.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JellyServer.Plugins;

internal sealed partial class PluginContext(string pluginId, string dataDirectory, IConfiguration configuration) : IPluginContext
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex KeyPattern();

    private readonly List<PluginTaskRegistration> _tasks = [];

    public string PluginId { get; } = pluginId;
    public string DataDirectory { get; } = dataDirectory;
    public IConfiguration Configuration { get; } = configuration;
    public IServiceCollection Services { get; } = new ServiceCollection();

    public IReadOnlyList<PluginTaskRegistration> Tasks => _tasks;

    public void AddScheduledTask<TJob>(PluginTaskInfo info) where TJob : class, IPluginJob
    {
        if (!KeyPattern().IsMatch(info.Key ?? "")) throw new ArgumentException($"Invalid task key '{info.Key}' (use A-Z, a-z, 0-9, '.', '_', '-')", nameof(info));
        if (_tasks.Any(t => t.Info.Key == info.Key)) throw new ArgumentException($"Task '{info.Key}' is already registered", nameof(info));
        Services.TryAddTransient<TJob>();
        _tasks.Add(new PluginTaskRegistration(info, typeof(TJob)));
    }
}
