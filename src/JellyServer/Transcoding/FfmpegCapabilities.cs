using System.Diagnostics;

namespace JellyServer.Transcoding;

/// <summary>Encoders, filters and hwaccels of the installed ffmpeg, read once from its listings.</summary>
public sealed class FfmpegCapabilities : IFfmpegCapabilities
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);

    private readonly HashSet<string> _encoders;
    private readonly HashSet<string> _decoders;
    private readonly HashSet<string> _filters;
    private readonly HashSet<string> _hwAccels;

    public string FfmpegPath { get; }

    public FfmpegCapabilities(string path, IEnumerable<string> encoders, IEnumerable<string> decoders, IEnumerable<string> filters,
        IEnumerable<string> hwAccels)
    {
        FfmpegPath = path;
        _encoders = encoders.ToHashSet(StringComparer.Ordinal);
        _decoders = decoders.ToHashSet(StringComparer.Ordinal);
        _filters = filters.ToHashSet(StringComparer.Ordinal);
        _hwAccels = hwAccels.ToHashSet(StringComparer.Ordinal);
    }

    public bool HasEncoder(string name) => _encoders.Contains(name);
    public bool HasDecoder(string name) => _decoders.Contains(name);
    public bool HasFilter(string name) => _filters.Contains(name);
    public bool HasHwAccel(string name) => _hwAccels.Contains(name);

    public async Task<bool> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        await RunAsync(FfmpegPath, args, ct) is { ExitCode: 0 };

    /// <summary>Null when ffmpeg cannot be started at all.</summary>
    public static async Task<FfmpegCapabilities?> DetectAsync(string path, CancellationToken ct)
    {
        var encoders = await RunAsync(path, ["-hide_banner", "-encoders"], ct);
        if (encoders is not { ExitCode: 0 } listed) return null;
        var decoders = await RunAsync(path, ["-hide_banner", "-decoders"], ct);
        var filters = await RunAsync(path, ["-hide_banner", "-filters"], ct);
        var hwAccels = await RunAsync(path, ["-hide_banner", "-hwaccels"], ct);
        return new FfmpegCapabilities(path, ParseEncoders(listed.Output), ParseEncoders(decoders?.Output ?? ""), ParseFilters(filters?.Output ?? ""),
            ParseHwAccels(hwAccels?.Output ?? ""));
    }

    /// <summary>Entries follow a "------" line: <c> V....D libx264   description</c>. Decoders are listed the same way.</summary>
    public static IEnumerable<string> ParseEncoders(string output)
    {
        var started = false;
        foreach (var line in output.Split('\n'))
        {
            if (!started) { started = line.Trim() == "------"; continue; }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) yield return parts[1];
        }
    }

    /// <summary>Entries look like <c> ... zscale   V->V   description</c>.</summary>
    public static IEnumerable<string> ParseFilters(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[2].Contains("->")) yield return parts[1];
        }
    }

    /// <summary>A header line followed by one name per line.</summary>
    public static IEnumerable<string> ParseHwAccels(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.EndsWith(':'));

    private static async Task<(int ExitCode, string Output)?> RunAsync(string path, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { return null; }

        using (proc)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(RunTimeout);
            try
            {
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                return (proc.ExitCode, await stdout + await stderr);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return (-1, "timed out");
            }
        }
    }
}
