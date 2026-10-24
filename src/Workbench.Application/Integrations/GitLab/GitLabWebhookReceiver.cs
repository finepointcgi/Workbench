using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Domain.Integrations.GitLab;

namespace Workbench.Application.Integrations.GitLab;

public sealed record GitLabWebhookOutcome(int StatusCode, string Message);

/// <summary>
/// GitLab's webhook calls (<c>POST /webhooks/gitlab/{linkId}</c>, anonymous). Each must carry the link's secret in
/// <c>X-Gitlab-Token</c> (compared in constant time). A call already received (GitLab's <c>Idempotency-Key</c> or
/// <c>X-Gitlab-Event-UUID</c>, else the body's hash) is acknowledged and ignored, so retries and replays change nothing.
/// The call itself only records the event: the outbox fetches the issue from GitLab and syncs it, so the payload's content is
/// never trusted for values and GitLab gets its answer quickly.
/// </summary>
public sealed class GitLabWebhookReceiver(IWorkbenchDbContextFactory dbFactory, GitLabSecrets secrets, TimeProvider clock, ILogger<GitLabWebhookReceiver> logger)
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const string TokenHeader = "X-Gitlab-Token";
    public const string EventHeader = "X-Gitlab-Event";
    public const string EventUuidHeader = "X-Gitlab-Event-UUID";
    public const string IdempotencyHeader = "Idempotency-Key";

    public async Task<GitLabWebhookOutcome> ReceiveAsync(Guid linkId, string? token, string? idempotencyKey, string? eventUuid, string body, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var link = await db.Set<GitLabProjectLink>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
        {
            return new(404, "Not found.");
        }

        if (!TokenMatches(token, secrets.UnprotectWebhookSecret(link.WebhookSecretProtected)))
        {
            logger.LogWarning("Rejected a GitLab webhook for link {LinkId} with a missing or wrong X-Gitlab-Token.", linkId);
            return new(401, "Invalid token.");
        }

        GitLabOutbox.PullData? pull;
        string kind;
        try
        {
            (kind, pull) = Parse(link, body);
        }
        catch (JsonException)
        {
            return new(400, "Expected a GitLab webhook (JSON).");
        }

        var key = Clip(idempotencyKey) ?? Clip(eventUuid) ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var now = clock.GetUtcNow().UtcDateTime;
        if (await db.Set<GitLabWebhookReceipt>().AnyAsync(r => r.LinkId == linkId && r.DedupKey == key, ct))
        {
            return new(200, "Already received.");
        }

        db.Set<GitLabWebhookReceipt>().Add(new GitLabWebhookReceipt
        {
            LinkId = linkId, DedupKey = key, EventKind = kind, ReceivedAtUtc = now, Outcome = pull is null ? "Ignored" : "Queued",
        });
        if (pull is not null)
        {
            GitLabOutbox.RaisePull(db, pull, now);
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

    /// <summary>The event's kind and what to sync, or null for events the sync doesn't use (or for another project).</summary>
    internal static (string Kind, GitLabOutbox.PullData? Pull) Parse(GitLabProjectLink link, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var kind = Str(root, "object_kind") ?? Str(root, "event_type") ?? "unknown";
        if (!root.TryGetProperty("object_attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return (kind, null);
        }

        var id = Long(attrs, "id");
        var iid = Long(attrs, "iid");
        var action = Str(attrs, "action");
        var type = Str(attrs, "type");
        var projectId = root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object ? Long(project, "id") : Long(attrs, "project_id");
        var groupId = Long(attrs, "group_id") ?? (root.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.Object ? Long(group, "id") ?? Long(group, "group_id") : null);

        var objectKind = kind switch
        {
            "issue" => "issue",
            "work_item" when string.Equals(type, "Epic", StringComparison.OrdinalIgnoreCase) => "epic",
            "work_item" when type is null || string.Equals(type, "Issue", StringComparison.OrdinalIgnoreCase) => "issue",
            "epic" => "epic",
            "milestone" => "milestone",
            _ => null,
        };
        if (objectKind is null || iid is null && id is null)
        {
            return (kind, null);
        }

        var ours = objectKind == "epic" ? groupId is null || groupId == link.GitLabGroupId : projectId is null || projectId == link.GitLabProjectId;
        return ours ? (kind, new GitLabOutbox.PullData(link.Id, objectKind, id ?? 0, iid ?? 0, action)) : (kind, null);
    }

    internal static bool TokenMatches(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    private static string? Clip(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim().Length > 128 ? key.Trim()[..128] : key.Trim();

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
