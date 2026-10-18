using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Workbench.Application.Imports.Smartsheet;

/// <summary>The few Smartsheet REST API calls the importer makes. Every call takes the customer's API token.</summary>
public interface ISmartsheetClient
{
    Task<IReadOnlyList<SmartsheetSheetSummary>> ListSheetsAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>A sheet with its columns and rows, including object values (contacts, durations, predecessors).</summary>
    Task<SmartsheetSheet> GetSheetAsync(string token, string sheetId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmartsheetDiscussion>> ListDiscussionsAsync(string token, string sheetId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmartsheetAttachment>> ListAttachmentsAsync(string token, string sheetId, CancellationToken cancellationToken = default);

    /// <summary>Downloads a file attachment's content (from the short-lived URL Smartsheet hands out).</summary>
    Task<byte[]> DownloadAttachmentAsync(string token, string sheetId, string attachmentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ISmartsheetClient"/> over HTTP (base address <c>https://api.smartsheet.com/2.0/</c>). Smartsheet allows
/// about 300 requests a minute per token and answers 429 (error 4003) beyond that; 429s and transient 5xx (4001/4002/4004)
/// are retried with exponential backoff, honouring Retry-After.
/// </summary>
public sealed class SmartsheetApiClient(HttpClient http, ILogger<SmartsheetApiClient>? logger = null, SmartsheetRetryPolicy? retry = null) : ISmartsheetClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.smartsheet.com/2.0/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILogger _logger = logger ?? NullLogger<SmartsheetApiClient>.Instance;
    private readonly SmartsheetRetryPolicy _retry = retry ?? new SmartsheetRetryPolicy();

    public async Task<IReadOnlyList<SmartsheetSheetSummary>> ListSheetsAsync(string token, CancellationToken cancellationToken = default) =>
        (await GetAsync<SmartsheetPage<SmartsheetSheetSummary>>(token, "sheets?includeAll=true", cancellationToken)).Data;

    public Task<SmartsheetSheet> GetSheetAsync(string token, string sheetId, CancellationToken cancellationToken = default) =>
        GetAsync<SmartsheetSheet>(token, $"sheets/{Id(sheetId)}?include=objectValue&level=2", cancellationToken);

    public async Task<IReadOnlyList<SmartsheetDiscussion>> ListDiscussionsAsync(string token, string sheetId, CancellationToken cancellationToken = default) =>
        (await GetAsync<SmartsheetPage<SmartsheetDiscussion>>(token, $"sheets/{Id(sheetId)}/discussions?include=comments&includeAll=true", cancellationToken)).Data;

    public async Task<IReadOnlyList<SmartsheetAttachment>> ListAttachmentsAsync(string token, string sheetId, CancellationToken cancellationToken = default) =>
        (await GetAsync<SmartsheetPage<SmartsheetAttachment>>(token, $"sheets/{Id(sheetId)}/attachments?includeAll=true", cancellationToken)).Data;

    public async Task<byte[]> DownloadAttachmentAsync(string token, string sheetId, string attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await GetAsync<SmartsheetAttachment>(token, $"sheets/{Id(sheetId)}/attachments/{Id(attachmentId)}", cancellationToken);
        if (attachment.Url is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Smartsheet returned no download link for attachment {attachment.Name}.");
        }

        // The download URL is pre-signed: send no token to it.
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<T> GetAsync<T>(string token, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A Smartsheet API token is required.");
        }

        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            return request;
        }, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new ArgumentException($"Smartsheet returned an empty response for {path.Split('?')[0]}.");
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (code, message) = ParseError(body);
            var transient = response.StatusCode == HttpStatusCode.TooManyRequests || code is 4001 or 4002 or 4003 or 4004
                || response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
            if (transient && attempt < _retry.MaxRetries)
            {
                var delay = _retry.DelayFor(attempt, response.Headers.RetryAfter);
                _logger.LogInformation("Smartsheet answered {Status} ({Code}); retrying in {Delay}.", (int)response.StatusCode, code, delay);
                response.Dispose();
                await _retry.WaitAsync(delay, cancellationToken);
                continue;
            }

            response.Dispose();
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new ArgumentException("Smartsheet rejected the API token. Check it in Smartsheet under Apps & Integrations → API Access."),
                HttpStatusCode.NotFound => new ArgumentException("Smartsheet couldn't find that sheet (or the token can't see it)."),
                HttpStatusCode.TooManyRequests => new ArgumentException("Smartsheet's rate limit is still exceeded after several retries; try again in a few minutes."),
                _ => new ArgumentException($"Smartsheet returned {(int)response.StatusCode}: {message ?? response.ReasonPhrase}"),
            };
        }
    }

    private static (int? Code, string? Message) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("errorCode", out var c) && c.TryGetInt32(out var n) ? n : (int?)null;
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string Id(string id) =>
        long.TryParse(id, out var n) && n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentException($"'{id}' isn't a Smartsheet id.");
}

/// <summary>Backoff for Smartsheet's rate limit: 1 s, 2 s, 4 s ... (capped at 60 s), or the server's Retry-After.</summary>
public class SmartsheetRetryPolicy
{
    public int MaxRetries { get; init; } = 6;

    public TimeSpan DelayFor(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta < TimeSpan.FromSeconds(120) ? delta : TimeSpan.FromSeconds(120);
        }

        var seconds = Math.Min(60, Math.Pow(2, attempt)) * (0.8 + Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(seconds);
    }

    public virtual Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
