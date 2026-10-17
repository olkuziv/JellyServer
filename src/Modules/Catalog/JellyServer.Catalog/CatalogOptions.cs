namespace JellyServer.Catalog;

/// <summary>Bound from <c>JellyServer:Cache</c>.</summary>
public sealed class CacheOptions
{
    public const string Section = "JellyServer:Cache";

    public TimeSpan ItemTtl { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan QueryTtl { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Bound from <c>JellyServer:Scanner</c>.</summary>
public sealed class ScannerOptions
{
    public const string Section = "JellyServer:Scanner";

    /// <summary>Default interval trigger of the scan task (changeable on the dashboard).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(12);
    public bool OnStartup { get; set; } = true;
    public int Workers { get; set; } = 16;
    public int BatchSize { get; set; } = 500;
}