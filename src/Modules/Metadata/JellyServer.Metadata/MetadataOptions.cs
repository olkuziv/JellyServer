namespace JellyServer.Metadata;

/// <summary>Bound from <c>JellyServer:Metadata</c>.</summary>
public sealed class MetadataOptions
{
    public const string Section = "JellyServer:Metadata";

    /// <summary>Items looked up in parallel.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>How long to wait before asking again about an item no provider knew.</summary>
    public TimeSpan NotFoundRetry { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long to wait after a temporary failure.</summary>
    public TimeSpan FailedRetry { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Default interval trigger of the metadata task: the catalog is walked for items nobody looked up yet (0 = no interval trigger).</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(24);
}