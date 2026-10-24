using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workbench.Application.Payroll;

/// <summary>
/// Creates the HTTP handlers the payroll adapters call their providers with. ADP needs the customer's client certificate
/// (mutual TLS), so handlers are made per certificate and kept for reuse. Tests replace this with recorded responses.
/// </summary>
public interface IPayrollHttpHandlerFactory
{
    HttpMessageHandler Create(string providerKey, X509Certificate2? clientCertificate);
}

public sealed class PayrollHttpHandlerFactory : IPayrollHttpHandlerFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> _handlers = new();

    public HttpMessageHandler Create(string providerKey, X509Certificate2? clientCertificate) =>
        _handlers.GetOrAdd($"{providerKey}:{clientCertificate?.Thumbprint}", _ =>
        {
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
            if (clientCertificate is not null)
            {
                handler.SslOptions.ClientCertificates = [clientCertificate];
            }

            return handler;
        });

    public void Dispose()
    {
        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }
    }
}

/// <summary>
/// A small JSON-over-HTTPS client for one provider call batch: bearer token, JSON bodies, and errors turned into
/// <see cref="PayrollProviderApiException"/> without echoing credentials. Responses are read as <see cref="JsonNode"/>,
/// so each adapter maps the provider's documented fields explicitly.
/// </summary>
public sealed class PayrollApi(HttpClient http, string providerName)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? _token;

    public HttpClient Http => http;

    public Dictionary<string, string> Headers { get; } = [];

    public void UseToken(string token) => _token = token;

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonNode?> PostAsync(string path, object? body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, body, ct);

    public Task<JsonNode?> PutAsync(string path, object? body, CancellationToken ct) => SendAsync(HttpMethod.Put, path, body, ct);

    public Task<JsonNode?> PatchAsync(string path, object? body, CancellationToken ct) => SendAsync(HttpMethod.Patch, path, body, ct);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PayrollProviderApiException($"Couldn't reach {providerName}: {ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new PayrollProviderApiException($"{providerName} returned {(int)response.StatusCode} for {method} {Redact(path)}: {Detail(text)}");
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    /// <summary>OAuth 2.0 token request (form post). Returns the token response; throws a plain message when refused.</summary>
    public async Task<JsonNode> TokenAsync(string url, IReadOnlyDictionary<string, string> form, (string User, string Password)? basic, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (basic is { } b)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(b.User)}:{Uri.EscapeDataString(b.Password)}")));
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PayrollProviderApiException($"Couldn't reach {providerName} to sign in: {ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new PayrollProviderApiException($"{providerName} refused the credentials ({(int)response.StatusCode}). Check them on the connect screen.");
            }

            var node = JsonNode.Parse(text) ?? throw new PayrollProviderApiException($"{providerName} returned no access token.");
            if (node["access_token"]?.GetValue<string>() is not { Length: > 0 } token)
            {
                throw new PayrollProviderApiException($"{providerName} returned no access token.");
            }

            UseToken(token);
            return node;
        }
    }

    public static decimal Decimal(JsonNode? node) => node switch
    {
        null => 0m,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        _ => 0m,
    };

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString();

    public static DateOnly? Date(JsonNode? node) => Text(node) is { Length: >= 10 } s && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", out var d) ? d : null;

    public static IEnumerable<JsonNode> Items(JsonNode? node) => node is JsonArray a ? a.OfType<JsonNode>() : [];

    private static string Redact(string path) => path.Split('?')[0];

    private static string Detail(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length > 300 ? flat[..300] : flat.Length == 0 ? "(no detail)" : flat;
    }
}
