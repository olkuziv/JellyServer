using JellyServer.Tasks.Contracts;
using Microsoft.Extensions.Options;

namespace JellyServer.Catalog;

/// <summary>Jellyfin's "Scan Media Library" task: reads every library's folders and saves what changed.</summary>
public sealed class ScanLibraryTask(ScanCoordinator scanner, IOptions<ScannerOptions> options) : IScheduledTask
{
    public const string TaskId = "b1f9ce7d9c2840729eae1a2a7060e7dc";

    // jellyfin-web finds the dashboard's "Scan All Libraries" action by this key.
    public const string TaskKey = "RefreshLibrary";

    public string Id => TaskId;
    public string Key => TaskKey;
    public string Name => "Scan Media Library";
    public string Description => "Scans media libraries for new and changed files.";
    public string Category => "Library";

    public IReadOnlyList<TaskTrigger> DefaultTriggers
    {
        get
        {
            var opt = options.Value;
            var triggers = new List<TaskTrigger>();
            if (opt.OnStartup) triggers.Add(TaskTrigger.Startup());
            if (opt.Interval > TimeSpan.Zero) triggers.Add(TaskTrigger.Interval(opt.Interval));
            return triggers;
        }
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await scanner.ScanAllAsync(ct, progress);
        progress.Report(100);
    }
}