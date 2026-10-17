using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyServer.Transcoding.Qsv;

public sealed class QsvOptions
{
    public const string DefaultDevice = "/dev/dri/renderD128";

    /// <summary>
    /// DRM render node of the Intel GPU (Jellyfin's QsvDevice); the QSV device is derived from a VA-API device on it.
    /// Empty means <see cref="DefaultDevice"/>.
    /// </summary>
    public string Device { get; set; } = "";

    /// <summary>QSV preset used when the dashboard's encoder preset is "auto": veryfast, faster, fast, medium, slow, slower or veryslow.</summary>
    public string Preset { get; set; } = "veryfast";

    /// <summary>ICQ quality (1-51, lower is better) used when the client sets no bitrate limit.</summary>
    public int GlobalQuality { get; set; } = 23;

    public int MaxConcurrent { get; set; } = 4;
}

public static class QsvModule
{
    /// <summary>
    /// Registers the Intel Quick Sync pipeline and its hardware decoder (one shared instance).
    /// Settings come from <c>{transcoding}:Qsv</c>.
    /// </summary>
    public static IServiceCollection AddQsvPipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<QsvOptions>().Bind(transcoding.GetSection("Qsv"));
        services.AddSingleton<QsvPipeline>();
        services.AddSingleton<IVideoPipeline>(sp => sp.GetRequiredService<QsvPipeline>());
        services.AddSingleton<IVideoDecoder>(sp => sp.GetRequiredService<QsvPipeline>());
        return services;
    }
}
