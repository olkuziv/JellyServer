using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.VideoToolbox;

public sealed class VideoToolboxOptions
{
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Used when the dashboard preset is "auto": veryslow … medium → -prio_speed 0, faster presets (and "auto") → 1, like Jellyfin.</summary>
    public string Preset { get; set; } = "auto";

    /// <summary>Constant quality (-q:v, 1-100) without a bitrate cap; Apple Silicon only (the probe checks).</summary>
    public int Quality { get; set; } = 60;

    /// <summary>Bitrate without a client cap where constant quality is unavailable (Intel Macs).</summary>
    public long FallbackBitrate { get; set; } = 8_000_000;
}

public static class VideoToolboxModule
{
    /// <summary>
    /// Registers Apple VideoToolbox encoding and decoding (macOS only; the Linux/Docker ffmpeg has no VideoToolbox).
    /// Settings come from <c>{transcoding}:VideoToolbox</c>.
    /// </summary>
    public static IServiceCollection AddVideoToolboxPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<VideoToolboxOptions>().Bind(transcoding.GetSection("VideoToolbox"));
        services.AddSingleton<VideoToolboxPipeline>();
        services.AddSingleton<IVideoPipeline>(sp => sp.GetRequiredService<VideoToolboxPipeline>());
        services.AddSingleton<IVideoDecoder>(sp => sp.GetRequiredService<VideoToolboxPipeline>());
        return services;
    }
}
