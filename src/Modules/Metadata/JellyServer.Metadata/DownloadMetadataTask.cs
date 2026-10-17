using JellyServer.Metadata.Contracts;
using JellyServer.Tasks.Contracts;
using Microsoft.Extensions.Options;

namespace JellyServer.Metadata;

/// <summary>
/// "Download Metadata": looks up everything nobody has looked up yet and stays running until the lookups are done, so the
/// dashboard shows the progress and can stop it.
/// </summary>
public sealed class DownloadMetadataTask(IMetadataRefresh metadata, IOptions<MetadataOptions> options) : IScheduledTask
{
    public const string TaskId = "6f1d3a52c8e94b7aa3d05e91b4c27f18";
    public const string TaskKey = "DownloadMetadata";

    public string Id => TaskId;
    public string Key => TaskKey;
    public string Name => "Download Metadata";
    public string Description => "Looks up descriptions, images and cast of the library items on the internet.";
    public string Category => "Library";

    public IReadOnlyList<TaskTrigger> DefaultTriggers =>
        options.Value.ReconcileInterval > TimeSpan.Zero ? [TaskTrigger.Startup(), TaskTrigger.Interval(options.Value.ReconcileInterval)] : [TaskTrigger.Startup()];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await metadata.ReconcileAsync(ct);
        await WaitUntilDoneAsync(metadata, progress, ct);
    }

    /// <summary>Stays until the queue is worked off, reporting progress; a stop drops what has not started (the lookups in progress finish by themselves).</summary>
    internal static async Task WaitUntilDoneAsync(IMetadataRefresh metadata, IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (metadata.Progress is { IsRunning: true } p)
            {
                progress.Report(p.Percent);
                await timer.WaitForNextTickAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            metadata.Cancel();
            throw;
        }
        progress.Report(100);
    }
}

/// <summary>"Refresh All Metadata": once a week every item is looked up again, so changes made on the provider's side (new cast, better images) arrive.</summary>
public sealed class RefreshAllMetadataTask(IMetadataRefresh metadata, IServiceProvider services) : IScheduledTask
{
    public const string TaskId = "a3c8e1f04b6d4792b5e07d31c9f28a64";

    public string Id => TaskId;
    public string Key => "RefreshAllMetadata";
    public string Name => "Refresh All Metadata";
    public string Description => "Looks up every item on the internet again, also those that already have data.";
    public string Category => "Library";

    public IReadOnlyList<TaskTrigger> DefaultTriggers => [TaskTrigger.Weekly(DayOfWeek.Sunday, TimeSpan.FromHours(3))];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await metadata.QueueAllAsync(ct);
        await DownloadMetadataTask.WaitUntilDoneAsync(metadata, progress, ct);
        // Items the provider stopped naming get their local name back when the library is scanned.
        // (looked up now, not injected: the task manager is built from the tasks)
        services.GetRequiredService<ITaskManager>().Start("RefreshLibrary");
    }
}