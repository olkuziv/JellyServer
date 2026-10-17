using System.Globalization;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.Vaapi;

/// <summary>
/// H.264/HEVC/AV1 on an Intel or AMD GPU through VAAPI. Decoding, deinterlacing, scaling, tone mapping and
/// encoding stay in GPU memory when the source decodes in hardware; other sources are decoded on the CPU and
/// uploaded. Output is always 8-bit 4:2:0. Keyframes rely on ffmpeg turning frames forced by
/// <c>-force_key_frames</c> into IDR pictures, which the VAAPI encoders honour.
/// </summary>
internal sealed class VaapiPipeline(VaapiProbe probe, IOptionsMonitor<VaapiOptions> options) : IVideoPipeline
{
    private static readonly Dictionary<string, string> Encoders = new() { ["h264"] = "h264_vaapi", ["hevc"] = "hevc_vaapi", ["av1"] = "av1_vaapi" };

    public string Id => "vaapi";
    public IReadOnlySet<string> OutputCodecs => probe.Current.Codecs;
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => false;

    public async Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await probe.ProbeAsync(ffmpeg, ct)).Codecs.Count > 0;

    public bool CanHandle(VideoJob job)
    {
        var s = probe.Current;
        return job is { Decoded: null, Target.Copy: false }
            && (job.Target.Codec != "av1" || job.Target.SegmentContainer == "mp4") // AV1 only goes into fMP4 segments
            && LowPower(s, job) is not null
            && VaapiChain.Build(s, job, toMemory: false) is not null;
    }

    public VideoArgs Build(VideoJob job)
    {
        var s = probe.Current;
        var (input, filters) = VaapiChain.Build(s, job, toMemory: false)
            ?? throw new InvalidOperationException("VAAPI cannot apply this job's tone-mapping settings");
        var codec = job.Target.Codec;
        var output = new List<string> { "-vf", string.Join(',', filters), "-c:v", Encoders[codec] };
        if (LowPower(s, job) == true) output.AddRange(["-low_power", "1"]);
        output.AddRange(["-profile:v", codec == "h264" ? "high" : "main"]);
        // -compression_level is only reliable on Intel iHD (Jellyfin's GetEncoderParam).
        if (s.IntelIhd && CompressionLevel(job.Processing.Software.Preset) is { } level) output.AddRange(["-compression_level", level]);

        if (job.Target.MaxBitrate is > 0 and var b)
        {
            // VAAPI has no capped constant quality that Intel and AMD both support: use VBR under the cap.
            var max = Math.Min(b * 9 / 10, 40_000_000);
            output.AddRange(["-rc_mode", "VBR", "-b:v", Num(max / 2), "-maxrate", Num(max), "-bufsize", Num(max * 2)]);
        }
        else
        {
            var qp = options.CurrentValue.Qp;
            // av1_vaapi has no -qp: its constant quality is a 0-255 quantizer index.
            output.AddRange(codec == "av1" ? ["-rc_mode", "CQP", "-global_quality:v", Num(qp * 255 / 51)] : ["-rc_mode", "CQP", "-qp", Num(qp)]);
        }
        if (codec != "av1") output.AddRange(["-sei", "-a53_cc"]); // Jellyfin: avoids "Access unit too large"

        // Keyframes exactly on segment boundaries: forced frames become IDR pictures.
        output.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{Num(job.Timing.SegmentSeconds)})"]);
        if (codec == "hevc")
        {
            // Mesa's in-band parameter sets differ from ffmpeg's extradata, which breaks hvc1: rebuild it from the stream.
            if (s.Amd) output.AddRange(["-flags:v", "-global_header", "-bsf:v", "extract_extradata=remove=0"]);
            if (job.Target.SegmentContainer == "mp4") output.AddRange(["-tag:v", "hvc1"]); // Apple players need hvc1
        }
        return new VideoArgs(input, output);
    }

    /// <summary>
    /// The encoder mode for the job: true = low power. The dashboard switch forces it (true) or forbids it (false);
    /// unset, the normal mode is preferred and low power used where only it passed the test encode.
    /// Null when the requested mode did not pass.
    /// </summary>
    private static bool? LowPower(VaapiState s, VideoJob job)
    {
        var codec = job.Target.Codec;
        var setting = codec switch { "h264" => job.Processing.HwEncode.LowPowerH264, "hevc" => job.Processing.HwEncode.LowPowerHevc, _ => null };
        var normal = s.Normal.Contains(codec);
        var low = s.LowPower.Contains(codec);
        return setting switch
        {
            true => low ? true : null,
            false => normal ? false : null,
            null => normal ? false : low ? true : null,
        };
    }

    private static string? CompressionLevel(string preset) => preset switch
    {
        "veryslow" => "1",
        "slower" => "2",
        "slow" => "3",
        "medium" => "4",
        "fast" => "5",
        "faster" => "6",
        "veryfast" or "superfast" or "ultrafast" => "7",
        _ => null,
    };

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
}
