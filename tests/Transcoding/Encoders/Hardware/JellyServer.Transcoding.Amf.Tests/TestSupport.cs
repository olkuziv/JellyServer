using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding;

/// <summary>An ffmpeg that lists <paramref name="features"/> and runs (or fails) test commands without starting a process.</summary>
internal sealed class FakeFfmpeg(IEnumerable<string> features, Func<string, bool>? runs = null) : IFfmpegCapabilities
{
    private readonly HashSet<string> _features = [.. features];
    public List<string> Runs { get; } = [];
    public string FfmpegPath => "ffmpeg";
    public bool HasEncoder(string name) => _features.Contains(name);
    public bool HasDecoder(string name) => _features.Contains(name);
    public bool HasFilter(string name) => _features.Contains(name);
    public bool HasHwAccel(string name) => _features.Contains(name);

    public Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var line = string.Join(' ', args);
        lock (Runs) Runs.Add(line);
        return Task.FromResult(runs?.Invoke(line) ?? true);
    }
}

/// <summary>A module registered through its Add method, with live configuration.</summary>
internal sealed class Module
{
    public required IConfigurationRoot Config { get; init; }
    public required ServiceProvider Services { get; init; }
    public IVideoPipeline Pipeline => Services.GetServices<IVideoPipeline>().Single();
    public IVideoDecoder Decoder => Services.GetServices<IVideoDecoder>().Single();

    public static Module Create(Func<IServiceCollection, IConfiguration, IServiceCollection> add, params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new Module { Config = config, Services = add(new ServiceCollection().AddLogging(), config).BuildServiceProvider() };
    }

    /// <summary>Changes a setting the way a dashboard save does (configuration reload).</summary>
    public void Set(string key, string value)
    {
        Config[key] = value;
        Config.Reload();
    }
}

internal static class Jobs
{
    public static readonly VideoSource Sdr1080 = new("h264", 1920, 1080, 8, null, "High", 23.976);
    public static readonly VideoSource Hevc4k10 = new("hevc", 3840, 2160, 10, null, "Main 10", 24);
    public static readonly VideoSource Hdr4k = new("hevc", 3840, 2160, 10, "HDR10", "Main 10", 24);
    public static readonly VideoSource Hlg4k = new("hevc", 3840, 2160, 10, "HLG", "Main 10", 50);
    public static readonly VideoSource Interlaced = new("mpeg2video", 1920, 1080, 8, null, "Main", 25, Interlaced: true);
    public static readonly VideoSource Xvid = new("mpeg4", 720, 400, 8, null, "Advanced Simple Profile", 25);
    public static readonly VideoTarget H264 = new(false, "h264", "ts", null);
    public static readonly VideoTarget HevcMp4 = new(false, "hevc", "mp4", null);
    public static readonly VideoTarget Av1Mp4 = new(false, "av1", "mp4", null);

    public static VideoJob Job(VideoSource source, VideoTarget? target = null, VideoProcessing? processing = null, int segment = 6) =>
        new(source, target ?? H264, processing ?? VideoProcessing.Default, new SegmentTiming(segment));

    public static string Args(VideoArgs a) => string.Join(' ', a.Input.Concat(a.Output));
    public static string Args(DecodedVideo d) => string.Join(' ', d.Input) + " | " + string.Join(',', d.Filters);
    public static string Vf(VideoArgs a) => a.Output.SkipWhile(x => x != "-vf").Skip(1).FirstOrDefault() ?? "";

    public static VideoProcessing With(Func<VideoProcessing, VideoProcessing> change) => change(VideoProcessing.Default);
}
