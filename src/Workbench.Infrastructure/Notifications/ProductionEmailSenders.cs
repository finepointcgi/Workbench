using System.Net.Http.Headers;
using System.Net.Http.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Workbench.Application.Notifications;
using Workbench.Application.Operations;

namespace Workbench.Infrastructure.Notifications;

/// <summary>Config section <c>Email</c>: how Workbench sends mail. See docs/operations/email.md.</summary>
public sealed class EmailSettings
{
    public const string DefaultPickupFolder = "App_Data/mail";

    /// <summary><c>Pickup</c> (write .eml files; development), <c>Smtp</c>, <c>SendGrid</c> or <c>Ses</c>.</summary>
    public string Mode { get; set; } = "Pickup";

    public string From { get; set; } = "workbench@localhost";
    public string FromName { get; set; } = "Workbench";

    /// <summary>Optional Reply-To, e.g. a monitored support mailbox (notification emails come from a no-reply address).</summary>
    public string? ReplyTo { get; set; }

    public string PickupFolder { get; set; } = DefaultPickupFolder;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 25;
    public bool SmtpEnableSsl { get; set; } = true;

    /// <summary>
    /// <c>Auto</c> (default: implicit TLS on port 465, otherwise STARTTLS, required when <see cref="SmtpEnableSsl"/>),
    /// <c>StartTls</c>, <c>SslOnConnect</c> or <c>None</c> (plain text; refused in Production).
    /// </summary>
    public string SmtpSecurity { get; set; } = "Auto";

    public string? SmtpUserName { get; set; }
    public string? SmtpPassword { get; set; }
    public string? SendGridApiKey { get; set; }

    /// <summary>SES region (e.g. <c>us-gov-west-1</c>); the SDK default (AWS_REGION) when empty.</summary>
    public string? SesRegion { get; set; }

    /// <summary>SES configuration set: where bounce and complaint events are published (docs/operations/email.md).</summary>
    public string? SesConfigurationSet { get; set; }

    /// <summary>Optional static credentials; prefer an IAM role (the SDK's default credential chain).</summary>
    public string? SesAccessKeyId { get; set; }
    public string? SesSecretAccessKey { get; set; }

    public SecureSocketOptions SmtpSocketOptions() => SmtpSecurity.ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "starttls" => SecureSocketOptions.StartTls,
        "sslonconnect" => SecureSocketOptions.SslOnConnect,
        _ when !SmtpEnableSsl => SecureSocketOptions.None,
        _ => SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
    };
}

internal static class MimeMessages
{
    public static MimeMessage Build(EmailMessage message, EmailSettings settings)
    {
        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(new MailboxAddress(settings.FromName, settings.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (!string.IsNullOrWhiteSpace(settings.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(settings.ReplyTo));
        }

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var file in message.Attachments ?? [])
        {
            body.Attachments.Add(file.FileName, file.Content, ContentType.Parse(file.ContentType));
        }

        mime.Body = body.ToMessageBody();
        return mime;
    }
}

/// <summary>
/// Sends through an SMTP relay with MailKit (config <c>Email:Smtp:*</c>). TLS is required unless
/// <c>Email:Smtp:Security</c> is <c>None</c>: STARTTLS fails rather than falling back to plain text.
/// </summary>
public sealed class SmtpEmailSender(EmailSettings settings) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            throw new InvalidOperationException("Email:Smtp:Host isn't configured.");
        }

        using var mime = MimeMessages.Build(message, settings);
        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, settings.SmtpSocketOptions(), cancellationToken);
        if (!string.IsNullOrEmpty(settings.SmtpUserName))
        {
            await client.AuthenticateAsync(settings.SmtpUserName, settings.SmtpPassword ?? "", cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

/// <summary>Sends with the SendGrid v3 Mail Send API (config <c>Email:SendGrid:ApiKey</c>, a key with only "Mail Send" access).</summary>
public sealed class SendGridEmailSender(EmailSettings settings, HttpClient http) : IEmailSender
{
    public static readonly Uri Endpoint = new("https://api.sendgrid.com/v3/mail/send");

    public SendGridEmailSender(EmailSettings settings)
        : this(settings, new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.SendGridApiKey))
        {
            throw new InvalidOperationException("Email:SendGrid:ApiKey isn't configured.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["personalizations"] = new[] { new { to = new[] { new { email = message.To } } } },
            ["from"] = new { email = settings.From, name = settings.FromName },
            ["subject"] = message.Subject,
            ["content"] = new[] { new { type = "text/plain", value = message.TextBody }, new { type = "text/html", value = message.HtmlBody } },
            // Links in notifications carry no tracking: click tracking would rewrite them through SendGrid.
            ["tracking_settings"] = new { click_tracking = new { enable = false }, open_tracking = new { enable = false } },
        };
        if (!string.IsNullOrWhiteSpace(settings.ReplyTo))
        {
            payload["reply_to"] = new { email = settings.ReplyTo };
        }

        if (message.Attachments is { Count: > 0 } files)
        {
            payload["attachments"] = files.Select(f => new { content = Convert.ToBase64String(f.Content), filename = f.FileName, type = f.ContentType }).ToArray();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SendGridApiKey);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The response body names the problem (bad key, unverified sender); it never echoes the key.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"SendGrid refused the message: {(int)response.StatusCode} {detail[..Math.Min(detail.Length, 500)]}");
        }
    }
}

/// <summary>Sends with Amazon SES (v2 API) as a raw MIME message, so attachments work. Credentials: an IAM role, or <c>Email:Ses:AccessKeyId</c>/<c>SecretAccessKey</c>.</summary>
public sealed class SesEmailSender(EmailSettings settings, IAmazonSimpleEmailServiceV2 ses) : IEmailSender
{
    public SesEmailSender(EmailSettings settings)
        : this(settings, CreateClient(settings))
    {
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mime = MimeMessages.Build(message, settings);
        using var raw = new MemoryStream();
        await mime.WriteToAsync(raw, cancellationToken);
        raw.Position = 0;
        await ses.SendEmailAsync(new SendEmailRequest
        {
            Content = new EmailContent { Raw = new RawMessage { Data = raw } },
            ConfigurationSetName = string.IsNullOrWhiteSpace(settings.SesConfigurationSet) ? null : settings.SesConfigurationSet,
        }, cancellationToken);
    }

    private static IAmazonSimpleEmailServiceV2 CreateClient(EmailSettings settings)
    {
        var config = new AmazonSimpleEmailServiceV2Config();
        if (!string.IsNullOrWhiteSpace(settings.SesRegion))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.SesRegion);
        }

        return string.IsNullOrWhiteSpace(settings.SesAccessKeyId)
            ? new AmazonSimpleEmailServiceV2Client(config)
            : new AmazonSimpleEmailServiceV2Client(new BasicAWSCredentials(settings.SesAccessKeyId, settings.SesSecretAccessKey), config);
    }
}

/// <summary>When email last succeeded and failed, for the readiness check and the delivery metrics.</summary>
public sealed class EmailDeliveryStatus
{
    private long _lastSuccessTicks;
    private long _lastFailureTicks;

    public DateTimeOffset? LastSuccess => Read(ref _lastSuccessTicks);
    public DateTimeOffset? LastFailure => Read(ref _lastFailureTicks);

    public void Succeeded(DateTimeOffset at) => Interlocked.Exchange(ref _lastSuccessTicks, at.UtcTicks);
    public void Failed(DateTimeOffset at) => Interlocked.Exchange(ref _lastFailureTicks, at.UtcTicks);

    private static DateTimeOffset? Read(ref long ticks) => Interlocked.Read(ref ticks) is var t && t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
}

/// <summary>Counts deliveries (<c>workbench.email.sent</c>) and remembers the last success and failure; never logs addresses.</summary>
public sealed class InstrumentedEmailSender(IEmailSender inner, string provider, EmailDeliveryStatus status, TimeProvider clock) : IEmailSender
{
    public IEmailSender Inner => inner;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.SendAsync(message, cancellationToken);
            status.Succeeded(clock.GetUtcNow());
            WorkbenchTelemetry.EmailsSent.Add(1, new("email.provider", provider), new("outcome", "success"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.Failed(clock.GetUtcNow());
            WorkbenchTelemetry.EmailsSent.Add(1, new("email.provider", provider), new("outcome", "failure"));
            throw;
        }
    }
}
