namespace JellyServer.Transcoding;

/// <summary>
/// One way of producing the video track of an HLS job: stream copy, a software encoder, or a
/// hardware encoder. A pipeline owns the whole video path (decode → filters → encode),
/// because hardware decoders, filters and encoders must share one memory domain.
/// The core assembles everything else: input, stream mapping, audio and the HLS muxer.
/// </summary>
public interface IVideoPipeline
{
    /// <summary>Name used in configuration (<c>Transcoding:Pipelines</c>), logs and status.</summary>
    string Id { get; }

    /// <summary>Codecs this pipeline can encode to; empty for stream copy. Read after <see cref="ProbeAsync"/>.</summary>
    IReadOnlySet<string> OutputCodecs { get; }

    /// <summary>Simultaneous jobs this pipeline may run; zero or less means unlimited.</summary>
    int MaxConcurrent { get; }

    /// <summary>
    /// Software encoders return true: they accept frames from an <see cref="IVideoDecoder"/>
    /// (<see cref="VideoJob.Decoded"/>), i.e. hardware decoding with software encoding.
    /// </summary>
    bool AcceptsDecodedInput { get; }

    /// <summary>
    /// Runs at startup and again whenever transcoding settings change. A pipeline that returns false is
    /// not used. Implementations must publish their probed state atomically: jobs may call
    /// <see cref="CanHandle"/>/<see cref="Build"/> concurrently with a re-probe.
    /// </summary>
    Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct);

    /// <summary>Whether this pipeline can produce <see cref="VideoJob.Target"/> from the job's source with its processing.</summary>
    bool CanHandle(VideoJob job);

    /// <summary>
    /// Video arguments for one ffmpeg run. Keyframes must fall exactly on segment boundaries
    /// (<see cref="SegmentTiming.SegmentSeconds"/>): the playlist is built before encoding starts.
    /// </summary>
    VideoArgs Build(VideoJob job);
}

/// <summary>
/// The hardware decode stage of a module, used when frames are decoded (and processed) on a GPU
/// but encoded in software. Registered by hardware modules next to their <see cref="IVideoPipeline"/>,
/// under the same <see cref="Id"/>.
/// </summary>
public interface IVideoDecoder
{
    string Id { get; }

    /// <summary>Same rules as <see cref="IVideoPipeline.ProbeAsync"/>.</summary>
    Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct);

    /// <summary>Whether the source decodes on this GPU under the job's <see cref="VideoProcessing.HwDecode"/> settings.</summary>
    bool CanDecode(VideoJob job);

    /// <summary>
    /// Input arguments plus a filter chain that ends with frames in system memory as 8-bit 4:2:0
    /// (<c>nv12</c> or <c>yuv420p</c>), already scaled to <see cref="VideoTarget.MaxHeight"/>, deinterlaced
    /// when the source is interlaced, and tone-mapped to SDR when the source is HDR and
    /// <see cref="ToneMapping.Enabled"/> is set. The software encoder only encodes them.
    /// </summary>
    DecodedVideo Build(VideoJob job);
}

public sealed record DecodedVideo(string DecoderId, IReadOnlyList<string> Input, IReadOnlyList<string> Filters);

/// <summary>Everything a pipeline needs for one ffmpeg run.</summary>
/// <param name="Decoded">Set only for software encoders when a hardware decoder handles the source.</param>
public sealed record VideoJob(VideoSource Source, VideoTarget Target, VideoProcessing Processing, SegmentTiming Timing,
    DecodedVideo? Decoded = null);

/// <summary>The first video stream of the input. Codec names are normalised (h264, hevc, …).</summary>
/// <param name="FrameRate">Frames per second, 0 when unknown.</param>
/// <param name="Interlaced">True for interlaced or telecined sources (ffprobe field_order other than progressive).</param>
public sealed record VideoSource(string Codec, int Width, int Height, int BitDepth, string? HdrType, string? Profile,
    double FrameRate = 0, bool Interlaced = false)
{
    public bool IsHdr => !string.IsNullOrEmpty(HdrType);
}

/// <summary>What the client needs: the source copied as-is, or re-encoded to <see cref="Codec"/> (h264, hevc, av1).</summary>
/// <param name="MaxHeight">Taller sources are scaled down to this height (even width, aspect kept); 0 keeps the size.</param>
public sealed record VideoTarget(bool Copy, string Codec, string SegmentContainer, long? MaxBitrate, int MaxHeight = 1080);

public sealed record SegmentTiming(int SegmentSeconds);

/// <param name="Input">Placed before <c>-i</c> (e.g. <c>-hwaccel</c>, <c>-noaccurate_seek</c>).</param>
/// <param name="Output">Placed after stream mapping (filters, <c>-c:v</c>, rate control, keyframes).</param>
public sealed record VideoArgs(IReadOnlyList<string> Input, IReadOnlyList<string> Output);

/// <summary>
/// Server-wide processing settings (the Jellyfin dashboard's Transcoding page), captured per job.
/// Defaults mean "use the module's own configuration".
/// </summary>
public sealed record VideoProcessing
{
    public static readonly VideoProcessing Default = new();

    public ToneMapping ToneMap { get; init; } = new();
    public VppToneMapping VppToneMap { get; init; } = new();
    public Deinterlacing Deinterlace { get; init; } = new();
    public HardwareDecoding HwDecode { get; init; } = new();
    public HardwareEncoding HwEncode { get; init; } = new();
    public SoftwareEncoding Software { get; init; } = new();
}

/// <summary>
/// HDR → SDR. <see cref="Enabled"/> governs hardware pipelines (Jellyfin's EnableTonemapping): when false they
/// encode HDR without tone mapping. Software encoders always tone-map. Values follow Jellyfin's EncodingOptions.
/// </summary>
/// <param name="Algorithm">none, clip, linear, gamma, reinhard, hable, mobius, bt2390.</param>
/// <param name="Mode">auto, max, rgb, lum, itp.</param>
/// <param name="Range">auto, tv, pc.</param>
public sealed record ToneMapping(bool Enabled = true, string Algorithm = "bt2390", string Mode = "auto", string Range = "auto",
    double Desat = 0, double Peak = 100, double Param = 0);

/// <summary>Intel VPP tone mapping for qsv/vaapi (Jellyfin's EnableVppTonemapping), preferred over <see cref="ToneMapping"/> when on.</summary>
public sealed record VppToneMapping(bool Enabled = false, double Brightness = 16, double Contrast = 1);

/// <param name="Method">yadif or bwdif.</param>
/// <param name="DoubleRate">One output frame per field (50/60 fps from 25/30i).</param>
public sealed record Deinterlacing(string Method = "yadif", bool DoubleRate = false);

/// <summary>Which sources the selected hardware may decode. Null <see cref="Codecs"/> means every codec the module supports.</summary>
public sealed record HardwareDecoding(IReadOnlySet<string>? Codecs = null, bool Hevc10Bit = true, bool Vp9_10Bit = true,
    bool HevcRext10Bit = false, bool HevcRext12Bit = false, bool EnhancedNvdec = true, bool PreferNativeDecoder = true)
{
    public bool Allows(string codec) => Codecs is null || Codecs.Contains(codec);
}

/// <summary>Null means "the module decides" (e.g. VAAPI probes for low-power support).</summary>
public sealed record HardwareEncoding(bool? LowPowerH264 = null, bool? LowPowerHevc = null);

/// <param name="Preset">auto, veryslow … ultrafast; "auto" keeps the module's configured preset.</param>
/// <param name="H264Crf">Null keeps the module's configured quality.</param>
/// <param name="H265Crf">Used for software HEVC and AV1, as in Jellyfin.</param>
/// <param name="Threads">-1 auto, 0 max, otherwise a thread count.</param>
public sealed record SoftwareEncoding(string Preset = "auto", int? H264Crf = null, int? H265Crf = null, int Threads = -1);

/// <summary>What the installed ffmpeg supports, detected at startup.</summary>
public interface IFfmpegCapabilities
{
    string FfmpegPath { get; }
    bool HasEncoder(string name);
    bool HasDecoder(string name);
    bool HasFilter(string name);
    bool HasHwAccel(string name);

    /// <summary>
    /// Runs ffmpeg with <paramref name="args"/> (e.g. a one-frame test encode) and reports whether
    /// it exited cleanly. Listing an encoder does not prove the hardware behind it works.
    /// </summary>
    Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct);
}
