using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Workbench.Domain.Integrations.GitLab;

namespace Workbench.Application.Integrations.GitLab;

// The slice of GitLab's REST API (v4) the sync uses, with GitLab's own JSON field names (snake_case).

public sealed record GitLabVersion(string Version, string? Revision, bool? Enterprise);

public sealed record GitLabUser(long Id, string Username, string Name, string? Email = null, string? PublicEmail = null, string? State = null, string? WebUrl = null);

public sealed record GitLabTokenInfo(string? Name, IReadOnlyList<string>? Scopes, DateOnly? ExpiresAt, bool? Active);

public sealed record GitLabNamespace(long Id, string Kind, string FullPath, string? Name = null);

public sealed record GitLabProject(long Id, string Name, string PathWithNamespace, string WebUrl, GitLabNamespace? Namespace);

public sealed record GitLabMilestoneRef(long Id, long Iid, string Title);

public sealed record GitLabEpicRef(long Id, long Iid, long? GroupId, string? Title);

public sealed record GitLabTimeStats(long TimeEstimate, long TotalTimeSpent);

public sealed record GitLabTaskCompletion(int Count, int CompletedCount);

public sealed record GitLabIssue(
    long Id,
    long Iid,
    long ProjectId,
    string Title,
    string? Description,
    string State,
    DateTime UpdatedAt,
    DateOnly? DueDate,
    IReadOnlyList<GitLabUser>? Assignees,
    IReadOnlyList<string>? Labels,
    GitLabMilestoneRef? Milestone,
    GitLabEpicRef? Epic,
    GitLabTimeStats? TimeStats,
    GitLabTaskCompletion? TaskCompletionStatus,
    int? Weight,
    string WebUrl,
    GitLabUser? Author = null,
    DateTime? ClosedAt = null);

public sealed record GitLabEpic(
    long Id,
    long Iid,
    long GroupId,
    string Title,
    string? Description,
    string State,
    DateTime UpdatedAt,
    DateOnly? StartDateFixed,
    DateOnly? DueDateFixed,
    long? ParentId,
    IReadOnlyList<string>? Labels,
    string WebUrl);

public sealed record GitLabMilestone(
    long Id,
    long Iid,
    long? ProjectId,
    string Title,
    string? Description,
    string State,
    DateOnly? StartDate,
    DateOnly? DueDate,
    DateTime UpdatedAt,
    string WebUrl);

/// <summary>An issue linked to another (<c>GET /issues/:iid/links</c> returns the other issue plus the link's id and type).</summary>
public sealed record GitLabLinkedIssue(long Id, long Iid, long ProjectId, long IssueLinkId, string LinkType);

public sealed record GitLabHook(long Id, string Url);

/// <summary>A child task (work item) of an issue, for progress.</summary>
public sealed record GitLabChildTask(bool Closed, int? Weight);

/// <summary>
/// A GitLab call that failed. <see cref="Hint"/> says what to do in plain words. <see cref="StatusCode"/> is null when no
/// answer came back (address, network, certificate, timeout).
/// </summary>
public sealed class GitLabApiException(int? statusCode, string message, string hint, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string Hint { get; } = hint;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Worth trying again later: no answer, a timeout, rate limiting or a GitLab server error.</summary>
    public bool IsRetryable => RetryPolicy.IsRetryable(StatusCode);
}

/// <summary>
/// Keeps Workbench under each GitLab server's rate limit: after an answer says only a few requests are left in the window
/// (<c>RateLimit-Remaining</c>), or after a 429, calls to that server wait for the window to reset. A wait longer than a
/// couple of seconds fails the call as retryable instead, so the outbox tries it after its backoff.
/// </summary>
public sealed class GitLabRateGate(TimeProvider clock)
{
    public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? PausedUntil(string host) => _pausedUntil.TryGetValue(host, out var until) && until > clock.GetUtcNow() ? until : null;

    public async Task WaitAsync(string host, CancellationToken ct)
    {
        if (PausedUntil(host) is not { } until)
        {
            return;
        }

        var wait = until - clock.GetUtcNow();
        if (wait > MaxInlineWait)
        {
            throw new GitLabApiException(429, $"GitLab asked Workbench to slow down until {until:HH:mm:ss} UTC.",
                "Nothing to do: Workbench sends the waiting changes once GitLab's rate limit resets.", wait);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, clock, ct);
        }
    }

    public void Pause(string host, DateTimeOffset until) =>
        _pausedUntil.AddOrUpdate(host, until, (_, existing) => existing > until ? existing : until);

    /// <summary>Reads the rate limit headers of an answer.</summary>
    public void Observe(string host, HttpResponseMessage response, int reserve)
    {
        var now = clock.GetUtcNow();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Pause(host, now + (RetryAfter(response, now) ?? TimeSpan.FromSeconds(60)));
            return;
        }

        if (Header(response, "RateLimit-Remaining") is { } left && int.TryParse(left, out var remaining) && remaining <= reserve
            && Header(response, "RateLimit-Reset") is { } reset && long.TryParse(reset, out var unix))
        {
            Pause(host, DateTimeOffset.FromUnixTimeSeconds(unix));
        }
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            return retry.Delta ?? (retry.Date is { } date ? date - now : null);
        }

        return Header(response, "RateLimit-Reset") is { } reset && long.TryParse(reset, out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) - now : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}

/// <summary>Encrypts the GitLab token and each link's webhook secret at rest (Data Protection).</summary>
public sealed class GitLabSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _token = provider.CreateProtector("Workbench.Integrations.GitLab.Token.v1");
    private readonly IDataProtector _webhook = provider.CreateProtector("Workbench.Integrations.GitLab.WebhookSecret.v1");

    public string ProtectToken(string token) => _token.Protect(token);

    public string UnprotectToken(string protectedToken) => _token.Unprotect(protectedToken);

    public string ProtectWebhookSecret(string secret) => _webhook.Protect(secret);

    public string UnprotectWebhookSecret(string protectedSecret) => _webhook.Unprotect(protectedSecret);

    public static string NewWebhookSecret() =>
        "wbgl_" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Makes <see cref="GitLabClient"/>s for the saved connection or for settings being tested.</summary>
public sealed class GitLabClientFactory(IGitLabHandlerFactory handlers, GitLabRateGate gate, GitLabSecrets secrets, IOptions<GitLabOptions> options)
{
    public GitLabClient For(GitLabConnection connection) =>
        Create(connection.BaseUrl, secrets.UnprotectToken(connection.TokenProtected), connection.CaCertificatePem);

    public GitLabClient Create(string baseUrl, string token, string? caPem)
    {
        var handler = handlers.Get(new Uri(baseUrl), caPem);
        return new GitLabClient(handler, baseUrl, token, gate, options.Value);
    }
}

/// <summary>One GitLab server as one token. Calls go through the shared handler (pooled connections); dispose when done.</summary>
public sealed class GitLabClient : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _host;
    private readonly GitLabRateGate _gate;
    private readonly GitLabOptions _options;

    public GitLabClient(HttpMessageHandler handler, string baseUrl, string token, GitLabRateGate gate, GitLabOptions options)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _host = new Uri(_baseUrl).Host;
        _gate = gate;
        _options = options;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 2, 120)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Workbench-GitLab-Sync/1.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("PRIVATE-TOKEN", token.Trim());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string BaseUrl => _baseUrl;

    public Task<GitLabVersion> GetVersionAsync(CancellationToken ct) => GetAsync<GitLabVersion>("version", ct);

    public Task<GitLabUser> GetCurrentUserAsync(CancellationToken ct) => GetAsync<GitLabUser>("user", ct);

    /// <summary>The token's own details (GitLab 16.0+). Null when the server doesn't have the endpoint.</summary>
    public async Task<GitLabTokenInfo?> GetTokenInfoAsync(CancellationToken ct)
    {
        try
        {
            return await GetAsync<GitLabTokenInfo>("personal_access_tokens/self", ct);
        }
        catch (GitLabApiException ex) when (ex.StatusCode is 404 or 403)
        {
            return null;
        }
    }

    public Task<GitLabProject> GetProjectAsync(string idOrPath, CancellationToken ct) => GetAsync<GitLabProject>($"projects/{GitLabUrls.Id(idOrPath)}", ct);

    public async Task<IReadOnlyList<GitLabProject>> SearchProjectsAsync(string? query, CancellationToken ct)
    {
        var q = string.IsNullOrWhiteSpace(query) ? "" : $"&search={Uri.EscapeDataString(query.Trim())}";
        return await GetAsync<List<GitLabProject>>($"projects?membership=true&simple=true&order_by=last_activity_at&per_page=20{q}", ct);
    }

    /// <summary>True when the group has epics (Premium and Ultimate) and the token may read them.</summary>
    public async Task<bool> GroupHasEpicsAsync(long groupId, CancellationToken ct)
    {
        try
        {
            await GetAsync<List<GitLabEpic>>($"groups/{groupId}/epics?per_page=1", ct);
            return true;
        }
        catch (GitLabApiException ex) when (ex.StatusCode is 403 or 404)
        {
            return false;
        }
    }

    public Task<GitLabIssue> GetIssueAsync(long projectId, long iid, CancellationToken ct) => GetAsync<GitLabIssue>($"projects/{projectId}/issues/{iid}", ct);

    /// <summary>An issue whose description carries <paramref name="marker"/> (a task's id), so a create retried after a crash finds its issue.</summary>
    public async Task<GitLabIssue?> FindIssueByMarkerAsync(long projectId, string marker, CancellationToken ct)
    {
        var found = await GetAsync<List<GitLabIssue>>($"projects/{projectId}/issues?in=description&search={Uri.EscapeDataString(marker)}&scope=all&per_page=5", ct);
        return found.FirstOrDefault(i => i.Description?.Contains(marker, StringComparison.Ordinal) == true);
    }

    public Task<IReadOnlyList<GitLabIssue>> ListIssuesAsync(long projectId, DateTime? updatedAfter, CancellationToken ct) =>
        ListAllAsync<GitLabIssue>($"projects/{projectId}/issues?scope=all&state=all&order_by=updated_at&sort=asc&per_page=100{After(updatedAfter)}", ct);

    public Task<GitLabIssue> CreateIssueAsync(long projectId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        SendAsync<GitLabIssue>(HttpMethod.Post, $"projects/{projectId}/issues", fields, ct);

    public Task<GitLabIssue> UpdateIssueAsync(long projectId, long iid, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        SendAsync<GitLabIssue>(HttpMethod.Put, $"projects/{projectId}/issues/{iid}", fields, ct);

    /// <summary>Sets the issue's estimate in hours (null clears it). Time spent is never read: GitLab time doesn't go to timesheets.</summary>
    public async Task SetTimeEstimateAsync(long projectId, long iid, decimal? hours, CancellationToken ct)
    {
        if (hours is not { } h || h <= 0)
        {
            await SendAsync<JsonElement>(HttpMethod.Post, $"projects/{projectId}/issues/{iid}/reset_time_estimate", null, ct);
            return;
        }

        var minutes = (long)Math.Round(h * 60m, MidpointRounding.AwayFromZero);
        await SendAsync<JsonElement>(HttpMethod.Post, $"projects/{projectId}/issues/{iid}/time_estimate?duration={minutes}m", null, ct);
    }

    public Task AddIssueNoteAsync(long projectId, long iid, string body, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"projects/{projectId}/issues/{iid}/notes", new Dictionary<string, object?> { ["body"] = body }, ct);

    public async Task<IReadOnlyList<GitLabLinkedIssue>> ListIssueLinksAsync(long projectId, long iid, CancellationToken ct) =>
        await GetAsync<List<GitLabLinkedIssue>>($"projects/{projectId}/issues/{iid}/links", ct);

    public Task CreateIssueLinkAsync(long projectId, long iid, long targetProjectId, long targetIid, string linkType, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"projects/{projectId}/issues/{iid}/links", new Dictionary<string, object?>
        {
            ["target_project_id"] = targetProjectId.ToString(CultureInfo.InvariantCulture),
            ["target_issue_iid"] = targetIid.ToString(CultureInfo.InvariantCulture),
            ["link_type"] = linkType,
        }, ct);

    public Task DeleteIssueLinkAsync(long projectId, long iid, long issueLinkId, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"projects/{projectId}/issues/{iid}/links/{issueLinkId}", null, ct);

    public Task<GitLabEpic> GetEpicAsync(long groupId, long iid, CancellationToken ct) => GetAsync<GitLabEpic>($"groups/{groupId}/epics/{iid}", ct);

    public Task<IReadOnlyList<GitLabEpic>> ListEpicsAsync(long groupId, DateTime? updatedAfter, CancellationToken ct) =>
        ListAllAsync<GitLabEpic>($"groups/{groupId}/epics?order_by=updated_at&sort=asc&per_page=100{After(updatedAfter)}", ct);

    public Task<GitLabEpic> CreateEpicAsync(long groupId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        SendAsync<GitLabEpic>(HttpMethod.Post, $"groups/{groupId}/epics", fields, ct);

    public Task<GitLabEpic> UpdateEpicAsync(long groupId, long iid, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        SendAsync<GitLabEpic>(HttpMethod.Put, $"groups/{groupId}/epics/{iid}", fields, ct);

    public Task<GitLabMilestone> GetMilestoneAsync(long projectId, long milestoneId, CancellationToken ct) =>
        GetAsync<GitLabMilestone>($"projects/{projectId}/milestones/{milestoneId}", ct);

    public Task<IReadOnlyList<GitLabMilestone>> ListMilestonesAsync(long projectId, CancellationToken ct) =>
        ListAllAsync<GitLabMilestone>($"projects/{projectId}/milestones?per_page=100", ct);

    public Task<GitLabMilestone> CreateMilestoneAsync(long projectId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        SendAsync<GitLabMilestone>(HttpMethod.Post, $"projects/{projectId}/milestones", fields, ct);

    public Task<GitLabMilestone> UpdateMilestoneAsync(long projectId, long milestoneId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        SendAsync<GitLabMilestone>(HttpMethod.Put, $"projects/{projectId}/milestones/{milestoneId}", fields, ct);

    /// <summary>Users matching a name, user name or email (an administrator's token also matches private emails).</summary>
    public async Task<IReadOnlyList<GitLabUser>> SearchUsersAsync(string query, CancellationToken ct) =>
        await GetAsync<List<GitLabUser>>($"users?active=true&per_page=20&search={Uri.EscapeDataString(query.Trim())}", ct);

    public async Task<GitLabUser?> FindUserByUsernameAsync(string username, CancellationToken ct) =>
        (await GetAsync<List<GitLabUser>>($"users?username={Uri.EscapeDataString(username.Trim())}", ct)).FirstOrDefault();

    public Task<GitLabUser> GetUserAsync(long id, CancellationToken ct) => GetAsync<GitLabUser>($"users/{id}", ct);

    public async Task<IReadOnlyList<GitLabUser>> ListProjectMembersAsync(long projectId, string? query, CancellationToken ct) =>
        await GetAsync<List<GitLabUser>>($"projects/{projectId}/members/all?per_page=100{(string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query.Trim()))}", ct);

    public async Task<long> AddProjectHookAsync(long projectId, string url, string secret, CancellationToken ct) =>
        (await SendAsync<GitLabHook>(HttpMethod.Post, $"projects/{projectId}/hooks", HookFields(url, secret, milestones: true), ct)).Id;

    public Task DeleteProjectHookAsync(long projectId, long hookId, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"projects/{projectId}/hooks/{hookId}", null, ct);

    /// <summary>A group webhook (Premium and Ultimate) for epic events, which project webhooks don't send.</summary>
    public async Task<long> AddGroupHookAsync(long groupId, string url, string secret, CancellationToken ct) =>
        (await SendAsync<GitLabHook>(HttpMethod.Post, $"groups/{groupId}/hooks", HookFields(url, secret, milestones: false), ct)).Id;

    public Task DeleteGroupHookAsync(long groupId, long hookId, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"groups/{groupId}/hooks/{hookId}", null, ct);

    /// <summary>
    /// The issue's child tasks (work items) with their weights, through GraphQL (REST v4 doesn't list them). Null when the
    /// server or token can't answer (older GitLab): progress then falls back to the description's checklist.
    /// </summary>
    public async Task<IReadOnlyList<GitLabChildTask>?> GetChildTasksAsync(long issueId, CancellationToken ct)
    {
        const string Query = """
            query($id: WorkItemID!) { workItem(id: $id) { widgets { ... on WorkItemWidgetHierarchy { children(first: 100) { nodes { state widgets { ... on WorkItemWidgetWeight { weight } } } } } } } }
            """;
        try
        {
            var body = new Dictionary<string, object?> { ["query"] = Query, ["variables"] = new Dictionary<string, object?> { ["id"] = $"gid://gitlab/WorkItem/{issueId}" } };
            var doc = await RawAsync(HttpMethod.Post, new Uri($"{_baseUrl}/api/graphql"), body, ct);
            if (!doc.TryGetProperty("data", out var data) || !data.TryGetProperty("workItem", out var item) || item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var children = new List<GitLabChildTask>();
            foreach (var widget in item.GetProperty("widgets").EnumerateArray())
            {
                if (!widget.TryGetProperty("children", out var kids) || !kids.TryGetProperty("nodes", out var nodes))
                {
                    continue;
                }

                foreach (var node in nodes.EnumerateArray())
                {
                    int? weight = null;
                    if (node.TryGetProperty("widgets", out var nodeWidgets))
                    {
                        foreach (var w in nodeWidgets.EnumerateArray())
                        {
                            if (w.TryGetProperty("weight", out var value) && value.ValueKind == JsonValueKind.Number)
                            {
                                weight = value.GetInt32();
                            }
                        }
                    }

                    children.Add(new GitLabChildTask(string.Equals(node.GetProperty("state").GetString(), "CLOSED", StringComparison.OrdinalIgnoreCase), weight));
                }
            }

            return children;
        }
        catch (GitLabApiException ex) when (!ex.IsRetryable || ex.StatusCode is null)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static Dictionary<string, object?> HookFields(string url, string secret, bool milestones)
    {
        var fields = new Dictionary<string, object?>
        {
            ["url"] = url,
            ["token"] = secret,
            ["issues_events"] = true,
            ["confidential_issues_events"] = true,
            ["push_events"] = false,
            ["enable_ssl_verification"] = true,
        };
        if (milestones)
        {
            fields["milestone_events"] = true;
        }
        else
        {
            fields["epic_events"] = true;
        }

        return fields;
    }

    private static string After(DateTime? updatedAfter) =>
        updatedAfter is { } after ? "&updated_after=" + Uri.EscapeDataString(DateTime.SpecifyKind(after, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)) : "";

    private async Task<T> GetAsync<T>(string path, CancellationToken ct) => await SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<IReadOnlyList<T>> ListAllAsync<T>(string path, CancellationToken ct)
    {
        var all = new List<T>();
        var page = 1;
        for (var pages = 0; pages < Math.Max(1, _options.MaxPagesPerReconcile); pages++)
        {
            var (items, next) = await SendPageAsync<T>($"{path}&page={page}", ct);
            all.AddRange(items);
            if (next is not { } n)
            {
                break;
            }

            page = n;
        }

        return all;
    }

    private async Task<(List<T> Items, int? Next)> SendPageAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, GitLabUrls.Api(_baseUrl, path), null, ct);
        var items = await ReadAsync<List<T>>(response, ct) ?? [];
        var next = response.Headers.TryGetValues("X-Next-Page", out var values) && int.TryParse(values.FirstOrDefault(), out var n) && n > 0 ? n : (int?)null;
        return (items, next);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, IReadOnlyDictionary<string, object?>? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, GitLabUrls.Api(_baseUrl, path), body, ct);
        if (typeof(T) == typeof(JsonElement) && (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0))
        {
            return default!;
        }

        return (await ReadAsync<T>(response, ct))!;
    }

    private async Task<JsonElement> RawAsync(HttpMethod method, Uri url, IReadOnlyDictionary<string, object?> body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
        return await ReadAsync<JsonElement>(response, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, Uri url, IReadOnlyDictionary<string, object?>? body, CancellationToken ct)
    {
        await _gate.WaitAsync(_host, ct);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitLabApiException(null, $"{_host} didn't answer in time.", "GitLab may be busy or down. Workbench tries again; if it keeps happening, check the server.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitLabApiException(null, $"Workbench couldn't verify {_host}'s HTTPS certificate.",
                "If your GitLab server uses a certificate from your own certificate authority, paste that authority's certificate (PEM) under Admin › Integrations › GitLab.");
        }
        catch (HttpRequestException ex)
        {
            throw new GitLabApiException(null, $"Workbench couldn't reach {_host}: {ex.Message}",
                ex.Message.Contains("GitLab:AllowedPrivateHosts", StringComparison.Ordinal)
                    ? "This GitLab server is on a private network. Ask your Workbench server operator to add it to GitLab:AllowedPrivateHosts."
                    : "Check the GitLab address, and that this Workbench server can reach it (firewall, VPN or proxy).");
        }

        _gate.Observe(_host, response, _options.RateLimitReserve);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var text = await ReadTextAsync(response, ct);
            throw Failure((int)response.StatusCode, text, GitLabRateGate.RetryAfter(response, DateTimeOffset.UtcNow));
        }
    }

    internal static GitLabApiException Failure(int status, string? body, TimeSpan? retryAfter)
    {
        var detail = Message(body);
        var (message, hint) = status switch
        {
            401 => ("GitLab refused the access token.",
                "The token may have expired or been revoked. Create a new one with the api scope and save it under Admin › Integrations › GitLab."),
            403 => ($"The access token isn't allowed to do this{Suffix(detail)}.",
                "Give the token's user (or the group or project token) at least the Developer role on the GitLab project, Maintainer to add webhooks, and the api scope."),
            404 => ($"GitLab couldn't find it{Suffix(detail)}.",
                "The GitLab project, group or issue may have been moved or deleted, or the token can't see it. Check the link on the project's Setup tab."),
            409 => ($"GitLab reported a conflict{Suffix(detail)}.", "Something with the same name may already exist in GitLab."),
            400 or 422 => ($"GitLab didn't accept the change{Suffix(detail)}.", "Check the task's values (title, dates) on the plan; the sync panel shows which task."),
            429 => ("GitLab's rate limit was reached.", "Nothing to do: Workbench sends the waiting changes once the limit resets."),
            >= 500 => ($"GitLab had a problem ({status}).", "Workbench tries again automatically. If it keeps failing, check GitLab's status."),
            _ => ($"GitLab answered {status}{Suffix(detail)}.", "Workbench tries again for temporary problems; otherwise check the connection settings."),
        };
        return new GitLabApiException(status, message, hint, retryAfter);
    }

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}";

    /// <summary>GitLab's error text (<c>message</c> or <c>error</c>), shortened.</summary>
    private static string? Message(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var text = root.ValueKind == JsonValueKind.Object
                ? root.TryGetProperty("message", out var m) ? m.ToString() : root.TryGetProperty("error", out var e) ? e.ToString() : null
                : null;
            return text is null ? null : text.Length > 300 ? text[..300] : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await ReadTextAsync(response, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new GitLabApiException((int)response.StatusCode, $"GitLab's answer couldn't be read: {ex.Message}",
                "Check that the address is your GitLab server (not a sign-in page or proxy).");
        }
    }

    private static Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct) => response.Content.ReadAsStringAsync(ct);
}
