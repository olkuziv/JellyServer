using System.Diagnostics;
using System.Text;
using JellyServer.Configuration;
using Microsoft.Extensions.Options;

namespace JellyServer.Api;

/// <summary>
/// Strips legacy prefixes (/emby, /jellyfin) and collapses duplicate slashes.
/// ASP.NET Core routing is already case-insensitive, like Jellyfin's.
/// </summary>
public sealed class PathNormalizationMiddleware(RequestDelegate next)
{
    private static readonly string[] Prefixes = ["/emby", "/mediabrowser", "/jellyfin"];

    public Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path.Contains("//")) path = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var p in Prefixes)
        {
            if (path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path[p.Length..];
                break;
            }
        }
        ctx.Request.Path = path;
        return next(ctx);
    }
}

/// <summary>Structured access log: 404s (compatibility gaps), errors and slow requests are always visible.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log, IOptions<ServerOptions> options, Metrics metrics)
{
    private readonly long _slowMs = options.Value.SlowRequestMs;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var start = Stopwatch.GetTimestamp();
        var path = ctx.Request.Path.Value ?? "";
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
        {
            return; // client went away mid-request (normal while seeking/zapping)
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error {Method} {Path}", ctx.Request.Method, path);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
        finally
        {
            var ms = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var status = ctx.Response.StatusCode;
            metrics.Observe(status, ms);
            var streaming = path.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase) || path.Equals("/socket", StringComparison.OrdinalIgnoreCase);
            if (status == 404)
                log.LogWarning("404 {Method} {Path}{Query} ({Client})", ctx.Request.Method, path, ctx.Request.QueryString, ClientName(ctx));
            else if (status >= 500)
                log.LogError("{Status} {Method} {Path} in {Ms} ms", status, ctx.Request.Method, path, ms);
            else if (_slowMs > 0 && ms > _slowMs && !streaming)
                log.LogWarning("Slow request {Method} {Path}{Query}: {Status} in {Ms} ms", ctx.Request.Method, path, ctx.Request.QueryString, status, ms);
            else if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("{Method} {Path}: {Status} in {Ms} ms", ctx.Request.Method, path, status, ms);
        }
    }

    private static string ClientName(HttpContext ctx)
    {
        var h = AuthHeader.Parse(ctx.Request);
        return h.Client.Length > 0 ? $"{h.Client}/{h.Device}" : ctx.Request.Headers.UserAgent.ToString();
    }
}

/// <summary>Minimal Prometheus-format request metrics (no external dependency).</summary>
public sealed class Metrics
{
    private static readonly long[] Buckets = [5, 25, 100, 250, 1000, 5000];
    private readonly long[] _bucketCounts = new long[Buckets.Length + 1];
    private long _requests, _errors, _notFound, _totalMs;

    public void Observe(int status, long ms)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _totalMs, ms);
        if (status >= 500) Interlocked.Increment(ref _errors);
        if (status == 404) Interlocked.Increment(ref _notFound);
        var i = Array.FindIndex(Buckets, b => ms <= b);
        Interlocked.Increment(ref _bucketCounts[i < 0 ? Buckets.Length : i]);
    }

    public string Render(IEnumerable<(string Name, double Value)> extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"jellyserver_requests_total {Interlocked.Read(ref _requests)}");
        sb.AppendLine($"jellyserver_requests_errors_total {Interlocked.Read(ref _errors)}");
        sb.AppendLine($"jellyserver_requests_not_found_total {Interlocked.Read(ref _notFound)}");
        sb.AppendLine($"jellyserver_request_duration_ms_sum {Interlocked.Read(ref _totalMs)}");
        long cumulative = 0;
        for (var i = 0; i < Buckets.Length; i++)
        {
            cumulative += Interlocked.Read(ref _bucketCounts[i]);
            sb.AppendLine($"jellyserver_request_duration_ms_bucket{{le=\"{Buckets[i]}\"}} {cumulative}");
        }
        cumulative += Interlocked.Read(ref _bucketCounts[Buckets.Length]);
        sb.AppendLine($"jellyserver_request_duration_ms_bucket{{le=\"+Inf\"}} {cumulative}");
        foreach (var (name, value) in extra) sb.AppendLine($"jellyserver_{name} {value}");
        return sb.ToString();
    }
}
