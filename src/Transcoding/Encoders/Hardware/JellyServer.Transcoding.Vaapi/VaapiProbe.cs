using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JellyServer.Transcoding.Vaapi;

/// <summary>
/// Proves what the configured VAAPI device can do by running real one-frame jobs: listed encoders and filters
/// say nothing about the GPU or its driver. Shared by the pipeline and the decoder: both ask in the same
/// selector pass (same <see cref="IFfmpegCapabilities"/>), so the device is probed once per settings change.
/// </summary>
internal sealed class VaapiProbe(IOptionsMonitor<VaapiOptions> options, ILogger<VaapiProbe> log)
{
    private static readonly (string Codec, string Encoder)[] Encoders = [("h264", "h264_vaapi"), ("hevc", "hevc_vaapi"), ("av1", "av1_vaapi")];

    // Decode candidates, each proven on a two-frame sample made with the first software encoder ffmpeg has.
    private static readonly (string Key, string[] Encoders, string PixFmt)[] Samples =
    [
        ("h264", ["libx264"], "yuv420p"),
        ("hevc", ["libx265"], "yuv420p"),
        ("vp9", ["libvpx-vp9"], "yuv420p"),
        ("av1", ["libsvtav1", "libaom-av1"], "yuv420p"),
        ("mpeg2video", ["mpeg2video"], "yuv420p"),
        ("vp8", ["libvpx"], "yuv420p"),
    ];

    // HDR10 Main 10 with mastering metadata (tonemap_vaapi needs it); doubles as the hevc10 decode sample.
    private const string Hdr10Params = "log-level=error:hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"
        + ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400";

    private static readonly string[] Quiet = ["-hide_banner", "-loglevel", "error"];
    private static readonly string[] TestSource = ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25"];

    private readonly Lock _gate = new();
    private (IFfmpegCapabilities Ffmpeg, string Device, Task<VaapiState> Task)? _probe;
    private volatile VaapiState _current = VaapiState.None;

    public VaapiState Current => _current;

    /// <summary>Probes the configured device, or joins the probe already running for this ffmpeg and device.</summary>
    public Task<VaapiState> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct)
    {
        var device = options.CurrentValue.Device;
        lock (_gate)
        {
            if (_probe is { } p && p.Ffmpeg == ffmpeg && p.Device == device && !p.Task.IsFaulted && !p.Task.IsCanceled) return p.Task;
            var task = RunAsync(ffmpeg, device, ct);
            _probe = (ffmpeg, device, task);
            return task;
        }
    }

    private async Task<VaapiState> RunAsync(IFfmpegCapabilities ffmpeg, string device, CancellationToken ct)
    {
        var state = await ProbeCoreAsync(ffmpeg, device, ct);
        _current = state;
        return state;
    }

    private static string[] Device(string device, string? driver = null) =>
        ["-init_hw_device", $"vaapi={VaapiChain.Va}:{device}{(driver is null ? "" : ",driver=" + driver)}", "-filter_hw_device", VaapiChain.Va];

    private async Task<VaapiState> ProbeCoreAsync(IFfmpegCapabilities ffmpeg, string device, CancellationToken ct)
    {
        if (!ffmpeg.HasHwAccel("vaapi") || !ffmpeg.HasFilter("hwupload") || !ffmpeg.HasFilter("scale_vaapi") || !ffmpeg.HasFilter("hwdownload"))
            return VaapiState.None;

        // Opens the device and runs upload → VPP → download: fails fast without /dev/dri or a VA driver.
        if (!await ffmpeg.TryRunAsync([.. Quiet, .. Device(device), .. TestSource, "-frames:v", "1",
                "-vf", "format=nv12,hwupload,scale_vaapi=format=nv12,hwdownload,format=nv12", "-f", "null", "-"], ct))
        {
            log.LogWarning("VAAPI device {Device} does not work: is it mapped into the container and is a VA driver installed?", device);
            return VaapiState.None;
        }

        // Some Intel chips only have the low-power (VDEnc) entrypoint, others lack it: test both modes.
        var normal = new HashSet<string>();
        var lowPower = new HashSet<string>();
        foreach (var (codec, encoder) in Encoders)
        {
            if (!ffmpeg.HasEncoder(encoder)) continue;
            if (await ffmpeg.TryRunAsync(TestEncode(device, encoder, false), ct)) normal.Add(codec);
            if (await ffmpeg.TryRunAsync(TestEncode(device, encoder, true), ct)) lowPower.Add(codec);
        }

        // The driver behind the device decides what Jellyfin enables (Rext decode, VPP, compression_level, AMD fixes).
        var ihd = await DriverAsync(ffmpeg, device, "iHD", ct);
        var amd = !ihd && await DriverAsync(ffmpeg, device, "radeonsi", ct);

        var dir = Directory.CreateTempSubdirectory("jellyserver-vaapi-");
        try
        {
            var decodes = await ProbeDecodersAsync(ffmpeg, device, dir.FullName, ct);
            var hdr = Path.Combine(dir.FullName, "hdr10.mkv");
            var vpp = ihd && decodes.Contains("hevc10") && ffmpeg.HasFilter("tonemap_vaapi") && ffmpeg.HasFilter("procamp_vaapi")
                && await ffmpeg.TryRunAsync([.. Quiet, .. Device(device), .. VaapiChain.HwAccel, "-i", hdr, "-frames:v", "1",
                    "-vf", $"{VaapiChain.VppToneMap(new VppToneMapping())},hwdownload,format=nv12", "-f", "null", "-"], ct);
            var openCl = decodes.Contains("hevc10") ? await ProbeOpenClAsync(ffmpeg, device, hdr, ct) : OpenClMode.None;

            var state = new VaapiState(device, normal, lowPower, decodes, ihd, amd, vpp, openCl);
            log.LogInformation(
                "VAAPI on {Device} ({Driver}): encodes {Codecs}{LowPower}; decodes {Decodes}; VPP tone mapping {Vpp}; OpenCL tone mapping {OpenCl}",
                device, ihd ? "Intel iHD" : amd ? "AMD radeonsi" : "other driver",
                state.Codecs.Count > 0 ? string.Join(", ", state.Codecs) : "nothing",
                lowPower.Count > 0 ? $" (low-power works for {string.Join(", ", lowPower)})" : "",
                decodes.Count > 0 ? string.Join(", ", decodes) : "nothing", vpp ? "works" : "unavailable", openCl);
            return state;
        }
        finally
        {
            try { dir.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private List<string> TestEncode(string device, string encoder, bool lowPower) =>
    [
        .. Quiet, .. Device(device), .. TestSource, "-frames:v", "1", "-vf", "format=nv12,hwupload",
        "-c:v", encoder, .. lowPower ? ["-low_power", "1"] : Array.Empty<string>(), "-f", "null", "-",
    ];

    private Task<bool> DriverAsync(IFfmpegCapabilities ffmpeg, string device, string driver, CancellationToken ct) =>
        ffmpeg.TryRunAsync([.. Quiet, .. Device(device, driver), .. TestSource, "-frames:v", "1",
            "-vf", "format=nv12,hwupload,hwdownload,format=nv12", "-f", "null", "-"], ct);

    /// <summary>
    /// Decodes each sample into VAAPI surfaces and scales it there. When the GPU can't take a stream ffmpeg
    /// silently decodes it on the CPU, and scale_vaapi then fails on the software frames.
    /// </summary>
    private async Task<HashSet<string>> ProbeDecodersAsync(IFfmpegCapabilities ffmpeg, string device, string dir, CancellationToken ct)
    {
        var candidates = Samples
            .Select(s => (s.Key, Encoder: s.Encoders.FirstOrDefault(ffmpeg.HasEncoder), Args: (string[])["-pix_fmt", s.PixFmt]))
            .Append(("hevc10", ffmpeg.HasEncoder("libx265") ? "libx265" : null, ["-pix_fmt", "yuv420p10le", "-x265-params", Hdr10Params]))
            .Where(c => c.Encoder is not null);
        var proven = await Task.WhenAll(candidates.Select(async Task<string?> (c) =>
        {
            var sample = Path.Combine(dir, c.Key == "hevc10" ? "hdr10.mkv" : c.Key + ".mkv");
            var x265 = c.Encoder == "libx265" && c.Key != "hevc10" ? (string[])["-x265-params", "log-level=error"] : [];
            var works = await ffmpeg.TryRunAsync([.. Quiet, .. TestSource, "-frames:v", "2", .. c.Args, "-c:v", c.Encoder!, .. x265, "-y", sample], ct)
                && await ffmpeg.TryRunAsync([.. Quiet, .. Device(device), .. VaapiChain.HwAccel, "-i", sample, "-frames:v", "1",
                    "-vf", "scale_vaapi=format=nv12,hwdownload,format=nv12", "-f", "null", "-"], ct);
            return works ? c.Key : null;
        }));
        return proven.OfType<string>().ToHashSet();
    }

    /// <summary>
    /// tonemap_opencl on the HDR10 sample: first through VAAPI-OpenCL interop (Intel), then through a copy to a
    /// separate OpenCL device (e.g. ROCm on AMD). Needs an OpenCL runtime (ICD) in the container.
    /// </summary>
    private async Task<OpenClMode> ProbeOpenClAsync(IFfmpegCapabilities ffmpeg, string device, string hdr, CancellationToken ct)
    {
        if (!ffmpeg.HasHwAccel("opencl") || !ffmpeg.HasFilter("tonemap_opencl") || !ffmpeg.HasFilter("hwmap")) return OpenClMode.None;
        var tonemap = VaapiChain.OpenClToneMap(new ToneMapping());
        if (await ffmpeg.TryRunAsync([.. Quiet, .. Device(device), .. VaapiChain.HwAccel, "-i", hdr, "-frames:v", "1", "-vf",
                $"hwmap=derive_device=opencl:mode=read,{tonemap},hwmap=derive_device=vaapi:mode=write:reverse=1,format=vaapi,hwdownload,format=nv12",
                "-f", "null", "-"], ct))
            return OpenClMode.Interop;
        return await ffmpeg.TryRunAsync([.. Quiet, "-init_hw_device", $"vaapi={VaapiChain.Va}:{device}", "-init_hw_device", $"opencl={VaapiChain.Ocl}",
                "-filter_hw_device", VaapiChain.Ocl, .. VaapiChain.HwAccel, "-i", hdr, "-frames:v", "1",
                "-vf", $"hwdownload,format=p010le,hwupload,{tonemap},hwdownload,format=nv12", "-f", "null", "-"], ct)
            ? OpenClMode.Copy : OpenClMode.None;
    }
}
