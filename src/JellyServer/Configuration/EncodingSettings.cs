using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace JellyServer.Configuration;

/// <summary>
/// Jellyfin's <c>EncodingOptions</c> (the dashboard's Transcoding page). Property names, value spellings and
/// defaults follow refs/jellyfin MediaBrowser.Model/Configuration/EncodingOptions.cs.
/// </summary>
public sealed class EncodingOptionsDto
{
    public int EncodingThreadCount { get; set; } = -1;
    public string? TranscodingTempPath { get; set; }
    public string? FallbackFontPath { get; set; }
    public bool EnableFallbackFont { get; set; }
    public bool EnableAudioVbr { get; set; }
    public double DownMixAudioBoost { get; set; } = 2;
    public string DownMixStereoAlgorithm { get; set; } = "None";
    public int MaxMuxingQueueSize { get; set; } = 2048;
    public bool EnableThrottling { get; set; }
    public int ThrottleDelaySeconds { get; set; } = 180;
    public bool EnableSegmentDeletion { get; set; }
    public int SegmentKeepSeconds { get; set; } = 720;
    public string HardwareAccelerationType { get; set; } = "none";
    public string? EncoderAppPath { get; set; }
    public string? EncoderAppPathDisplay { get; set; }
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    public string QsvDevice { get; set; } = "";
    public bool EnableTonemapping { get; set; }
    public bool EnableVppTonemapping { get; set; }
    public bool EnableVideoToolboxTonemapping { get; set; }
    public string TonemappingAlgorithm { get; set; } = "bt2390";
    public string TonemappingMode { get; set; } = "auto";
    public string TonemappingRange { get; set; } = "auto";
    public double TonemappingDesat { get; set; }
    public double TonemappingPeak { get; set; } = 100;
    public double TonemappingParam { get; set; }
    public double VppTonemappingBrightness { get; set; } = 16;
    public double VppTonemappingContrast { get; set; } = 1;
    public int H264Crf { get; set; } = 23;
    public int H265Crf { get; set; } = 28;
    public string EncoderPreset { get; set; } = "auto";
    public bool DeinterlaceDoubleRate { get; set; }
    public string DeinterlaceMethod { get; set; } = "yadif";
    public bool EnableDecodingColorDepth10Hevc { get; set; } = true;
    public bool EnableDecodingColorDepth10Vp9 { get; set; } = true;
    public bool EnableDecodingColorDepth10HevcRext { get; set; }
    public bool EnableDecodingColorDepth12HevcRext { get; set; }
    public bool EnableEnhancedNvdecDecoder { get; set; } = true;
    public bool PreferSystemNativeHwDecoder { get; set; } = true;
    public bool EnableIntelLowPowerH264HwEncoder { get; set; }
    public bool EnableIntelLowPowerHevcHwEncoder { get; set; }
    public bool EnableHardwareEncoding { get; set; } = true;
    public bool AllowHevcEncoding { get; set; }
    public bool AllowAv1Encoding { get; set; }
    public bool EnableSubtitleExtraction { get; set; } = true;
    public int SubtitleExtractionTimeoutMinutes { get; set; } = 30;
    public string[] AllowOnDemandMetadataBasedKeyframeExtractionForExtensions { get; set; } = ["mkv"];
    public string[] HardwareDecodingCodecs { get; set; } = ["h264", "vc1"];
    public string HlsAudioSeekStrategy { get; set; } = "TrimCopiedAudio";
}

/// <summary>Reads, validates and translates the dashboard's encoding settings into <see cref="TranscodingOptions"/> keys.</summary>
public static class EncodingSettings
{
    public const string Key = "encoding";

    public static readonly string[] HardwareTypes = ["none", "amf", "qsv", "nvenc", "v4l2m2m", "vaapi", "videotoolbox", "rkmpp"];
    public static readonly string[] SoftwarePipelines = ["x264", "x265", "svtav1"];
    public static readonly string[] AllDecodeCodecs = ["h264", "hevc", "mpeg1video", "mpeg2video", "mpeg4", "vc1", "vp8", "vp9", "av1"];
    private static readonly string[] Presets = ["auto", "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast", "ultrafast"];
    private static readonly string[] Algorithms = ["none", "clip", "linear", "gamma", "reinhard", "hable", "mobius", "bt2390"];
    private static readonly string[] Modes = ["auto", "max", "rgb", "lum", "itp"];
    private static readonly string[] Ranges = ["auto", "tv", "pc"];
    private static readonly string[] Deinterlacers = ["yadif", "bwdif"];
    private static readonly string[] Downmixes = ["None", "Dave750", "NightmodeDialogue", "Rfc7845", "Ac4"];
    private static readonly string[] SeekStrategies = ["TrimCopiedAudio", "TranscodeAudio"];

    private const string Prefix = ServerOptions.Section + ":Transcoding:";
    private const int PipelineSlots = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly PropertyInfo[] Properties = typeof(EncodingOptionsDto).GetProperties();

    public static EncodingOptionsDto Deserialize(string json) => Overlay(new EncodingOptionsDto(), JsonDocument.Parse(json).RootElement);

    public static string Serialize(EncodingOptionsDto dto) => JsonSerializer.Serialize(dto, Json);

    /// <summary>
    /// Copies every known property present in <paramref name="body"/> onto <paramref name="target"/>. Lenient like
    /// Jellyfin: the dashboard form posts numbers as strings and cleared number fields as "".
    /// </summary>
    public static EncodingOptionsDto Overlay(EncodingOptionsDto target, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new FormatException("Body must be a JSON object");
        foreach (var json in body.EnumerateObject())
        {
            var prop = Properties.FirstOrDefault(p => p.Name.Equals(json.Name, StringComparison.OrdinalIgnoreCase));
            if (prop is null || !prop.CanWrite) continue;
            var v = json.Value;
            object? value = prop.PropertyType switch
            {
                var t when t == typeof(bool) => v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
                    _ => null,
                },
                var t when t == typeof(int) => Number(v) is { } d ? (int)Math.Round(d) : null,
                var t when t == typeof(double) => Number(v),
                var t when t == typeof(string[]) => v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                    : null,
                _ => v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
                    _ => null,
                },
            };
            if (value is not null) prop.SetValue(target, value); // null or unparsable keeps the current value
        }
        return target;
    }

    private static double? Number(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null, // "" from a cleared field keeps the current value
    };

    /// <summary>Problems that make the settings unusable; empty when they can be saved.</summary>
    public static List<string> Validate(EncodingOptionsDto o)
    {
        var errors = new List<string>();
        void OneOf(string name, string value, string[] allowed, StringComparison cmp = StringComparison.OrdinalIgnoreCase)
        {
            if (!allowed.Contains(value, StringComparer.FromComparison(cmp))) errors.Add($"{name} must be one of {string.Join(", ", allowed)}");
        }
        void Range(string name, double value, double min, double max)
        {
            if (value < min || value > max) errors.Add($"{name} must be between {min} and {max}");
        }

        OneOf(nameof(o.HardwareAccelerationType), o.HardwareAccelerationType, HardwareTypes);
        OneOf(nameof(o.EncoderPreset), o.EncoderPreset, Presets);
        OneOf(nameof(o.TonemappingAlgorithm), o.TonemappingAlgorithm, Algorithms);
        OneOf(nameof(o.TonemappingMode), o.TonemappingMode, Modes);
        OneOf(nameof(o.TonemappingRange), o.TonemappingRange, Ranges);
        OneOf(nameof(o.DeinterlaceMethod), o.DeinterlaceMethod, Deinterlacers);
        OneOf(nameof(o.DownMixStereoAlgorithm), o.DownMixStereoAlgorithm, Downmixes);
        OneOf(nameof(o.HlsAudioSeekStrategy), o.HlsAudioSeekStrategy, SeekStrategies);
        Range(nameof(o.EncodingThreadCount), o.EncodingThreadCount, -1, 256);
        Range(nameof(o.H264Crf), o.H264Crf, 0, 51);
        Range(nameof(o.H265Crf), o.H265Crf, 0, 51);
        Range(nameof(o.DownMixAudioBoost), o.DownMixAudioBoost, 0.5, 3);
        Range(nameof(o.MaxMuxingQueueSize), o.MaxMuxingQueueSize, 128, 1 << 20);
        Range(nameof(o.ThrottleDelaySeconds), o.ThrottleDelaySeconds, 10, 3600);
        Range(nameof(o.SegmentKeepSeconds), o.SegmentKeepSeconds, 10, 3600);
        Range(nameof(o.VppTonemappingBrightness), o.VppTonemappingBrightness, 0, 100);
        Range(nameof(o.VppTonemappingContrast), o.VppTonemappingContrast, 1, 2);
        Range(nameof(o.TonemappingPeak), o.TonemappingPeak, 0, 10_000);
        if (o.TonemappingDesat < 0) errors.Add($"{nameof(o.TonemappingDesat)} must not be negative");
        if (o.TonemappingParam < 0) errors.Add($"{nameof(o.TonemappingParam)} must not be negative");
        if (o.SubtitleExtractionTimeoutMinutes < 1) errors.Add($"{nameof(o.SubtitleExtractionTimeoutMinutes)} must be at least 1");
        foreach (var c in o.HardwareDecodingCodecs.Where(c => !AllDecodeCodecs.Contains(c, StringComparer.OrdinalIgnoreCase)))
            errors.Add($"Unknown codec in {nameof(o.HardwareDecodingCodecs)}: {c}");
        if (!string.IsNullOrWhiteSpace(o.TranscodingTempPath) && !IsWritableDirectory(o.TranscodingTempPath))
            errors.Add($"{nameof(o.TranscodingTempPath)} is not a writable directory: {o.TranscodingTempPath}");
        if (o.EnableFallbackFont && (string.IsNullOrWhiteSpace(o.FallbackFontPath) || !Directory.Exists(o.FallbackFontPath)))
            errors.Add($"{nameof(o.FallbackFontPath)} must be an existing directory when the fallback font is enabled");
        return errors;
    }

    private static bool IsWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".jellyserver-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The configuration keys (under <c>JellyServer:Transcoding</c>) these settings stand for.</summary>
    public static Dictionary<string, string?> ToConfiguration(EncodingOptionsDto o)
    {
        var hw = o.HardwareAccelerationType.ToLowerInvariant();
        var hardware = hw != "none" && HardwareTypes.Contains(hw);
        // Pipelines is an array: fill a fixed number of slots so entries from appsettings never leak through.
        var pipelines = (hardware && o.EnableHardwareEncoding ? [hw, .. SoftwarePipelines] : SoftwarePipelines).ToList();
        var c = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < PipelineSlots; i++) c[$"{Prefix}Pipelines:{i}"] = i < pipelines.Count ? pipelines[i] : "";
        c[Prefix + "Decoder"] = hardware ? hw : "";

        if (!string.IsNullOrWhiteSpace(o.TranscodingTempPath)) c[Prefix + "TempDir"] = o.TranscodingTempPath;
        c[Prefix + "MaxMuxingQueueSize"] = Str(o.MaxMuxingQueueSize);
        c[Prefix + "AllowHevcEncoding"] = Str(o.AllowHevcEncoding);
        c[Prefix + "AllowAv1Encoding"] = Str(o.AllowAv1Encoding);
        c[Prefix + "Vaapi:Device"] = o.VaapiDevice;
        if (!string.IsNullOrWhiteSpace(o.QsvDevice)) c[Prefix + "Qsv:Device"] = o.QsvDevice;

        // VideoToolbox has its own tone-mapping switch on the page; every other hardware type uses EnableTonemapping.
        c[Prefix + "ToneMap:Enabled"] = Str(hw == "videotoolbox" ? o.EnableVideoToolboxTonemapping : o.EnableTonemapping);
        c[Prefix + "ToneMap:Algorithm"] = o.TonemappingAlgorithm.ToLowerInvariant();
        c[Prefix + "ToneMap:Mode"] = o.TonemappingMode.ToLowerInvariant();
        c[Prefix + "ToneMap:Range"] = o.TonemappingRange.ToLowerInvariant();
        c[Prefix + "ToneMap:Desat"] = Str(o.TonemappingDesat);
        c[Prefix + "ToneMap:Peak"] = Str(o.TonemappingPeak);
        c[Prefix + "ToneMap:Param"] = Str(o.TonemappingParam);
        c[Prefix + "VppToneMap:Enabled"] = Str(o.EnableVppTonemapping);
        c[Prefix + "VppToneMap:Brightness"] = Str(o.VppTonemappingBrightness);
        c[Prefix + "VppToneMap:Contrast"] = Str(o.VppTonemappingContrast);
        c[Prefix + "Deinterlace:Method"] = o.DeinterlaceMethod.ToLowerInvariant();
        c[Prefix + "Deinterlace:DoubleRate"] = Str(o.DeinterlaceDoubleRate);

        // An empty list must mean "no codec", not "every codec": "none" matches nothing.
        c[Prefix + "HwDecode:Codecs"] = o.HardwareDecodingCodecs.Length == 0 ? "none" : string.Join(',', o.HardwareDecodingCodecs);
        c[Prefix + "HwDecode:Hevc10Bit"] = Str(o.EnableDecodingColorDepth10Hevc);
        c[Prefix + "HwDecode:Vp9_10Bit"] = Str(o.EnableDecodingColorDepth10Vp9);
        c[Prefix + "HwDecode:HevcRext10Bit"] = Str(o.EnableDecodingColorDepth10HevcRext);
        c[Prefix + "HwDecode:HevcRext12Bit"] = Str(o.EnableDecodingColorDepth12HevcRext);
        c[Prefix + "HwDecode:EnhancedNvdec"] = Str(o.EnableEnhancedNvdecDecoder);
        c[Prefix + "HwDecode:PreferNativeDecoder"] = Str(o.PreferSystemNativeHwDecoder);
        // Unticked means "not forced": the module may still detect that only low-power mode works.
        c[Prefix + "HwEncode:LowPowerH264"] = o.EnableIntelLowPowerH264HwEncoder ? "true" : null;
        c[Prefix + "HwEncode:LowPowerHevc"] = o.EnableIntelLowPowerHevcHwEncoder ? "true" : null;

        c[Prefix + "Software:Preset"] = o.EncoderPreset.ToLowerInvariant();
        c[Prefix + "Software:H264Crf"] = Str(o.H264Crf);
        c[Prefix + "Software:H265Crf"] = Str(o.H265Crf);
        c[Prefix + "Software:Threads"] = Str(o.EncodingThreadCount);

        c[Prefix + "Audio:Vbr"] = Str(o.EnableAudioVbr);
        c[Prefix + "Audio:DownMixBoost"] = Str(o.DownMixAudioBoost);
        c[Prefix + "Audio:StereoDownmix"] = o.DownMixStereoAlgorithm;
        c[Prefix + "Audio:SeekStrategy"] = o.HlsAudioSeekStrategy;
        c[Prefix + "Throttling:Enabled"] = Str(o.EnableThrottling);
        c[Prefix + "Throttling:DelaySeconds"] = Str(o.ThrottleDelaySeconds);
        c[Prefix + "SegmentDeletion:Enabled"] = Str(o.EnableSegmentDeletion);
        c[Prefix + "SegmentDeletion:KeepSeconds"] = Str(o.SegmentKeepSeconds);
        c[Prefix + "Subtitles:Extraction"] = Str(o.EnableSubtitleExtraction);
        c[Prefix + "Subtitles:ExtractionTimeoutMinutes"] = Str(o.SubtitleExtractionTimeoutMinutes);
        c[Prefix + "FallbackFont:Enabled"] = Str(o.EnableFallbackFont);
        c[Prefix + "FallbackFont:Path"] = o.FallbackFontPath ?? "";
        return c;
    }

    /// <summary>
    /// What the page shows before anything was saved: the server's actual behaviour, so the first save
    /// doesn't silently turn hardware acceleration off.
    /// </summary>
    public static EncodingOptionsDto FromOptions(TranscodingOptions t, string? activeHardware, string tempDir)
    {
        var s = t.Software;
        return new EncodingOptionsDto
        {
            HardwareAccelerationType = activeHardware ?? "none",
            EnableHardwareEncoding = true,
            HardwareDecodingCodecs = string.IsNullOrWhiteSpace(t.HwDecode.Codecs)
                ? AllDecodeCodecs
                : t.HwDecode.Codecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x != "none").ToArray(),
            EnableDecodingColorDepth10Hevc = t.HwDecode.Hevc10Bit,
            EnableDecodingColorDepth10Vp9 = t.HwDecode.Vp9_10Bit,
            EnableDecodingColorDepth10HevcRext = t.HwDecode.HevcRext10Bit,
            EnableDecodingColorDepth12HevcRext = t.HwDecode.HevcRext12Bit,
            EnableEnhancedNvdecDecoder = t.HwDecode.EnhancedNvdec,
            PreferSystemNativeHwDecoder = t.HwDecode.PreferNativeDecoder,
            EnableIntelLowPowerH264HwEncoder = t.HwEncode.LowPowerH264 == true,
            EnableIntelLowPowerHevcHwEncoder = t.HwEncode.LowPowerHevc == true,
            EnableTonemapping = t.ToneMap.Enabled,
            EnableVideoToolboxTonemapping = t.ToneMap.Enabled,
            TonemappingAlgorithm = t.ToneMap.Algorithm,
            TonemappingMode = t.ToneMap.Mode,
            TonemappingRange = t.ToneMap.Range,
            TonemappingDesat = t.ToneMap.Desat,
            TonemappingPeak = t.ToneMap.Peak,
            TonemappingParam = t.ToneMap.Param,
            EnableVppTonemapping = t.VppToneMap.Enabled,
            VppTonemappingBrightness = t.VppToneMap.Brightness,
            VppTonemappingContrast = t.VppToneMap.Contrast,
            DeinterlaceMethod = t.Deinterlace.Method,
            DeinterlaceDoubleRate = t.Deinterlace.DoubleRate,
            EncoderPreset = s.Preset,
            H264Crf = s.H264Crf ?? 23,
            H265Crf = s.H265Crf ?? 28,
            EncodingThreadCount = s.Threads,
            AllowHevcEncoding = t.AllowHevcEncoding,
            AllowAv1Encoding = t.AllowAv1Encoding,
            MaxMuxingQueueSize = t.MaxMuxingQueueSize,
            EnableAudioVbr = t.Audio.Vbr,
            DownMixAudioBoost = t.Audio.DownMixBoost,
            DownMixStereoAlgorithm = t.Audio.StereoDownmix,
            HlsAudioSeekStrategy = t.Audio.SeekStrategy,
            EnableThrottling = t.Throttling.Enabled,
            ThrottleDelaySeconds = t.Throttling.DelaySeconds,
            EnableSegmentDeletion = t.SegmentDeletion.Enabled,
            SegmentKeepSeconds = t.SegmentDeletion.KeepSeconds,
            EnableSubtitleExtraction = t.Subtitles.Extraction,
            SubtitleExtractionTimeoutMinutes = t.Subtitles.ExtractionTimeoutMinutes,
            EnableFallbackFont = t.FallbackFont.Enabled,
            FallbackFontPath = t.FallbackFont.Path,
            TranscodingTempPath = tempDir,
        };
    }

    private static string Str(bool v) => v ? "true" : "false";
    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Str(double v) => v.ToString(CultureInfo.InvariantCulture);
}
