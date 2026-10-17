using System.Text;
using JellyServer.Kernel;
using JellyServer.Identity.Contracts;

namespace JellyServer.Services;

/// <summary>
/// Relays remote .strm streams through this server. HLS playlists are rewritten
/// so every variant playlist and segment also goes through <c>/Videos/{id}/proxy</c>
/// with an HMAC signature (players fetch segments without auth headers).
/// </summary>
public sealed class StreamProxy(IHttpClientFactory http, ServerIdentity identity, ILogger<StreamProxy> log)
{
    private static readonly string[] PassthroughResponseHeaders =
        ["Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag"];

    public string SignedPath(Guid itemId, string url)
    {
        var u = Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"proxy?u={u}&s={identity.Sign(itemId.N() + "|" + url)}";
    }

    public string? VerifySigned(Guid itemId, string? u, string? s)
    {
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(s)) return null;
        var b64 = u.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        string url;
        try { url = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return null; }
        return identity.Sign(itemId.N() + "|" + url) == s ? url : null;
    }

    public async Task RelayAsync(HttpContext ctx, Guid itemId, string url)
    {
        using var req = new HttpRequestMessage(HttpMethods.IsHead(ctx.Request.Method) ? HttpMethod.Head : HttpMethod.Get, url);
        if (ctx.Request.Headers.Range.Count > 0) req.Headers.TryAddWithoutValidation("Range", ctx.Request.Headers.Range.ToString());
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 JellyServer");

        HttpResponseMessage resp;
        try
        {
            resp = await http.CreateClient("upstream").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Upstream {Url} failed: {Error}", url, ex.Message);
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        using (resp)
        {
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var finalUrl = resp.RequestMessage?.RequestUri ?? new Uri(url);
            if (IsPlaylist(contentType, finalUrl))
            {
                var body = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.Headers.CacheControl = "no-cache";
                await ctx.Response.WriteAsync(RewritePlaylist(body, finalUrl, itemId), ctx.RequestAborted);
                return;
            }

            ctx.Response.StatusCode = (int)resp.StatusCode;
            foreach (var name in PassthroughResponseHeaders)
            {
                if (resp.Headers.TryGetValues(name, out var v) || resp.Content.Headers.TryGetValues(name, out v))
                    ctx.Response.Headers[name] = v.ToArray();
            }
            if (!HttpMethods.IsHead(ctx.Request.Method))
                await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }

    private static bool IsPlaylist(string contentType, Uri url) =>
        contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
        url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rewrites URI lines and URI="…" attributes to absolute, signed proxy URLs.</summary>
    public string RewritePlaylist(string body, Uri baseUri, Guid itemId)
    {
        var sb = new StringBuilder(body.Length * 2);
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { sb.Append('\n'); continue; }
            if (line.StartsWith('#'))
            {
                var idx = line.IndexOf("URI=\"", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var start = idx + 5;
                    var end = line.IndexOf('"', start);
                    if (end > start)
                    {
                        var abs = new Uri(baseUri, line[start..end]).ToString();
                        line = line[..start] + SignedPath(itemId, abs) + line[end..];
                    }
                }
                sb.Append(line).Append('\n');
                continue;
            }
            sb.Append(SignedPath(itemId, new Uri(baseUri, line.Trim()).ToString())).Append('\n');
        }
        return sb.ToString();
    }
}
