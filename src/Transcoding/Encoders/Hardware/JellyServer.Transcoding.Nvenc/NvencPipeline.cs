using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.Nvenc;

/// <summary>
/// NVIDIA: NVDEC (or cuvid) decode → CUDA deinterlace/scale/tone map → NVENC (h264, hevc, av1), following
/// Jellyfin's nvenc filter chain. The same probe serves the decode stage for software encoders, which ends
/// with <c>hwdownload</c>. Sources NVDEC can't take are decoded on the CPU; output is always 8-bit 4:2:0.
/// </summary>
internal sealed class NvencPipeline(IOptionsMonitor<NvencOptions> options, ILogger<NvencPipeline> log) : IVideoPipeline, IVideoDecoder
{
    private static readonly (string Codec, string Encoder)[] Encoders =
        [("h264", "h264_nvenc"), ("hevc", "hevc_nvenc"), ("av1", "av1_nvenc")];

    // NVDEC candidates, each proven by decoding a sample made with the first encoder ffmpeg has: support differs
    // between GPU generations (AV1 needs Ampere, HEVC 4:2:2 Blackwell). Key = what DecodeKey() asks for.
    private static readonly (string Key, string[] Encoders, string PixFmt, string? Cuvid)[] Samples =
    [
        ("h264", ["libx264", "h264_nvenc"], "yuv420p", "h264_cuvid"),
        ("hevc", ["libx265", "hevc_nvenc"], "yuv420p10le", "hevc_cuvid"),
        ("hevc-rext10", ["libx265"], "yuv444p10le", null),
        ("hevc-rext12", ["libx265"], "yuv420p12le", null),
        ("hevc-422", ["libx265"], "yuv422p10le", null),
        ("vp9", ["libvpx-vp9"], "yuv420p", "vp9_cuvid"),
        ("av1", ["libsvtav1", "libaom-av1"], "yuv420p", "av1_cuvid"),
        ("mpeg2video", ["mpeg2video"], "yuv420p", "mpeg2_cuvid"),
        ("mpeg4", ["mpeg4"], "yuv420p", "mpeg4_cuvid"),
        ("vp8", ["libvpx"], "yuv420p", "vp8_cuvid"),
    ];

    private static readonly Dictionary<string, string> CuvidDecoders = new()
    {
        ["h264"] = "h264_cuvid", ["hevc"] = "hevc_cuvid", ["vp9"] = "vp9_cuvid", ["av1"] = "av1_cuvid",
        ["mpeg2video"] = "mpeg2_cuvid", ["mpeg4"] = "mpeg4_cuvid", ["vp8"] = "vp8_cuvid", ["vc1"] = "vc1_cuvid",
    };

    private static readonly string[] TestSource =
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=1", "-frames:v", "1"];

    private const string Hdr10Params = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc";
    private const string HlgParams = "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc";
    private const string SdrParams = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709";

    /// <summary>One probe result, swapped in as a whole.</summary>
    /// <param name="Nvdec">Proven <c>-hwaccel cuda</c> decode keys.</param>
    /// <param name="Cuvid">Proven cuvid decode keys (codec names).</param>
    private sealed record State(int Gpu, IReadOnlySet<string> Codecs, bool CudaFilters, bool ToneMap, bool Yadif, bool Bwdif,
        IReadOnlySet<string> Nvdec, IReadOnlySet<string> Cuvid)
    {
        public static readonly State None = new(0, new HashSet<string>(), false, false, false, false, new HashSet<string>(), new HashSet<string>());
    }

    private volatile State _state = State.None;
    private readonly Lock _gate = new();
    private (IFfmpegCapabilities Ffmpeg, int Gpu, Task<State> Task, long At)? _probe;

    public string Id => "nvenc";
    public IReadOnlySet<string> OutputCodecs => _state.Codecs;
    public int MaxConcurrent => options.CurrentValue.MaxConcurrent;
    public bool AcceptsDecodedInput => false;

    public async Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await ProbeSharedAsync(ffmpeg, ct)).Codecs.Count > 0;

    async Task<bool> IVideoDecoder.ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        var s = await ProbeSharedAsync(ffmpeg, ct);
        return s.CudaFilters && s.Nvdec.Count > 0;
    }

    /// <summary>
    /// The selector probes the pipeline and the decoder one after the other: both share one run per ffmpeg
    /// and GPU for a minute.
    /// </summary>
    private async Task<State> ProbeSharedAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        var gpu = options.CurrentValue.Gpu;
        Task<State> task;
        lock (_gate)
        {
            if (_probe is { } p && p.Ffmpeg == ffmpeg && p.Gpu == gpu && !p.Task.IsFaulted && !p.Task.IsCanceled
                && Environment.TickCount64 - p.At < 60_000)
                task = p.Task;
            else
            {
                task = ProbeCoreAsync(ffmpeg, gpu, ct);
                _probe = (ffmpeg, gpu, task, Environment.TickCount64);
            }
        }
        var state = await task;
        _state = state;
        return state;
    }

    private async Task<State> ProbeCoreAsync(IFfmpegCapabilities ffmpeg, int gpu, CancellationToken ct)
    {
        if (!ffmpeg.HasHwAccel("cuda")) return State.None with { Gpu = gpu };
        var g = Num(gpu);

        // A listed encoder only means ffmpeg was built with it: encode a real frame on the configured GPU.
        var codecs = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
            if (ffmpeg.HasEncoder(encoder) && await ffmpeg.TryRunAsync([.. TestSource, "-c:v", encoder, "-gpu", g, "-f", "null", "-"], ct))
                codecs.Add(codec);

        var upload = $"hwupload_cuda=device={g}";
        var cudaFilters = ffmpeg.HasFilter("scale_cuda") && ffmpeg.HasFilter("hwupload_cuda") && ffmpeg.HasFilter("hwdownload")
            && await ffmpeg.TryRunAsync([.. TestSource, "-vf", $"format=nv12,{upload},scale_cuda=w=160:h=120:format=yuv420p,hwdownload,format=yuv420p",
                "-f", "null", "-"], ct);
        if (!cudaFilters) log.LogWarning("CUDA filters do not work with this ffmpeg: NVENC jobs decode and scale on the CPU, HDR is left to other pipelines");

        var toneMap = cudaFilters && ffmpeg.HasFilter("tonemap_cuda") && await ffmpeg.TryRunAsync(
            [.. TestSource, "-vf", $"format=p010le,{Hdr10Params},{upload},{ToneMapFilter(new ToneMapping())},hwdownload,format=yuv420p",
                "-f", "null", "-"], ct);
        if (cudaFilters && !toneMap) log.LogInformation("ffmpeg has no working tonemap_cuda: HDR sources are tone-mapped by other pipelines");

        var (nvdec, cuvid) = cudaFilters ? await ProbeDecodersAsync(ffmpeg, g, ct) : (new HashSet<string>(), new HashSet<string>());
        var state = new State(gpu, codecs, cudaFilters, toneMap, ffmpeg.HasFilter("yadif_cuda"), ffmpeg.HasFilter("bwdif_cuda"), nvdec, cuvid);
        log.LogInformation("NVIDIA GPU {Gpu}: encodes {Codecs}; NVDEC for {Nvdec}; cuvid for {Cuvid}; tone mapping {ToneMap}", gpu,
            codecs.Count > 0 ? string.Join(", ", codecs) : "nothing", nvdec.Count > 0 ? string.Join(", ", nvdec) : "nothing",
            cuvid.Count > 0 ? string.Join(", ", cuvid) : "nothing", toneMap ? "on the GPU" : "unavailable");
        return state;
    }

    /// <summary>Decode keys NVDEC (and cuvid) turned into CUDA frames that scale_cuda accepted.</summary>
    private static async Task<(HashSet<string> Nvdec, HashSet<string> Cuvid)> ProbeDecodersAsync(IFfmpegCapabilities ffmpeg, string gpu, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("jellyserver-nvenc-");
        try
        {
            var results = await Task.WhenAll(Samples.Select(async d =>
            {
                if (d.Encoders.FirstOrDefault(ffmpeg.HasEncoder) is not { } encoder) return (d.Key, Nvdec: false, Cuvid: false);
                var sample = Path.Combine(dir.FullName, d.Key + ".mkv");
                if (!await ffmpeg.TryRunAsync([.. TestSource, "-c:v", encoder, "-pix_fmt", d.PixFmt, "-y", sample], ct)) return (d.Key, Nvdec: false, Cuvid: false);
                // If NVDEC can't take the stream ffmpeg silently decodes on the CPU, and scale_cuda then fails.
                string[] tail = ["-i", sample, "-vf", "scale_cuda=format=yuv420p", "-f", "null", "-"];
                var nvdec = await ffmpeg.TryRunAsync(["-hide_banner", "-loglevel", "error", .. NvdecInput(gpu), .. tail], ct);
                var cuvid = d.Cuvid is { } c && ffmpeg.HasDecoder(c)
                    && await ffmpeg.TryRunAsync(["-hide_banner", "-loglevel", "error", .. CuvidInput(gpu, c), .. tail], ct);
                return (d.Key, Nvdec: nvdec, Cuvid: cuvid);
            }));
            return (results.Where(r => r.Nvdec).Select(r => r.Key).ToHashSet(), results.Where(r => r.Cuvid).Select(r => r.Key).ToHashSet());
        }
        finally
        {
            try { dir.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // One decoder thread: NVDEC doesn't thread, frame threads only add surfaces, and more than 32 fail (seen with small H.264).
    private static string[] NvdecInput(string gpu) =>
        ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", gpu, "-noautorotate", "-threads", "1"];

    private static string[] CuvidInput(string gpu, string decoder) =>
        ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", gpu, "-noautorotate", "-c:v", decoder];

    public bool CanHandle(VideoJob job)
    {
        var s = _state;
        var t = job.Target;
        return !t.Copy && s.Codecs.Contains(t.Codec)
            && (t.Codec != "av1" || t.SegmentContainer == "mp4") // AV1 only in fMP4 segments
            && (!ToneMaps(job) || s.ToneMap); // without tonemap_cuda the next pipeline tone-maps on the CPU
    }

    public bool CanDecode(VideoJob job)
    {
        var s = _state;
        return GpuDecoder(job, s) is not null && (!ToneMaps(job) || s.ToneMap);
    }

    public VideoArgs Build(VideoJob job)
    {
        var s = _state;
        var (input, filters) = Frames(job, s, download: false);
        var codec = job.Target.Codec;
        var encoder = Encoders.First(e => e.Codec == codec).Encoder;
        var seg = job.Timing.SegmentSeconds;
        var args = new List<string>();
        if (filters.Count > 0) args.AddRange(["-vf", string.Join(',', filters)]);
        args.AddRange(["-c:v", encoder, "-gpu", Num(s.Gpu), "-preset", Preset(job.Processing.Software.Preset),
            // Constant quality: with -b:v 0 only -cq (and -maxrate, if set) decides the bitrate.
            "-rc", "vbr", "-cq", Num(options.CurrentValue.Cq), "-b:v", "0"]);
        if (codec != "av1") args.AddRange(["-profile:v", codec == "hevc" ? "main" : "high"]); // av1_nvenc has no profile option
        // Keyframes exactly on segment boundaries, as IDR frames, and no extra ones at scene cuts.
        args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{seg})", "-forced-idr", "1", "-no-scenecut", "1"]);
        if (codec == "hevc" && job.Target.SegmentContainer == "mp4") args.AddRange(["-tag:v", "hvc1"]); // Apple players reject hev1
        if (job.Target.MaxBitrate is > 0 and var b)
        {
            var max = Math.Min(b * 9 / 10, 40_000_000);
            args.AddRange(["-maxrate", Num(max), "-bufsize", Num(max * 2)]);
        }
        return new VideoArgs(input, args);
    }

    DecodedVideo IVideoDecoder.Build(VideoJob job)
    {
        var (input, filters) = Frames(job, _state, download: true);
        return new DecodedVideo(Id, input, filters);
    }

    /// <summary>Jellyfin's EncoderPreset → NVENC: veryslow…faster = p7…p2, faster presets p1, auto keeps the configured one.</summary>
    private string Preset(string preset) => preset.ToLowerInvariant() switch
    {
        "auto" or "" => options.CurrentValue.Preset,
        "veryslow" => "p7",
        "slower" => "p6",
        "slow" => "p5",
        "medium" => "p4",
        "fast" => "p3",
        "faster" => "p2",
        _ => "p1",
    };

    /// <summary>
    /// Input arguments and the filter chain (Jellyfin's GetNvidiaVidFiltersPrefered): colour tags, deinterlace,
    /// scale, tone map; <paramref name="download"/> ends in system memory for a software encoder.
    /// </summary>
    private (List<string> Input, List<string> Filters) Frames(VideoJob job, State s, bool download)
    {
        var src = job.Source;
        var toneMap = ToneMaps(job) && s.ToneMap;
        var size = ScaledSize(src, job.Target.MaxHeight);
        var deint = src.Interlaced;
        var doubleRate = job.Processing.Deinterlace.DoubleRate && (src.FrameRate > 0 ? src.FrameRate : 60) <= 30;
        var rate = doubleRate ? "1" : "0";
        var bwdif = job.Processing.Deinterlace.Method.Equals("bwdif", StringComparison.OrdinalIgnoreCase);

        // Tag the input as HDR for tonemap_cuda, or the output as BT.709 (HDR without tone mapping included, as Jellyfin).
        var filters = new List<string> { toneMap ? (src.HdrType == "HLG" ? HlgParams : Hdr10Params) : SdrParams };
        var input = new List<string>();
        var gpu = Num(s.Gpu);
        if (GpuDecoder(job, s) is { } decoder)
        {
            input.AddRange(decoder.Length == 0 ? NvdecInput(gpu) : CuvidInput(gpu, decoder));
            if (deint) filters.Add($"{(bwdif && s.Bwdif ? "bwdif" : "yadif")}_cuda={rate}:-1:0");
            // Tone mapping reads 10-bit; HEVC RExt surfaces (4:4:4, 12-bit) are converted to p010 first.
            var format = toneMap ? (IsRext(src) ? "p010" : null) : "yuv420p";
            var scale = new List<string>();
            if (size is (var w, var h)) scale.AddRange([$"w={Num(w)}", $"h={Num(h)}"]);
            if (format is not null) scale.Add($"format={format}");
            if (scale.Count > 0) filters.Add("scale_cuda=" + string.Join(':', scale));
            if (toneMap) filters.Add(ToneMapFilter(job.Processing.ToneMap));
            if (download) filters.AddRange(["hwdownload", "format=yuv420p"]);
        }
        else
        {
            // CPU decode: deinterlace and scale in software; upload only for tone mapping, NVENC takes system memory itself.
            if (deint) filters.Add($"{(bwdif ? "bwdif" : "yadif")}={rate}:-1:0");
            if (size is (var w, var h)) filters.Add($"scale={Num(w)}:{Num(h)}");
            if (toneMap) filters.AddRange(["format=p010le", $"hwupload_cuda=device={gpu}", ToneMapFilter(job.Processing.ToneMap)]);
            else filters.Add("format=yuv420p");
        }
        return (input, filters);
    }

    /// <summary>Jellyfin's cuda tone mapping: mode only when not auto, param only when set, range only when tv/pc.</summary>
    private static string ToneMapFilter(ToneMapping t)
    {
        var f = $"tonemap_cuda=format=yuv420p:p=bt709:t=bt709:m=bt709:tonemap={t.Algorithm.ToLowerInvariant()}:peak={Num(t.Peak)}:desat={Num(t.Desat)}";
        var mode = t.Mode.ToLowerInvariant();
        if (mode is "max" or "rgb" or "lum" or "itp") f += $":tonemap_mode={mode}";
        if (t.Param != 0) f += $":param={Num(t.Param)}";
        var range = t.Range.ToLowerInvariant();
        if (range is "tv" or "pc") f += $":range={range}";
        return f;
    }

    /// <summary>Hardware pipelines tone-map HDR only when the dashboard enables it; otherwise HDR is encoded as is.</summary>
    private static bool ToneMaps(VideoJob job) => job.Source.IsHdr && job.Processing.ToneMap.Enabled;

    /// <summary>
    /// How this source decodes on the GPU: "" for NVDEC (<c>-hwaccel cuda</c>), a cuvid decoder name when
    /// Enhanced NVDEC is off, null for CPU decoding. Mirrors Jellyfin's GetNvdecVidDecoder and its
    /// 10-bit/RExt switches; only formats the probe proved on this GPU are used.
    /// </summary>
    private static string? GpuDecoder(VideoJob job, State s)
    {
        var src = job.Source;
        var hw = job.Processing.HwDecode;
        if (!s.CudaFilters || !hw.Allows(src.Codec)) return null;
        if (src.Interlaced && !s.Yadif) return null;
        if (DecodeKey(src, hw) is not { } key || !s.Nvdec.Contains(key)) return null;
        if (!hw.EnhancedNvdec && CuvidDecoders.TryGetValue(src.Codec, out var cuvid)
            && s.Cuvid.Contains(src.Codec == "vc1" ? "h264" : src.Codec))
            return cuvid;
        return "";
    }

    private static string? DecodeKey(VideoSource src, HardwareDecoding hw)
    {
        var p = src.Profile ?? "";
        var chroma420 = !p.Contains("4:2:2") && !p.Contains("4:4:4");
        switch (src.Codec)
        {
            case "h264":
                return src.BitDepth <= 8 && chroma420 && !p.Contains("10") ? "h264" : null; // no Hi10P/Hi422/Hi444 on NVDEC
            case "vc1": // every NVDEC generation decodes VC-1; there is no encoder to make a sample with
                return src.BitDepth <= 8 ? "h264" : null;
            case "mpeg2video" or "mpeg4" or "vp8":
                return src.BitDepth <= 8 && chroma420 ? src.Codec : null;
            case "vp9":
                if (src.BitDepth > 10 || p is "Profile 1" or "Profile 3") return null;
                return src.BitDepth < 10 || hw.Vp9_10Bit ? "vp9" : null;
            case "av1":
                return src.BitDepth <= 10 && p is "" or "Main" ? "av1" : null;
            case "hevc":
                if (IsRext(src))
                {
                    if (src.BitDepth > 12 || (src.BitDepth <= 10 ? !hw.HevcRext10Bit : !hw.HevcRext12Bit)) return null;
                    // ffprobe names every RExt stream "Rext": 4:2:2 only shows when the profile string says so.
                    return p.Contains("4:2:2") ? "hevc-422" : src.BitDepth > 10 ? "hevc-rext12" : "hevc-rext10";
                }
                return src.BitDepth <= 8 || hw.Hevc10Bit ? "hevc" : null;
            default:
                return null;
        }
    }

    private static bool IsRext(VideoSource s) =>
        s.Codec == "hevc" && (s.BitDepth > 10 || (s.Profile ?? "") is var p && (p.Contains("Rext", StringComparison.OrdinalIgnoreCase)
            || p.Contains("4:2:2") || p.Contains("4:4:4")));

    /// <summary>Exact even size at <paramref name="maxHeight"/> keeping the aspect ratio; null when no scaling is needed.</summary>
    private static (int W, int H)? ScaledSize(VideoSource s, int maxHeight)
    {
        if (maxHeight <= 0 || s.Height <= maxHeight || s.Width <= 0) return null;
        var h = maxHeight / 2 * 2;
        var w = Math.Max(2, (int)Math.Round(s.Width * (double)h / s.Height / 2) * 2);
        return (w, h);
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Num(double n) => n.ToString(CultureInfo.InvariantCulture);
}
