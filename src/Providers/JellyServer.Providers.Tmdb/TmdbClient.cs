using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JellyServer.Providers.Tmdb;

/// <summary>The key was rejected: nothing will work until it is fixed.</summary>
public sealed class TmdbUnauthorizedException() : Exception("TMDB rejected the API key");

/// <summary>HTTP access to TMDB: paced requests, retries on 429 and server errors, 404 as null.</summary>
public sealed class TmdbClient(HttpClient http, IOptions<TmdbOptions> options, ILogger<TmdbClient> log)
{
    private const int Attempts = 4;
    private readonly TmdbOptions _opt = options.Value;
    private readonly object _gate = new();
    private DateTime _next = DateTime.MinValue;
    private volatile bool _unauthorized;

    public bool HasKey => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    /// <summary>The key was refused (401); the provider switches itself off.</summary>
    public bool Unauthorized => _unauthorized;

    public string ImageUrl(string path, string size = "original") => _opt.ImageBaseUrl.TrimEnd('/') + "/" + size + path;

    /// <summary>One request start every 1/RequestsPerSecond seconds, across all workers.</summary>
    private async Task WaitTurnAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var start = now > _next ? now : _next;
            _next = start + TimeSpan.FromSeconds(1.0 / Math.Max(1, _opt.RequestsPerSecond));
            delay = start - now;
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }

    /// <summary>GET a TMDB path; null when it does not exist. The caller disposes the document.</summary>
    public async Task<JsonDocument?> GetAsync(string path, IReadOnlyDictionary<string, string?> query, CancellationToken ct)
    {
        if (_unauthorized) throw new TmdbUnauthorizedException();
        var url = new System.Text.StringBuilder(_opt.BaseUrl.TrimEnd('/')).Append(path).Append('?');
        foreach (var (key, value) in query)
            if (value is not null) url.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value)).Append('&');
        if (!_opt.IsToken) url.Append("api_key=").Append(Uri.EscapeDataString(_opt.ApiKey ?? ""));

        for (var attempt = 1; ; attempt++)
        {
            await WaitTurnAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
                if (_opt.IsToken) request.Headers.Authorization = new("Bearer", _opt.ApiKey);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    case HttpStatusCode.NotFound:
                        return null;
                    case HttpStatusCode.Unauthorized:
                        _unauthorized = true;
                        log.LogError("TMDB rejected the API key (401): the TMDB provider is switched off until the server restarts");
                        throw new TmdbUnauthorizedException();
                    case HttpStatusCode.TooManyRequests when attempt < Attempts:
                        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                        log.LogWarning("TMDB rate limit hit, waiting {Seconds} s", wait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
                        await Task.Delay(wait, ct);
                        continue;
                    case >= HttpStatusCode.InternalServerError when attempt < Attempts:
                        await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                        continue;
                    default:
                        throw new HttpRequestException($"TMDB answered {(int)response.StatusCode} for {path}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }
}