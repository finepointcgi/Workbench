namespace Workbench.Domain.Notifications;

/// <summary>
/// An email address the mail provider reported trouble with: a hard bounce, a spam complaint, or (SMTP, which has no
/// bounce webhook) a recipient the relay refused. One row per address, whoever it belongs to (a user, a CRM or vendor
/// contact, a report subscriber). While <see cref="IsSuppressed"/>, only essential mail (password resets, invitations,
/// sign-in links) is sent to it. Clearing it (an admin, or the person after fixing their mailbox) keeps the row as history.
/// </summary>
public sealed class EmailAddressIssue : Entity
{
    /// <summary>Lower-case, unique.</summary>
    public required string Address { get; set; }

    public EmailIssueKind Kind { get; set; }

    /// <summary><c>sendgrid</c>, <c>ses</c> or <c>smtp</c>.</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's reason (e.g. "550 5.1.1 user unknown"), trimmed. Never message content.</summary>
    public string? Detail { get; set; }

    public DateTime FirstAtUtc { get; set; }
    public DateTime LastAtUtc { get; set; }

    /// <summary>Events recorded since the issue was last opened.</summary>
    public int EventCount { get; set; }

    public DateTime? ClearedAtUtc { get; set; }
    public string? ClearedBy { get; set; }

    public bool IsOpen => ClearedAtUtc is null;

    /// <summary>Non-essential mail stops: a hard bounce or a complaint that nobody has cleared.</summary>
    public bool IsSuppressed => IsOpen && Kind is EmailIssueKind.Bounce or EmailIssueKind.Complaint;
}

/// <summary>Ordered by severity: a complaint outranks a bounce, which outranks a failed send.</summary>
public enum EmailIssueKind
{
    /// <summary>A temporary or unconfirmed problem (a soft bounce, a relay that refused the recipient with a 4xx). Recorded, not suppressed.</summary>
    SendFailure = 0,

    /// <summary>The mailbox doesn't exist or permanently refuses mail.</summary>
    Bounce = 1,

    /// <summary>The recipient marked a Workbench email as spam.</summary>
    Complaint = 2,
}
