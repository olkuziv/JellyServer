using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.Vaapi;

public sealed class VaapiOptions
{
    /// <summary>DRM render node of the GPU (the dashboard's VA-API device); it must be mapped into the container.</summary>
    public string Device { get; set; } = "/dev/dri/renderD128";

    /// <summary>Constant QP when the client sets no bitrate cap (lower is better quality, 0-52).</summary>
    public int Qp { get; set; } = 24;

    public int MaxConcurrent { get; set; } = 4;
}

public static class VaapiModule
{
    /// <summary>
    /// Registers the VAAPI (Intel/AMD on Linux) pipeline and its hardware decoder for software encoders.
    /// Settings come from <c>{transcoding}:Vaapi</c>; both share one probe of the device.
    /// </summary>
    public static IServiceCollection AddVaapiPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<VaapiOptions>().Bind(transcoding.GetSection("Vaapi"));
        services.AddSingleton<VaapiProbe>();
        services.AddSingleton<IVideoPipeline, VaapiPipeline>();
        services.AddSingleton<IVideoDecoder, VaapiDecoder>();
        return services;
    }
}
