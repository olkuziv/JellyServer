using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.Nvenc;

public sealed class NvencOptions
{
    /// <summary>NVENC preset, p1 (fastest) … p7 (best quality), used when the dashboard's encoder preset is auto.</summary>
    public string Preset { get; set; } = "p4";

    /// <summary>Constant quality level (<c>-cq</c>); lower is better and bigger.</summary>
    public int Cq { get; set; } = 23;

    /// <summary>Consumer GeForce cards allow only a few NVENC sessions at once.</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>CUDA device index used for decoding, filtering and encoding.</summary>
    public int Gpu { get; set; }
}

public static class NvencModule
{
    /// <summary>
    /// Registers the NVIDIA pipeline (NVDEC → CUDA filters → NVENC) and, under the same id, its decode stage for
    /// software encoders. Settings come from <c>{transcoding}:Nvenc</c>.
    /// </summary>
    public static IServiceCollection AddNvencPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<NvencOptions>().Bind(transcoding.GetSection("Nvenc"));
        services.AddSingleton<NvencPipeline>();
        services.AddSingleton<IVideoPipeline>(sp => sp.GetRequiredService<NvencPipeline>());
        services.AddSingleton<IVideoDecoder>(sp => sp.GetRequiredService<NvencPipeline>());
        return services;
    }
}
