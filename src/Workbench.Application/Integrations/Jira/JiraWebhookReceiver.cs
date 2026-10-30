using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Domain.Integrations.Jira;

namespace Workbench.Application.Integrations.Jira;

public sealed record JiraWebhookOutcome(int StatusCode, string Message);

/// <summary>
/// Jira's webhook calls (<c>POST /webhooks/jira/{linkId}</c>, anonymous). Each must prove it knows the link's secret, either by
/// Jira's own signature (<c>X-Hub-Signature: sha256=…</c>, the HMAC-SHA256 of the body, which Jira adds when a secret is set on
/// the webhook) or by the secret itself in <c>X-Workbench-Token</c> (for an automation rule's "send web request", which can set
/// headers). Compared in constant time. A call already received (<c>X-Atlassian-Webhook-Identifier</c>, else the body's hash) is
/// acknowledged and ignored. The call only records the event: the outbox fetches the issue from Jira and syncs it, so the
/// payload's content is never trusted for values and Jira gets its answer quickly.
/// </summary>
public sealed class JiraWebhookReceiver(IWorkbenchDbContextFactory dbFactory, JiraSecrets secrets, TimeProvider clock, ILogger<JiraWebhookReceiver> logger)
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const string SignatureHeader = "X-Hub-Signature";
    public const string TokenHeader = "X-Workbench-Token";
    public const string IdentifierHeader = "X-Atlassian-Webhook-Identifier";

    public async Task<JiraWebhookOutcome> ReceiveAsync(Guid linkId, string? signature, string? token, string? identifier, string body, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var link = await db.Set<JiraProjectLink>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
        {
            return new(404, "Not found.");
        }

        if (!Verified(secrets.UnprotectWebhookSecret(link.WebhookSecretProtected), signature, token, body))
        {
            logger.LogWarning("Rejected a Jira webhook for link {LinkId} with a missing or wrong signature or token.", linkId);
            return new(401, "Invalid signature or token.");
        }

        JiraOutbox.PullData? pull;
        string kind;
        try
        {
            (kind, pull) = Parse(link, body);
        }
        catch (JsonException)
        {
            return new(400, "Expected a Jira webhook (JSON).");
        }

        var key = string.IsNullOrWhiteSpace(identifier) ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))) : identifier.Trim()[..Math.Min(128, identifier.Trim().Length)];
        var now = clock.GetUtcNow().UtcDateTime;
        if (await db.Set<JiraWebhookReceipt>().AnyAsync(r => r.LinkId == linkId && r.DedupKey == key, ct))
        {
            return new(200, "Already received.");
        }

        db.Set<JiraWebhookReceipt>().Add(new JiraWebhookReceipt { LinkId = linkId, DedupKey = key, EventKind = kind, ReceivedAtUtc = now, Outcome = pull is null ? "Ignored" : "Queued" });
        if (pull is not null)
        {
            JiraOutbox.RaisePull(db, pull, now);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return new(200, "Already received."); // the same call arrived twice at once
        }

        return new(200, pull is null ? "Ignored: not an event the sync uses." : "Queued.");
    }

    internal static bool Verified(string secret, string? signature, string? token, string body)
    {
        if (!string.IsNullOrWhiteSpace(signature))
        {
            var given = signature.Trim();
            if (given.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                given = given["sha256=".Length..];
            }

            var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
            return Equal(given.ToLowerInvariant(), expected);
        }

        return token is not null && Equal(token.Trim(), secret);
    }

    /// <summary>The HMAC Jira would send for <paramref name="body"/> (tests and the docs use it).</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>The event's kind and what to sync, or null for events the sync doesn't use (or for another project).</summary>
    internal static (string Kind, JiraOutbox.PullData? Pull) Parse(JiraProjectLink link, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var kind = Str(root, "webhookEvent") ?? "unknown";
        if (kind is not ("jira:issue_created" or "jira:issue_updated" or "jira:issue_deleted") || !root.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object)
        {
            return (kind, null);
        }

        var key = Str(issue, "key");
        var project = issue.TryGetProperty("fields", out var fields) && fields.TryGetProperty("project", out var p) ? Str(p, "key") : null;
        if (!JiraUrls.IsIssueKey(key) || project is not null && !string.Equals(project, link.JiraProjectKey, StringComparison.OrdinalIgnoreCase)
            || project is null && !key!.StartsWith(link.JiraProjectKey + "-", StringComparison.OrdinalIgnoreCase))
        {
            return (kind, null);
        }

        return (kind, new JiraOutbox.PullData(link.Id, key!, kind == "jira:issue_deleted"));
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
