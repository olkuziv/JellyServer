namespace JellyServer.Configuration;

/// <summary>
/// Bound from the "JellyServer" config section. Every value can be overridden by
/// environment variables, e.g. <c>JellyServer__Playback__StrmMode=proxy</c>. Libraries and users are not configured
/// here: they are created in the startup wizard and the dashboard.
/// </summary>
public sealed class ServerOptions
{
    public const string Section = "JellyServer";

    public string Name { get; set; } = "JellyServer";
    public int Port { get; set; } = 8096;
    public string DataDir { get; set; } = "/data";
    /// <summary>Version reported to clients; jellyfin-web rejects servers below its minimum.</summary>
    public string Version { get; set; } = "10.11.0";
    /// <summary>Address advertised via UDP discovery, e.g. http://192.168.1.10:8096.</summary>
    public string? PublicUrl { get; set; }
    public bool Discovery { get; set; } = true;
    /// <summary>Directory with a jellyfin-web build, served at /web (null/missing disables it).</summary>
    public string? WebDir { get; set; } = "web";
    public int SlowRequestMs { get; set; } = 500;
    public int SlowQueryMs { get; set; } = 200;
    public PlaybackOptions Playback { get; set; } = new();
    public ImageOptions Images { get; set; } = new();
    public TranscodingOptions Transcoding { get; set; } = new();

    public void Validate()
    {
        if (Playback.StrmMode is not ("direct" or "redirect" or "proxy"))
            throw new InvalidOperationException($"Playback.StrmMode must be direct|redirect|proxy, got '{Playback.StrmMode}'");
    }
}

public sealed class PlaybackOptions
{
    /// <summary>
    /// How .strm targets reach the client:
    /// direct   - client plays the remote URL itself (zero server bandwidth),
    /// redirect - client hits /Videos/{id}/stream, server answers 302,
    /// proxy    - server relays the stream (HLS playlists are rewritten).
    /// </summary>
    public string StrmMode { get; set; } = "direct";
    /// <summary>Stopping past this fraction of the runtime marks the item played.</summary>
    public double PlayedThreshold { get; set; } = 0.9;
    /// <summary>Positions below this fraction are not kept for resume.</summary>
    public double MinResumeFraction { get; set; } = 0.05;
}

public sealed class ImageOptions
{
    public bool Resize { get; set; } = true;
    public string? CacheDir { get; set; }
    public int JpegQuality { get; set; } = 85;
    /// <summary>Allow downloading http(s) artwork referenced in NFO files.</summary>
    public bool RemoteFetch { get; set; } = true;
}

/// <summary>
/// ffmpeg-based conversion for clients that cannot play a file as-is (e.g. MKV on iOS).
/// Compatible streams are copied (remux, near-zero CPU); only unsupported ones are re-encoded.
/// </summary>
public sealed class TranscodingOptions
{
    public bool Enabled { get; set; } = true;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    /// <summary>Working directory for HLS segments (defaults to {DataDir}/transcodes).</summary>
    public string? TempDir { get; set; }
    public int SegmentSeconds { get; set; } = 6;
    /// <summary>
    /// Video encoders to use, in order of preference (e.g. <c>["vaapi", "x264"]</c>); unlisted ones stay off.
    /// Null uses every registered encoder. Stream copy (remux) is always available. Each encoder has its
    /// own settings section, e.g. <c>Transcoding:X264</c>.
    /// </summary>
    public string[]? Pipelines { get; set; }
    /// <summary>
    /// Hardware module whose decoder feeds the software encoders (hardware decoding with software encoding).
    /// Null picks the first working hardware module; empty disables it.
    /// </summary>
    public string? Decoder { get; set; }
    /// <summary>Taller sources are scaled down to this height when re-encoded; 0 keeps the size.</summary>
    public int MaxHeight { get; set; } = 1080;
    public int MaxMuxingQueueSize { get; set; } = 4096;
    /// <summary>Let the playback decision pick HEVC / AV1 output when the client accepts it.</summary>
    public bool AllowHevcEncoding { get; set; }
    public bool AllowAv1Encoding { get; set; }
    /// <summary>Kill an ffmpeg process when no segment was requested for this long (e.g. paused).</summary>
    public TimeSpan IdleKillAfter { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Delete a job's segments after this much inactivity.</summary>
    public TimeSpan CleanupAfter { get; set; } = TimeSpan.FromMinutes(10);

    // Server-wide processing settings, edited on the dashboard's Transcoding page (see EncodingSettings).
    public ToneMapOptions ToneMap { get; set; } = new();
    public VppToneMapOptions VppToneMap { get; set; } = new();
    public DeinterlaceOptions Deinterlace { get; set; } = new();
    public HwDecodeOptions HwDecode { get; set; } = new();
    public HwEncodeOptions HwEncode { get; set; } = new();
    public SoftwareEncodeOptions Software { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public ThrottlingOptions Throttling { get; set; } = new();
    public SegmentDeletionOptions SegmentDeletion { get; set; } = new();
    public SubtitleOptions Subtitles { get; set; } = new();
    public FallbackFontOptions FallbackFont { get; set; } = new();

    /// <summary>The per-job snapshot handed to video pipelines.</summary>
    public Transcoding.VideoProcessing ToProcessing() => new()
    {
        ToneMap = new(ToneMap.Enabled, ToneMap.Algorithm, ToneMap.Mode, ToneMap.Range, ToneMap.Desat, ToneMap.Peak, ToneMap.Param),
        VppToneMap = new(VppToneMap.Enabled, VppToneMap.Brightness, VppToneMap.Contrast),
        Deinterlace = new(Deinterlace.Method, Deinterlace.DoubleRate),
        HwDecode = new(
            string.IsNullOrWhiteSpace(HwDecode.Codecs) ? null
                : HwDecode.Codecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
            HwDecode.Hevc10Bit, HwDecode.Vp9_10Bit, HwDecode.HevcRext10Bit, HwDecode.HevcRext12Bit, HwDecode.EnhancedNvdec, HwDecode.PreferNativeDecoder),
        HwEncode = new(HwEncode.LowPowerH264, HwEncode.LowPowerHevc),
        Software = new(Software.Preset, Software.H264Crf, Software.H265Crf, Software.Threads),
    };
}

public sealed class ToneMapOptions
{
    /// <summary>Hardware pipelines only; software encoders always tone-map.</summary>
    public bool Enabled { get; set; } = true;
    public string Algorithm { get; set; } = "bt2390";
    public string Mode { get; set; } = "auto";
    public string Range { get; set; } = "auto";
    public double Desat { get; set; }
    public double Peak { get; set; } = 100;
    public double Param { get; set; }
}

public sealed class VppToneMapOptions
{
    public bool Enabled { get; set; }
    public double Brightness { get; set; } = 16;
    public double Contrast { get; set; } = 1;
}

public sealed class DeinterlaceOptions
{
    public string Method { get; set; } = "yadif";
    public bool DoubleRate { get; set; }
}

public sealed class HwDecodeOptions
{
    /// <summary>Comma-separated codecs the GPU may decode; empty means every codec the module supports.</summary>
    public string? Codecs { get; set; }
    public bool Hevc10Bit { get; set; } = true;
    public bool Vp9_10Bit { get; set; } = true;
    public bool HevcRext10Bit { get; set; }
    public bool HevcRext12Bit { get; set; }
    public bool EnhancedNvdec { get; set; } = true;
    public bool PreferNativeDecoder { get; set; } = true;
}

public sealed class HwEncodeOptions
{
    /// <summary>True forces Intel low-power encoding; null lets the module decide.</summary>
    public bool? LowPowerH264 { get; set; }
    public bool? LowPowerHevc { get; set; }
}

public sealed class SoftwareEncodeOptions
{
    /// <summary>auto keeps each encoder's configured preset.</summary>
    public string Preset { get; set; } = "auto";
    public int? H264Crf { get; set; }
    public int? H265Crf { get; set; }
    public int Threads { get; set; } = -1;
}

public sealed class AudioOptions
{
    public bool Vbr { get; set; }
    public double DownMixBoost { get; set; } = 2;
    /// <summary>None, Dave750, NightmodeDialogue, Rfc7845, Ac4.</summary>
    public string StereoDownmix { get; set; } = "None";
    /// <summary>TrimCopiedAudio or TranscodeAudio.</summary>
    public string SeekStrategy { get; set; } = "TrimCopiedAudio";
}

public sealed class ThrottlingOptions
{
    /// <summary>Pause ffmpeg while it is this far ahead of the player.</summary>
    public bool Enabled { get; set; }
    public int DelaySeconds { get; set; } = 180;
}

public sealed class SegmentDeletionOptions
{
    /// <summary>Delete segments the player has moved past by more than <see cref="KeepSeconds"/>.</summary>
    public bool Enabled { get; set; }
    public int KeepSeconds { get; set; } = 720;
}

public sealed class SubtitleOptions
{
    /// <summary>Extract embedded text subtitles so clients can load them as external files.</summary>
    public bool Extraction { get; set; } = true;
    public int ExtractionTimeoutMinutes { get; set; } = 30;
}

public sealed class FallbackFontOptions
{
    /// <summary>Serve the fonts in <see cref="Path"/> to clients that render ASS/SSA subtitles themselves.</summary>
    public bool Enabled { get; set; }
    public string? Path { get; set; }
}
