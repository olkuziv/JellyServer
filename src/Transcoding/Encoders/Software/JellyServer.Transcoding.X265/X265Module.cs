using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.X265;

public sealed class X265Options
{
    /// <summary>libx265 preset used when the dashboard's encoder preset is "auto" (Jellyfin: veryfast).</summary>
    public string Preset { get; set; } = "veryfast";

    /// <summary>Quality used when the dashboard sets no H.265 CRF (Jellyfin's default 28).</summary>
    public int Crf { get; set; } = 28;

    /// <summary>libx265 is several times slower than libx264: one job at a time by default.</summary>
    public int MaxConcurrent { get; set; } = 1;
}

public static class X265Module
{
    /// <summary>Registers the libx265 pipeline. Settings come from <c>{transcoding}:X265</c>.</summary>
    public static IServiceCollection AddX265Pipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<X265Options>().Bind(transcoding.GetSection("X265"));
        services.AddSingleton<IVideoPipeline, X265Pipeline>();
        return services;
    }
}
