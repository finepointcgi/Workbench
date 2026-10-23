namespace Workbench.Application.Notifications;

/// <summary>An email with a plain-text and an HTML body. Implemented in infrastructure (SMTP or a pickup folder).</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>A file attached to an email (e.g. a scheduled report).</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Content added to a notification email below its message (e.g. Approve and Reject links), as text and HTML.</summary>
public sealed record EmailExtra(string Text, string Html);

/// <summary>Adds content to notification emails as they're sent: one notification's email, or a daily digest.</summary>
public interface INotificationEmailExtras
{
    Task<EmailExtra?> ForSingleAsync(Domain.Notifications.Notification notification, CancellationToken cancellationToken = default);

    Task<EmailExtra?> ForDigestAsync(string recipient, IReadOnlyList<Domain.Notifications.Notification> notifications, CancellationToken cancellationToken = default);
}

/// <summary>Drops every email. Used until infrastructure registers a real sender (and in tests).</summary>
public sealed class NullEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// <see cref="PublicBaseUrl"/> turns app-relative links into absolute ones in emails (config <c>Notifications:PublicBaseUrl</c>).
/// Digests go out once a day after <see cref="DigestHourLocal"/> server time (<c>Notifications:DigestHourLocal</c>).
/// </summary>
public sealed class NotificationOptions
{
    private string? _publicBaseUrl;

    /// <summary>
    /// May contain <c>{tenant}</c> (MultiTenant), replaced by the current tenant's id, e.g. <c>https://{tenant}.workbench.example</c>.
    /// Without a tenant such a template gives null, so links stay relative.
    /// </summary>
    public string? PublicBaseUrl
    {
        get => _publicBaseUrl is { } url && url.Contains("{tenant}", StringComparison.Ordinal)
            ? Tenancy.TenantScope.Current is { } tenant ? url.Replace("{tenant}", tenant.Slug, StringComparison.Ordinal) : null
            : _publicBaseUrl;
        set => _publicBaseUrl = value;
    }

    public int DigestHourLocal { get; set; } = 7;
}
