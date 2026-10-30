using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Workbench.Domain.Integrations.Jira;

namespace Workbench.Application.Integrations.Jira;

// The slice of Jira's REST API the sync uses, behind IJiraClient so Jira Data Center (API v2, personal access tokens) can follow.

public sealed record JiraUser(string AccountId, string DisplayName, string? Email);

public sealed record JiraProjectInfo(string Key, string Name);

/// <param name="StatusCategory">Jira's own grouping of any workflow status: <c>new</c>, <c>indeterminate</c> or <c>done</c>.</param>
/// <param name="EstimateSeconds">The original estimate, when the issue has one.</param>
public sealed record JiraIssue(
    string Id,
    string Key,
    string Summary,
    string? Status,
    string StatusCategory,
    JiraUser? Assignee,
    DateOnly? DueDate,
    DateTime? UpdatedUtc,
    long? EstimateSeconds,
    IReadOnlyList<string> Labels)
{
    public bool IsDone => string.Equals(StatusCategory, "done", StringComparison.OrdinalIgnoreCase);

    public decimal? EstimateHours => EstimateSeconds is > 0 ? Math.Round(EstimateSeconds.Value / 3600m, 2) : null;
}

/// <summary>A Jira call that failed. <see cref="Hint"/> says what to do in plain words; <see cref="StatusCode"/> is null when no answer came back.</summary>
public sealed class JiraApiException(int? statusCode, string message, string hint, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string Hint { get; } = hint;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsRetryable => RetryPolicy.IsRetryable(StatusCode);
}

/// <summary>One Jira site as one account. Dispose when done.</summary>
public interface IJiraClient : IDisposable
{
    string SiteUrl { get; }

    string BrowseUrl(string issueKey);

    Task<JiraUser> GetMyselfAsync(CancellationToken ct);

    Task<IReadOnlyList<JiraProjectInfo>> SearchProjectsAsync(string? query, CancellationToken ct);

    Task<JiraProjectInfo?> GetProjectAsync(string key, CancellationToken ct);

    /// <summary>One issue by key; null when it doesn't exist (or the account can't see it).</summary>
    Task<JiraIssue?> GetIssueAsync(string issueKey, CancellationToken ct);

    /// <summary>Issues matching <paramref name="jql"/>, up to <paramref name="maxPages"/> pages of 100.</summary>
    Task<IReadOnlyList<JiraIssue>> SearchIssuesAsync(string jql, int maxPages, CancellationToken ct);

    Task<JiraIssue> CreateIssueAsync(string projectKey, string issueType, string summary, DateOnly? dueDate, string label, CancellationToken ct);

    Task SetDueDateAsync(string issueKey, DateOnly? dueDate, CancellationToken ct);

    /// <summary>Moves the issue to a status in Jira's Done category. False when its workflow offers no way to Done from where it is.</summary>
    Task<bool> TransitionToDoneAsync(string issueKey, CancellationToken ct);

    /// <summary>The Jira account with this email, when Jira lets the connection see it.</summary>
    Task<JiraUser?> FindUserByEmailAsync(string email, CancellationToken ct);
}

public static class JiraUrls
{
    /// <summary>The site's address from what an admin typed: https only (http only where integrations allow it), no path, user name or query.</summary>
    public static Uri NormalizeSite(string? input, bool allowHttp)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Enter your Jira address, such as https://yourcompany.atlassian.net.");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        var uri = OutboundUrlGuard.ParseShape(text, "The Jira address", allowHttp);
        return new Uri(new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>The project key from a key (<c>rad</c>) or a Jira web address (<c>.../browse/RAD-1</c>, <c>.../projects/RAD/...</c>).</summary>
    public static string ProjectKey(string? input)
    {
        var text = (input ?? "").Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var at = Array.FindIndex(parts, p => p is "projects" or "browse");
            text = at >= 0 && at + 1 < parts.Length ? parts[at + 1] : "";
            if (text.Contains('-'))
            {
                text = text[..text.LastIndexOf('-')];
            }
        }

        text = text.ToUpperInvariant();
        if (text.Length is 0 or > 32 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !char.IsAsciiLetter(text[0]))
        {
            throw new ArgumentException("Enter the Jira project's key (such as RAD) or paste its address.");
        }

        return text;
    }

    public static bool IsIssueKey(string? key) =>
        key is { Length: > 2 and <= 64 } && key.LastIndexOf('-') is > 0 and var dash && key[(dash + 1)..].All(char.IsAsciiDigit) && key[(dash + 1)..].Length > 0
        && ProjectKeyOrNull(key[..dash]) is not null;

    public static string? ProjectKeyOrNull(string key)
    {
        try
        {
            return ProjectKey(key);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Escapes a value for a quoted JQL string.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Encrypts the API token and each link's webhook secret at rest (Data Protection).</summary>
public sealed class JiraSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _token = provider.CreateProtector("Workbench.Integrations.Jira.Token.v1");
    private readonly IDataProtector _webhook = provider.CreateProtector("Workbench.Integrations.Jira.WebhookSecret.v1");

    public string ProtectToken(string token) => _token.Protect(token);

    public string UnprotectToken(string protectedToken) => _token.Unprotect(protectedToken);

    public string ProtectWebhookSecret(string secret) => _webhook.Protect(secret);

    public string UnprotectWebhookSecret(string protectedSecret) => _webhook.Unprotect(protectedSecret);

    public static string NewWebhookSecret() =>
        "wbjira_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The HTTP handler Jira calls go through. The real one guards addresses; tests use a fake Jira.</summary>
public interface IJiraHandlerFactory
{
    HttpMessageHandler Get(Uri siteUrl);
}

/// <summary>
/// One pooled handler per site. Never follows redirects (a redirect could carry the credentials elsewhere); connects only to
/// public addresses (unless private targets are allowed in Development), checked at connect time like <see cref="GuardedHttpTransport"/>.
/// </summary>
public sealed class GuardedJiraHandlerFactory(OutboundUrlGuard guard, IOptions<JiraOptions> options) : IJiraHandlerFactory, IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpMessageHandler> _handlers = new(StringComparer.Ordinal);

    public HttpMessageHandler Get(Uri siteUrl) => _handlers.GetOrAdd($"{siteUrl.Scheme}://{siteUrl.Authority}", _ => Create());

    private SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 2, 120)),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = (await guard.AllowedAddressesAsync(host, ct)).ToArray();
            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"Refused: {host} resolves only to private or internal addresses.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public void Dispose()
    {
        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }
    }
}

/// <summary>Makes clients for the saved connection or for settings being tested. Swap this (by <see cref="JiraFlavor"/>) for Data Center.</summary>
public sealed class JiraClientFactory(IJiraHandlerFactory handlers, JiraSecrets secrets, IOptions<JiraOptions> options)
{
    public IJiraClient For(JiraConnection connection) => Create(connection.SiteUrl, connection.AccountEmail, secrets.UnprotectToken(connection.TokenProtected));

    public IJiraClient Create(string siteUrl, string accountEmail, string token) =>
        new JiraCloudClient(handlers.Get(new Uri(siteUrl)), siteUrl, accountEmail, token, options.Value);
}

/// <summary>Jira Cloud: REST API v3 with an account email and API token (HTTP Basic). Never logs the token.</summary>
public sealed class JiraCloudClient : IJiraClient
{
    private const string IssueFields = "summary,status,assignee,duedate,updated,timeoriginalestimate,labels";
    private readonly HttpClient _http;

    public JiraCloudClient(HttpMessageHandler handler, string siteUrl, string accountEmail, string token, JiraOptions options)
    {
        SiteUrl = siteUrl.TrimEnd('/');
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 2, 120)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Workbench-Jira-Sync/1.0");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{accountEmail.Trim()}:{token.Trim()}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string SiteUrl { get; }

    public string BrowseUrl(string issueKey) => $"{SiteUrl}/browse/{Uri.EscapeDataString(issueKey)}";

    public async Task<JiraUser> GetMyselfAsync(CancellationToken ct) => ToUser((await GetAsync("myself", ct)).RootElement)!;

    public async Task<IReadOnlyList<JiraProjectInfo>> SearchProjectsAsync(string? query, CancellationToken ct)
    {
        using var doc = await GetAsync($"project/search?maxResults=20&orderBy=name{(string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query.Trim()))}", ct);
        return doc.RootElement.TryGetProperty("values", out var values) ? values.EnumerateArray().Select(ToProject).ToList() : [];
    }

    public async Task<JiraProjectInfo?> GetProjectAsync(string key, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync($"project/{Uri.EscapeDataString(key)}", ct);
            return ToProject(doc.RootElement);
        }
        catch (JiraApiException ex) when (ex.StatusCode is 404 or 403)
        {
            return null;
        }
    }

    public async Task<JiraIssue?> GetIssueAsync(string issueKey, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync($"issue/{Uri.EscapeDataString(issueKey)}?fields={IssueFields}", ct);
            return ToIssue(doc.RootElement);
        }
        catch (JiraApiException ex) when (ex.StatusCode is 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<JiraIssue>> SearchIssuesAsync(string jql, int maxPages, CancellationToken ct)
    {
        var issues = new List<JiraIssue>();
        string? token = null;
        for (var page = 0; page < Math.Max(1, maxPages); page++)
        {
            using var doc = await GetAsync($"search/jql?jql={Uri.EscapeDataString(jql)}&fields={IssueFields}&maxResults=100{(token is null ? "" : "&nextPageToken=" + Uri.EscapeDataString(token))}", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("issues", out var list))
            {
                issues.AddRange(list.EnumerateArray().Select(ToIssue));
            }

            token = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (token is null)
            {
                break;
            }
        }

        return issues;
    }

    public async Task<JiraIssue> CreateIssueAsync(string projectKey, string issueType, string summary, DateOnly? dueDate, string label, CancellationToken ct)
    {
        var fields = new Dictionary<string, object?>
        {
            ["project"] = new { key = projectKey },
            ["issuetype"] = new { name = issueType },
            ["summary"] = summary,
            ["labels"] = new[] { label },
        };
        if (dueDate is { } due)
        {
            fields["duedate"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        using var created = await SendAsync(HttpMethod.Post, "issue", new { fields }, ct);
        var key = created.RootElement.GetProperty("key").GetString()!;
        using var doc = await GetAsync($"issue/{Uri.EscapeDataString(key)}?fields={IssueFields}", ct);
        return ToIssue(doc.RootElement);
    }

    public async Task SetDueDateAsync(string issueKey, DateOnly? dueDate, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Put, $"issue/{Uri.EscapeDataString(issueKey)}",
            new { fields = new Dictionary<string, object?> { ["duedate"] = dueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } }, ct);
    }

    public async Task<bool> TransitionToDoneAsync(string issueKey, CancellationToken ct)
    {
        using var doc = await GetAsync($"issue/{Uri.EscapeDataString(issueKey)}/transitions", ct);
        string? id = null;
        if (doc.RootElement.TryGetProperty("transitions", out var transitions))
        {
            foreach (var t in transitions.EnumerateArray())
            {
                if (t.TryGetProperty("to", out var to) && to.TryGetProperty("statusCategory", out var cat) && Str(cat, "key") == "done")
                {
                    id = Str(t, "id");
                    break;
                }
            }
        }

        if (id is null)
        {
            return false;
        }

        using var _ = await SendAsync(HttpMethod.Post, $"issue/{Uri.EscapeDataString(issueKey)}/transitions", new { transition = new { id } }, ct);
        return true;
    }

    public async Task<JiraUser?> FindUserByEmailAsync(string email, CancellationToken ct)
    {
        using var doc = await GetAsync($"user/search?query={Uri.EscapeDataString(email.Trim())}&maxResults=5", ct);
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            if (Str(u, "accountType") is { } type && type != "atlassian")
            {
                continue;
            }

            var user = ToUser(u);
            if (user?.Email is { } found && string.Equals(found, email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return user;
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();

    private Task<JsonDocument> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{SiteUrl}/rest/api/3/{path}");
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JiraApiException(null, "Jira didn't answer in time.", "Nothing to do: Workbench tries again shortly.");
        }
        catch (HttpRequestException ex)
        {
            throw new JiraApiException(null, $"Couldn't reach Jira: {ex.InnerException?.Message ?? ex.Message}", "Check the Jira address under Admin › Integrations › Jira. A Jira outage clears on its own.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }

            var status = (int)response.StatusCode;
            var detail = ErrorText(text);
            throw status switch
            {
                401 => new JiraApiException(status, "Jira didn't accept the account email and API token.", "Create a new API token in your Atlassian account (Security › API tokens) and save it under Admin › Integrations › Jira."),
                403 => new JiraApiException(status, $"Jira says this account isn't allowed to do that{(detail is null ? "" : ": " + detail)}.", "Give the account access to the Jira project, or link a project it can see."),
                404 => new JiraApiException(status, $"Jira couldn't find that{(detail is null ? "" : ": " + detail)}.", "Check the project key or issue, and that the account can see it."),
                429 => new JiraApiException(status, "Jira asked Workbench to slow down.", "Nothing to do: Workbench tries again shortly.", response.Headers.RetryAfter?.Delta),
                >= 500 => new JiraApiException(status, "Jira had a problem and couldn't answer.", "Nothing to do: Workbench tries again shortly."),
                _ => new JiraApiException(status, $"Jira refused the change{(detail is null ? "" : ": " + detail)}.", "Look at the issue in Jira: a required field or a workflow rule may be in the way."),
            };
        }
    }

    private static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var messages = new List<string>();
            if (doc.RootElement.TryGetProperty("errorMessages", out var list))
            {
                messages.AddRange(list.EnumerateArray().Select(e => e.GetString() ?? ""));
            }

            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                messages.AddRange(errors.EnumerateObject().Select(p => p.Value.GetString() ?? ""));
            }

            var text = string.Join(" ", messages.Where(m => m.Length > 0));
            return text.Length == 0 ? null : text.Length > 300 ? text[..300] : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JiraProjectInfo ToProject(JsonElement e) => new(Str(e, "key") ?? "", Str(e, "name") ?? Str(e, "key") ?? "");

    private static JiraUser? ToUser(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && Str(e, "accountId") is { } id ? new JiraUser(id, Str(e, "displayName") ?? id, Str(e, "emailAddress")) : null;

    internal static JiraIssue ToIssue(JsonElement e)
    {
        var f = e.TryGetProperty("fields", out var fields) ? fields : default;
        string? status = null;
        var category = "new";
        if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            status = Str(s, "name");
            category = s.TryGetProperty("statusCategory", out var c) ? Str(c, "key") ?? "new" : "new";
        }

        DateOnly? due = f.ValueKind == JsonValueKind.Object && Str(f, "duedate") is { Length: >= 10 } d
            && DateOnly.TryParseExact(d[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        DateTime? updated = f.ValueKind == JsonValueKind.Object && Str(f, "updated") is { } u && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.UtcDateTime : null;
        long? estimate = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("timeoriginalestimate", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var seconds) ? seconds : null;
        var labels = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
        return new JiraIssue(Str(e, "id") ?? "", Str(e, "key") ?? "", f.ValueKind == JsonValueKind.Object ? Str(f, "summary") ?? "" : "", status, category,
            f.ValueKind == JsonValueKind.Object && f.TryGetProperty("assignee", out var a) ? ToUser(a) : null, due, updated, estimate, labels);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
