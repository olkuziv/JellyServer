using JellyServer.Catalog;
using JellyServer.Metadata;
using JellyServer.Tasks.Contracts;
using Xunit;

namespace JellyServer.Tests;

public class ScheduledTaskWiringTests
{
    // The task manager is built from all tasks: a task that asks for the manager makes a cycle, and the container then hangs at startup.
    [Fact]
    public void Tasks_DoNotDependOnTheTaskManager()
    {
        var tasks = new[] { typeof(ScanLibraryTask), typeof(DownloadMetadataTask), typeof(RefreshAllMetadataTask) }.Concat(
            new[] { typeof(ScanLibraryTask).Assembly, typeof(DownloadMetadataTask).Assembly }
                .SelectMany(a => a.GetTypes()).Where(t => typeof(IScheduledTask).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })).Distinct();
        foreach (var task in tasks)
            Assert.DoesNotContain(task.GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(ITaskManager));
    }
}