namespace Workbench.Application.Notifications;

/// <summary>An email with a plain-text and an HTML body. Implemented in infrastructure (SMTP or a pickup folder).</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
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
    public string? PublicBaseUrl { get; set; }
    public int DigestHourLocal { get; set; } = 7;
}
