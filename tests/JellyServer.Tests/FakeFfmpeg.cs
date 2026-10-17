using JellyServer.Transcoding;

namespace JellyServer.Tests;

/// <summary>Capabilities of an imaginary ffmpeg build: everything listed is "supported".</summary>
public sealed class FakeFfmpeg(params string[] features) : IFfmpegCapabilities
{
    public static readonly FakeFfmpeg Full = new("libx264", "zscale", "tonemap");

    public string FfmpegPath => "ffmpeg";
    public bool HasEncoder(string name) => features.Contains(name);
    public bool HasDecoder(string name) => features.Contains(name);
    public bool HasFilter(string name) => features.Contains(name);
    public bool HasHwAccel(string name) => features.Contains(name);
    public Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(true);
}
