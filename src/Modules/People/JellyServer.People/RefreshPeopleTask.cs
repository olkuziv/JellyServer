using JellyServer.Tasks.Contracts;

namespace JellyServer.People;

/// <summary>Jellyfin's "Refresh People" task: drops people no item credits any more (credits go with their items).</summary>
public sealed class RefreshPeopleTask(PeopleStore store, ILogger<RefreshPeopleTask> log) : IScheduledTask
{
    public string Id => "7be8e5f2a3f948a6b1b1b6c3a1c2d0e4";
    public string Key => "RefreshPeople";
    public string Name => "Refresh People";
    public string Description => "Removes people who are no longer credited on any item.";
    public string Category => "Library";

    public IReadOnlyList<TaskTrigger> DefaultTriggers => [TaskTrigger.Interval(TimeSpan.FromDays(7))];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var removed = await store.RemoveUncreditedAsync(ct);
        if (removed > 0) log.LogInformation("Removed {Count} people without credits", removed);
        progress.Report(100);
    }
}
