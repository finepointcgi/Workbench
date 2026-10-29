namespace Workbench.Domain.Notifications;

/// <summary>
/// The place notifications are also posted to: one incoming-webhook channel per chat product (<see cref="Kind"/> <c>teams</c> today;
/// Slack can reuse the table). The webhook URL is a secret (its path grants posting), so it is stored encrypted and only the host is shown.
/// </summary>
public sealed class ChatChannel : Entity
{
    public required string Kind { get; set; }

    public required string WebhookUrlProtected { get; set; }

    /// <summary>The URL's host, for display. Never the path.</summary>
    public required string Host { get; set; }

    public bool IsActive { get; set; } = true;

    public required string UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? LastPostedAtUtc { get; set; }

    /// <summary>Why the last post failed, in words; null while posts work. Never contains the URL.</summary>
    public string? LastError { get; set; }
}

/// <summary>A person's choice to also receive one group of notifications in the chat channel of <see cref="ChannelKind"/>. No row, no posts.</summary>
public sealed class ChatNotificationChoice : Entity
{
    public required string UserEmail { get; set; }

    public required string ChannelKind { get; set; }

    /// <summary>A notification group key (<c>approvals</c>, <c>decisions</c>, ...).</summary>
    public required string Group { get; set; }
}
