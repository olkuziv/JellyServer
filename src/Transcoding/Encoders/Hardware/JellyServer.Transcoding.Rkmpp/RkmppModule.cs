using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.Rkmpp;

public sealed class RkmppOptions
{
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Bitrate without a client cap (the RKMPP encoders are driven by bitrate, as in Jellyfin).</summary>
    public long FallbackBitrate { get; set; } = 8_000_000;
}

public static class RkmppModule
{
    /// <summary>
    /// Registers Rockchip MPP encoding/decoding with RGA filters (Rockchip boards with jellyfin-ffmpeg's rkmpp build).
    /// Settings come from <c>{transcoding}:Rkmpp</c>.
    /// </summary>
    public static IServiceCollection AddRkmppPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<RkmppOptions>().Bind(transcoding.GetSection("Rkmpp"));
        services.AddSingleton<RkmppPipeline>();
        services.AddSingleton<IVideoPipeline>(sp => sp.GetRequiredService<RkmppPipeline>());
        services.AddSingleton<IVideoDecoder>(sp => sp.GetRequiredService<RkmppPipeline>());
        return services;
    }
}
