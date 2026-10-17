using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.SvtAv1;

public sealed class SvtAv1Options
{
    /// <summary>SVT-AV1 preset (5 slowest offered … 13 fastest) used when the dashboard's preset is "auto"; Jellyfin uses 10.</summary>
    public int Preset { get; set; } = 10;

    /// <summary>Quality (0–63) used when the dashboard sets no H.265 CRF; Jellyfin's page shares that field with AV1.</summary>
    public int Crf { get; set; } = 28;

    public int MaxConcurrent { get; set; } = 1;
}

public static class SvtAv1Module
{
    /// <summary>Registers the SVT-AV1 pipeline. Settings come from <c>{transcoding}:SvtAv1</c>.</summary>
    public static IServiceCollection AddSvtAv1Pipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<SvtAv1Options>().Bind(transcoding.GetSection("SvtAv1"));
        services.AddSingleton<IVideoPipeline, SvtAv1Pipeline>();
        return services;
    }
}
