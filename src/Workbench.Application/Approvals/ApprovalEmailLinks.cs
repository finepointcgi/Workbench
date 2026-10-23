using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Domain.Approvals;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Approvals;

/// <summary>What an email Approve or Reject link carries: one task's decision, for one recipient, until it expires.</summary>
public sealed record ApprovalEmailToken(
    Guid Nonce,
    Guid InstanceId,
    Guid TaskId,
    string DocumentType,
    Guid DocumentId,
    ApprovalDecisionKind Decision,
    string Recipient,
    DateTime ExpiresAtUtc);

/// <summary>
/// Signs and reads email link tokens with the host's Data Protection keys (isolated per tenant in MultiTenant mode), so a
/// token can't be forged or edited. A token only names a decision: it's made by a signed-in confirm, never by opening the link.
/// </summary>
public sealed class ApprovalEmailTokens(IDataProtectionProvider provider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = provider.CreateProtector("Workbench.Approvals.EmailLink.v1");

    public string Create(ApprovalEmailToken token) => _protector.Protect(JsonSerializer.Serialize(token, Json));

    public ApprovalEmailToken? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4_000)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ApprovalEmailToken>(_protector.Unprotect(token.Trim()), Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The app-relative confirm page for a token.</summary>
    public static string Link(string token) => $"approvals/email/{token}";
}

/// <summary>
/// The confirm page behind email Approve and Reject links. Every refusal (wrong user, expired, used, no longer waiting) and
/// every decision is audited under entity type <see cref="AuditEntityType"/> with the token's nonce as its id; a decision
/// row is what makes the token used.
/// </summary>
public sealed class ApprovalEmailLinkService(
    IWorkbenchDbContextFactory dbFactory,
    ApprovalEngine engine,
    AccessControl access,
    AuditRecorder audit,
    ApprovalEmailTokens tokens,
    IApprovalInboxService inbox) : IApprovalEmailLinkService
{
    public const string AuditEntityType = "ApprovalEmailLink";
    public const string RefusedAction = "Refused";

    // Two confirms of one link at once (a double click, two tabs): only the first goes ahead.
    private static readonly ConcurrentDictionary<Guid, byte> InFlight = new();

    public async Task<ApprovalEmailLinkDto> OpenAsync(string token, CancellationToken cancellationToken = default) =>
        (await ValidateAsync(token, cancellationToken)).Dto;

    public async Task<ApprovalEmailLinkDto> ConfirmAsync(ConfirmApprovalEmailLinkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (dto, token) = await ValidateAsync(request.Token, cancellationToken);
        if (dto.State != ApprovalEmailLinkState.Ready || token is null)
        {
            return dto;
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (token.Decision == ApprovalDecisionKind.Reject && comment is null)
        {
            throw new ArgumentException("A reason is required when rejecting.");
        }

        if (!InFlight.TryAdd(token.Nonce, 0))
        {
            return dto with { State = ApprovalEmailLinkState.Used, Message = "This link is being used right now." };
        }

        try
        {
            try
            {
                await inbox.DecideAsync(token.DocumentType, token.DocumentId, new ApprovalDecisionRequest(token.Decision, comment), cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or WorkbenchNotFoundException)
            {
                // The module refused (a closed period, a ceiling, segregation of duties): the link stays unused.
                await RecordAsync(token, RefusedAction, ex.Message, cancellationToken);
                throw;
            }

            var done = token.Decision == ApprovalDecisionKind.Reject ? "Rejected" : "Approved";
            await RecordAsync(token, done, comment, cancellationToken);
            return dto with { State = ApprovalEmailLinkState.Done, Message = $"{done}: {dto.Title}." };
        }
        finally
        {
            InFlight.TryRemove(token.Nonce, out _);
        }
    }

    private async Task<(ApprovalEmailLinkDto Dto, ApprovalEmailToken? Token)> ValidateAsync(string? raw, CancellationToken ct)
    {
        if (!access.IsAuthenticated)
        {
            throw new WorkbenchForbiddenException("Sign in to approve from an email link.");
        }

        var token = tokens.Read(raw);
        if (token is null)
        {
            return (new ApprovalEmailLinkDto(ApprovalEmailLinkState.Invalid,
                "This link isn't valid. It may have been cut short by your email program; open the item from your Approvals inbox instead."), null);
        }

        var me = ApprovalEngine.Normalize(access.Email);
        if (me != ApprovalEngine.Normalize(token.Recipient))
        {
            // Nothing about the document is shown to someone the email wasn't for.
            await RecordAsync(token, RefusedAction, $"Opened by {me}; the link was sent to {token.Recipient}", ct);
            return (new ApprovalEmailLinkDto(ApprovalEmailLinkState.WrongUser,
                $"This link was sent to someone else. You're signed in as {me}: sign out and back in as the person the email went to."), null);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var instance = await db.Set<ApprovalInstance>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == token.InstanceId, ct);
        var details = new ApprovalEmailLinkDto(ApprovalEmailLinkState.Ready, "", token.Decision, token.DocumentType, ApprovalDocumentTypes.Label(token.DocumentType),
            token.DocumentId, instance?.Title, instance?.Summary, instance?.Amount, instance?.SubmitterEmail, instance?.Link, token.ExpiresAtUtc);

        var used = await db.Set<AuditEvent>().AsNoTracking()
            .AnyAsync(a => a.EntityType == AuditEntityType && a.EntityId == token.Nonce && a.Action != RefusedAction, ct);
        if (used)
        {
            await RecordAsync(token, RefusedAction, "Already used", ct);
            return (details with { State = ApprovalEmailLinkState.Used, Message = "This link has already been used. Each link works once." }, token);
        }

        if (engine.UtcNow > token.ExpiresAtUtc)
        {
            await RecordAsync(token, RefusedAction, "Expired", ct);
            return (details with { State = ApprovalEmailLinkState.Expired, Message = "This link has expired. Decide from your Approvals inbox instead." }, token);
        }

        var task = await db.Set<ApprovalTask>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == token.TaskId, ct);
        var waiting = instance is { Status: ApprovalInstanceStatus.Pending } && task is { Status: ApprovalTaskStatus.Pending }
            && (await engine.ActionableForAsync(db, token.DocumentType, token.DocumentId, me, null, ct)).Any(t => t.Id == token.TaskId);
        if (!waiting)
        {
            await RecordAsync(token, RefusedAction, "No longer waiting on the recipient", ct);
            return (details with
            {
                State = ApprovalEmailLinkState.NoLongerWaiting,
                Message = "This is no longer waiting on you: it was already decided, recalled or changed since the email was sent.",
            }, token);
        }

        return (details with { Message = token.Decision == ApprovalDecisionKind.Reject ? "Reject this? Say why, then confirm." : "Approve this? Confirm to approve." }, token);
    }

    private async Task RecordAsync(ApprovalEmailToken token, string action, string? reason, CancellationToken ct)
    {
        // Saved on its own: a refusal is recorded even though nothing else changes.
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        if (action == RefusedAction)
        {
            // A page renders twice (prerendered, then live) and people refresh: one row per refusal a minute is enough.
            var since = engine.UtcNow.AddMinutes(-1);
            var actor = access.Email;
            if (await db.Set<AuditEvent>().AsNoTracking().AnyAsync(a => a.EntityType == AuditEntityType && a.EntityId == token.Nonce
                    && a.Action == RefusedAction && a.Actor == actor && a.Reason == reason && a.OccurredAtUtc >= since, CancellationToken.None))
            {
                return;
            }
        }

        audit.Record(db, AuditEntityType, token.Nonce, action, null,
            new { token.DocumentType, token.DocumentId, token.TaskId, Decision = token.Decision.ToString(), token.Recipient }, reason);
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>
/// Adds a "Waiting on you" block with Approve and Reject links to notification emails: in one notification's email, the
/// approvals that became the recipient's when it was sent (or all of them for approval reminders); in a digest, everything
/// waiting on them. Each link opens the confirm page.
/// </summary>
public sealed class ApprovalEmailActions(
    IWorkbenchDbContextFactory dbFactory,
    ApprovalEngine engine,
    ApprovalEmailTokens tokens,
    NotificationOptions options,
    ILogger<ApprovalEmailActions>? logger = null) : INotificationEmailExtras
{
    private const int SingleMax = 5;
    private const int DigestMax = 10;

    /// <summary>Tasks activated this long before a notification count as the reason it was sent.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<EmailExtra?> ForSingleAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var reminder = notification.Kind.StartsWith("approval.", StringComparison.Ordinal);
        return await BuildAsync(notification.RecipientEmail, SingleMax,
            t => reminder || (t.ActivatedAtUtc is { } at && at >= notification.CreatedAtUtc - Window && at <= notification.CreatedAtUtc + TimeSpan.FromMinutes(1)),
            cancellationToken);
    }

    public Task<EmailExtra?> ForDigestAsync(string recipient, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken = default) =>
        BuildAsync(recipient, DigestMax, _ => true, cancellationToken);

    private async Task<EmailExtra?> BuildAsync(string recipient, int max, Func<ApprovalTask, bool> include, CancellationToken ct)
    {
        try
        {
            var me = ApprovalEngine.Normalize(recipient);
            await using var db = await dbFactory.CreateAsync(ct);
            var items = (await engine.ActionableAsync(db, null, me, ct))
                .Select(x => (x.Instance, Task: x.Tasks.Where(t => include(t.Task)).OrderBy(t => t.Task.ActivatedAtUtc).Select(t => t.Task).FirstOrDefault()))
                .Where(x => x.Task is not null)
                .OrderBy(x => x.Task!.ActivatedAtUtc)
                .ToList();
            if (items.Count == 0)
            {
                return null;
            }

            var expires = engine.UtcNow + ApprovalEmailTokens.Lifetime;
            var text = new StringBuilder().AppendLine("Waiting on you:");
            var html = new StringBuilder()
                .Append("<h2 style=\"font-size:17px;margin:20px 0 8px\">Waiting on you</h2>")
                .Append("<ul style=\"padding-left:20px;margin:0 0 12px\">");
            foreach (var (instance, task) in items.Take(max))
            {
                var approve = Link(instance, task!, me, ApprovalDecisionKind.Approve, expires);
                var reject = Link(instance, task!, me, ApprovalDecisionKind.Reject, expires);
                var label = $"{ApprovalDocumentTypes.Label(instance.DocumentType)}: {instance.Title}";
                text.Append("- ").AppendLine(label);
                if (instance.Summary is { Length: > 0 } summary)
                {
                    text.Append("  ").AppendLine(summary.ReplaceLineEndings(" "));
                }

                text.Append("  Approve: ").AppendLine(approve).Append("  Reject: ").AppendLine(reject);
                html.Append("<li style=\"margin:0 0 12px\"><strong>").Append(NotificationEmails.Encode(label)).Append("</strong>");
                if (instance.Summary is { Length: > 0 } s)
                {
                    html.Append("<br>").Append(NotificationEmails.Encode(s));
                }

                html.Append("<br><a href=\"").Append(NotificationEmails.Encode(approve))
                    .Append("\" style=\"display:inline-block;margin:6px 8px 0 0;padding:6px 14px;background:#0b57d0;color:#ffffff;border-radius:4px;text-decoration:none\">Approve</a>")
                    .Append("<a href=\"").Append(NotificationEmails.Encode(reject))
                    .Append("\" style=\"display:inline-block;margin:6px 0 0;padding:5px 13px;border:1px solid #b3261e;color:#b3261e;border-radius:4px;text-decoration:none\">Reject</a></li>");
            }

            html.Append("</ul>");
            var more = items.Count - max;
            var inboxLink = NotificationEmails.AbsoluteLink("approvals", options) ?? "approvals";
            var note = (more > 0 ? $"And {more} more in your Approvals inbox. " : "")
                + $"Each link opens Workbench to confirm, works once and expires on {expires:MMM d}.";
            text.AppendLine(note).Append("Approvals inbox: ").Append(inboxLink);
            html.Append("<p style=\"margin:0 0 16px;font-size:13px;color:#444746\">").Append(NotificationEmails.Encode(note))
                .Append(" <a href=\"").Append(NotificationEmails.Encode(inboxLink)).Append("\" style=\"color:#0b57d0\">Approvals inbox</a></p>");
            return new EmailExtra(text.ToString(), html.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The email still goes out, just without the links.
            _log.LogWarning(ex, "Couldn't add approval links to an email for {Recipient}.", recipient);
            return null;
        }
    }

    private string Link(ApprovalInstance instance, ApprovalTask task, string recipient, ApprovalDecisionKind decision, DateTime expires)
    {
        var token = tokens.Create(new ApprovalEmailToken(Guid.NewGuid(), instance.Id, task.Id, instance.DocumentType, instance.DocumentId, decision, recipient, expires));
        var link = ApprovalEmailTokens.Link(token);
        return NotificationEmails.AbsoluteLink(link, options) ?? link;
    }
}
