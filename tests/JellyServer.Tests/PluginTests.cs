using JellyServer.Kernel.Data;
using JellyServer.Plugins;
using JellyServer.Plugins.Contracts;
using JellyServer.Tasks.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JellyServer.Tests;

/// <summary>A plugin that lives in this test assembly; the tests copy the assembly into a plugins folder and load it from there.</summary>
public sealed class SamplePlugin : IServerPlugin
{
    public string Id => "sample";
    public string Name => "Sample";
    public string Version => "1.0.0";

    public void Register(IPluginContext context)
    {
        context.Services.AddSingleton(new SampleSetting(context.Configuration["Greeting"] ?? "none"));
        context.AddScheduledTask<SampleJob>(new PluginTaskInfo("write-marker", "Write marker", "Writes a file.", DefaultTriggers: [TaskTrigger.Daily(TimeSpan.FromHours(3))]));
    }
}

public sealed record SampleSetting(string Greeting);

public sealed class SampleJob(PluginEnvironment env, SampleSetting setting, ILogger<SampleJob> log) : IPluginJob
{
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        log.LogInformation("sample job");
        await File.WriteAllTextAsync(Path.Combine(env.DataDirectory, "marker.txt"), setting.Greeting, ct);
        progress.Report(100);
    }
}

public sealed class PluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyserver-plugins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // A loaded plugin assembly stays locked (plugins are never unloaded), so the temp folder may linger.
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private ServiceProvider Boot(Dictionary<string, string?> extra)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:Directory"] = Path.Combine(_root, "plugins"),
            ["Plugins:DataDirectory"] = Path.Combine(_root, "data"),
            ["ConnectionStrings:Postgres"] = "Host=localhost;Username=x;Password=x;Database=x",
        }.Concat(extra).ToDictionary()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(new DatabaseSettings(0));
        services.AddLogging();
        services.AddHttpClient();
        new PluginsModule().Register(services, config);
        return services.BuildServiceProvider();
    }

    private void InstallSamplePlugin(string folder)
    {
        var dir = Path.Combine(_root, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.Copy(typeof(SamplePlugin).Assembly.Location, Path.Combine(dir, folder + ".dll"));
    }

    [Fact]
    public void NoPluginsDirectory_NoTasks()
    {
        using var sp = Boot([]);
        Assert.Empty(sp.GetServices<IScheduledTask>());
    }

    [Fact]
    public async Task PluginJobs_BecomeScheduledTasksAndRunInThePluginsContainer()
    {
        InstallSamplePlugin("SamplePlugin");
        await using var sp = Boot(new() { ["Plugins:Settings:sample:Greeting"] = "hello" });

        var task = Assert.Single(sp.GetServices<IScheduledTask>());
        Assert.Equal("sample.write-marker", task.Key);
        Assert.Matches("^[0-9a-f]{32}$", task.Id);
        Assert.Equal("Plugins", task.Category);
        Assert.Single(task.DefaultTriggers);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "data", "sample", "marker.txt")));
    }

    [Fact]
    public void BrokenPlugin_IsSkippedWithoutStoppingTheHost()
    {
        var dir = Path.Combine(_root, "plugins", "Broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Broken.dll"), "not an assembly");
        InstallSamplePlugin("SamplePlugin");

        using var sp = Boot([]);
        Assert.Single(sp.GetServices<IScheduledTask>());
    }
}
