namespace Workbench.Contracts.Notifications;

public enum EmailIssueKind
{
    SendFailure = 0,
    Bounce = 1,
    Complaint = 2,
}

/// <summary>
/// An address mail can't reach. <see cref="UserDisplayName"/> is set when a Workbench user has the address; otherwise
/// it belongs to a contact or an outside recipient. <see cref="Suppressed"/>: only essential mail goes to it.
/// </summary>
public sealed record EmailAddressIssueDto(
    string Address, EmailIssueKind Kind, string Provider, string? Detail, DateTime FirstAtUtc, DateTime LastAtUtc, int EventCount,
    bool Suppressed, DateTime? ClearedAtUtc, string? ClearedBy, string? UserDisplayName)
{
    /// <summary>The sentence the UI shows.</summary>
    public string Message => Kind switch
    {
        EmailIssueKind.Complaint => "This address marked a Workbench email as spam, so Workbench only sends it essential email (password resets, invitations).",
        EmailIssueKind.Bounce => "Emails to this address are bouncing, so Workbench only sends it essential email (password resets, invitations).",
        _ => "Recent emails to this address couldn't be delivered.",
    };
}

/// <summary>Addresses that bounce or complained (from the mail provider's webhooks, or SMTP refusals).</summary>
public interface IEmailDeliverabilityService
{
    /// <summary>Open issues (and cleared ones with <paramref name="includeCleared"/>). "Administer users" or "Manage sign-in and security".</summary>
    Task<IReadOnlyList<EmailAddressIssueDto>> ListAsync(bool includeCleared = false, CancellationToken cancellationToken = default);

    /// <summary>The signed-in user's own open issue, or null.</summary>
    Task<EmailAddressIssueDto?> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the issue resolved so normal mail resumes (the mailbox was fixed). Admins can clear any address, people
    /// their own. A new bounce reopens it.
    /// </summary>
    Task ClearAsync(string address, CancellationToken cancellationToken = default);
}
