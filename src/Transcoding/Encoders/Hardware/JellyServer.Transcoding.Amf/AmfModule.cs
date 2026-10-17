using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.Amf;

public sealed class AmfOptions
{
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Used when the dashboard preset is "auto": veryslow … ultrafast, mapped to AMF's -quality like Jellyfin ("auto" = speed).</summary>
    public string Preset { get; set; } = "auto";

    /// <summary>Constant QP when the client sets no bitrate cap (0-51; AV1 is scaled to its 0-255 range).</summary>
    public int Qp { get; set; } = 24;
}

public static class AmfModule
{
    /// <summary>
    /// Registers the AMD AMF encoder (Windows with d3d11va decoding and OpenCL filters, or Linux with AMD's AMF runtime)
    /// and its d3d11va decoder. Settings come from <c>{transcoding}:Amf</c>.
    /// </summary>
    public static IServiceCollection AddAmfPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<AmfOptions>().Bind(transcoding.GetSection("Amf"));
        services.AddSingleton<AmfPipeline>();
        services.AddSingleton<IVideoPipeline>(sp => sp.GetRequiredService<AmfPipeline>());
        services.AddSingleton<IVideoDecoder>(sp => sp.GetRequiredService<AmfPipeline>());
        return services;
    }
}
