using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.X264;

public sealed class X264Options
{
    /// <summary>libx264 preset used when the dashboard's encoder preset is "auto".</summary>
    public string Preset { get; set; } = "veryfast";

    /// <summary>Quality used when the dashboard sets no H.264 CRF.</summary>
    public int Crf { get; set; } = 23;

    public int MaxConcurrent { get; set; } = 2;
}

public static class X264Module
{
    /// <summary>
    /// Registers the libx264 pipeline. Settings come from <c>{transcoding}:X264</c>; the older flat keys
    /// <c>H264Preset</c>, <c>H264Crf</c> and <c>MaxConcurrentEncodes</c> still work as defaults.
    /// </summary>
    public static IServiceCollection AddX264Pipeline(this IServiceCollection services, IConfiguration transcoding)
    {
        services.AddOptions<X264Options>()
            .Configure(o =>
            {
                if (transcoding["H264Preset"] is { Length: > 0 } preset) o.Preset = preset;
                if (int.TryParse(transcoding["H264Crf"], out var crf)) o.Crf = crf;
                if (int.TryParse(transcoding["MaxConcurrentEncodes"], out var max)) o.MaxConcurrent = Math.Max(1, max);
            })
            .Bind(transcoding.GetSection("X264"));
        // The legacy keys live outside the X264 section: reload on any change of the transcoding settings.
        services.AddSingleton<IOptionsChangeTokenSource<X264Options>>(new ConfigurationChangeTokenSource<X264Options>(transcoding));
        services.AddSingleton<IVideoPipeline, X264Pipeline>();
        return services;
    }
}
