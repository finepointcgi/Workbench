namespace Workbench.LicenseServer.Mail;

/// <summary>Sends the portal sign-in links and "your key is ready" notices. The shipped implementation only logs: plug a real sender in here.</summary>
public interface IMailer
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>STUB: writes the message to the log instead of sending it. Replace with SMTP/SES/SendGrid before going live.</summary>
public sealed class LogMailer(ILogger<LogMailer> log) : IMailer
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        log.LogWarning("MAIL (not sent, LogMailer stub) to {To}: {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
