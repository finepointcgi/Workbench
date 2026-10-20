using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Workbench.Application.Integrations;

public sealed record OutboundRequest(Uri Url, string Body, string ContentType, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <param name="StatusCode">Null when no response came back (DNS, connection, TLS or timeout); <paramref name="Error"/> says why.</param>
public sealed record OutboundResponse(int? StatusCode, string? Headers, string? Body, string? Error, int DurationMs)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// Sends integration HTTP requests (webhooks, Teams, Slack). The real one is <see cref="GuardedHttpTransport"/>; tests register a
/// fake, so no test ever calls a third party.
/// </summary>
public interface IOutboundTransport
{
    Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// POSTs with a fixed timeout, never follows redirects, ignores proxies, and connects only to addresses the
/// <see cref="OutboundUrlGuard"/> allows, checked at connect time (DNS rebinding). Response bodies are read up to 4 KB.
/// </summary>
public sealed class GuardedHttpTransport : IOutboundTransport, IDisposable
{
    public const int MaxResponseChars = 4000;
    private readonly HttpClient _http;

    public GuardedHttpTransport(OutboundUrlGuard guard, IOptions<IntegrationOptions> options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.RequestTimeoutSeconds)),
            ConnectCallback = async (context, ct) =>
            {
                var allowed = await guard.AllowedAddressesAsync(context.DnsEndPoint.Host, ct);
                if (allowed.Count == 0)
                {
                    throw new HttpRequestException($"Refused: {context.DnsEndPoint.Host} resolves only to private or internal addresses.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.RequestTimeoutSeconds)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Workbench-Integrations/1.0");
    }

    public async Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
            {
                Content = new StringContent(request.Body, Encoding.UTF8, MediaTypeHeaderValue.Parse(request.ContentType).MediaType!),
            };
            foreach (var (name, value) in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await ReadLimitedAsync(response.Content, cancellationToken);
            var headers = string.Join("\n", response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
            return new OutboundResponse((int)response.StatusCode, Clip(headers, MaxResponseChars), body, null, (int)watch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OutboundResponse(null, null, null, "Timed out waiting for a response.", (int)watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            return new OutboundResponse(null, null, null, Clip(ex.InnerException?.Message is { } inner ? $"{ex.Message} {inner}" : ex.Message, 1000), (int)watch.ElapsedMilliseconds);
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new char[MaxResponseChars];
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, read);
    }

    internal static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}

/// <summary>Retries with exponential backoff: 30 s, 1 min, 2 min … capped at 6 h, with a little jitter so retries don't arrive together.</summary>
public static class RetryPolicy
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);

    /// <summary>The wait after failed attempt number <paramref name="attempt"/> (1-based): base × 2^(attempt−1), up to 10% more.</summary>
    public static TimeSpan DelayAfter(int attempt, Guid jitterSeed)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var seconds = Math.Min(BaseDelay.TotalSeconds * Math.Pow(2, exponent), MaxDelay.TotalSeconds);
        var jitter = (Math.Abs(jitterSeed.GetHashCode() % 1000) / 10000.0) * seconds;
        return TimeSpan.FromSeconds(Math.Min(seconds + jitter, MaxDelay.TotalSeconds));
    }

    /// <summary>Whether a failure is worth retrying: no response, a timeout (408), rate limiting (429) or a server error (5xx).</summary>
    public static bool IsRetryable(int? statusCode) => statusCode is null or 408 or 429 or >= 500;
}
