using JellyServer.Configuration;
using JellyServer.Tasks.Contracts;
using Microsoft.Extensions.Options;

namespace JellyServer.Services;

/// <summary>
/// Jellyfin's "Clean Cache Directory" for artwork: deletes cached images not used for <see cref="ImageOptions.CacheMaxAgeDays"/> days.
/// The cache only holds what can be made again (resized variants, remote artwork downloaded and compressed once), so nothing is lost;
/// the next request for a deleted picture builds it again. Uploaded images are stored elsewhere and are not touched.
/// </summary>
public sealed class ClearImageCacheTask(ImageService images, IOptions<ServerOptions> options, ILogger<ClearImageCacheTask> log) : IScheduledTask
{
    public string Id => "c9d2b3a14e5f4c7d8a1b6e0f3d2c5a47";
    public string Key => "DeleteCacheFiles";
    public string Name => "Clean Image Cache";
    public string Description => "Deletes cached images (resized copies and downloaded artwork) that have not been used for a while. They are made again when needed.";
    public string Category => "Maintenance";

    public IReadOnlyList<TaskTrigger> DefaultTriggers => [TaskTrigger.Interval(TimeSpan.FromHours(24))];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var days = Math.Max(0, options.Value.Images.CacheMaxAgeDays);
        var freed = await Task.Run(() => ImageCacheJanitor.ClearOlderThan(images.CacheDir, TimeSpan.FromDays(days), progress, ct), ct);
        log.LogInformation("Image cache cleaned (files unused for {Days} days): {Freed} MB freed", days, freed / (1024 * 1024));
    }
}
