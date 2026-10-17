using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.V4l2m2m;

public sealed class V4l2m2mOptions
{
    /// <summary>SoC encoders (Raspberry Pi 4: one 1080p30 H.264 stream) rarely manage more.</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>Bitrate without a client cap (the V4L2 encoders are driven by bitrate only).</summary>
    public long FallbackBitrate { get; set; } = 8_000_000;

    /// <summary>
    /// Decode with the V4L2 M2M decoders (h264_v4l2m2m, …). Off by default like Jellyfin, which decodes in software for
    /// this hardware type; when on, each codec is enabled only after a real test decode.
    /// </summary>
    public bool HwDecode { get; set; }
}

public static class V4l2m2mModule
{
    /// <summary>
    /// Registers Video4Linux2 memory-to-memory encoding (Raspberry Pi and other SoCs exposing /dev/video* M2M devices).
    /// Settings come from <c>{transcoding}:V4l2m2m</c>.
    /// </summary>
    public static IServiceCollection AddV4l2m2mPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<V4l2m2mOptions>().Bind(transcoding.GetSection("V4l2m2m"));
        services.AddSingleton<V4l2m2mPipeline>();
        services.AddSingleton<IVideoPipeline>(sp => sp.GetRequiredService<V4l2m2mPipeline>());
        services.AddSingleton<IVideoDecoder>(sp => sp.GetRequiredService<V4l2m2mPipeline>());
        return services;
    }
}
