using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Application.Operations;
using Workbench.Application.Tenancy;
using Workbench.Domain.Notifications;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Infrastructure.Notifications;

/// <summary>Names Workbench attaches to outgoing mail so the provider's events come back to the right tenant.</summary>
public static class EmailEventTags
{
    /// <summary>The tenant slug: a SendGrid custom argument and an SES message tag. Absent in SingleTenant mode.</summary>
    public const string Tenant = "wb_tenant";

    public static string? CurrentTenant => TenantScope.Current?.Slug;
}

/// <summary>A provider event with the tenant it was tagged with (null when the mail carried no tag).</summary>
public sealed record TaggedEmailEvent(string? Tenant, EmailDeliveryEvent Event);

/// <summary>
/// Holds back non-essential mail to addresses that bounce or complained (<see cref="EmailAddressIssue"/>), and records
/// recipients an SMTP relay refuses (SMTP has no bounce webhook). Lookups that fail let the mail through: a missing
/// suppression check must never stop a password reset or a notification.
/// </summary>
public sealed class SuppressingEmailSender(IEmailSender inner, string provider, IServiceProvider services, TimeProvider clock, ILogger<SuppressingEmailSender> logger)
    : IEmailSender
{
    public IEmailSender Inner => inner;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var recorder = CanUseTenantDatabase() ? services.GetService<EmailDeliverabilityRecorder>() : null;
        if (!message.Essential && recorder is not null)
        {
            bool suppressed;
            try
            {
                suppressed = await recorder.IsSuppressedAsync(message.To, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't check the email suppression list; sending anyway.");
                suppressed = false;
            }

            if (suppressed)
            {
                // Never log the address itself (docs/iso/secure-development-policy.md).
                logger.LogInformation("Held back a non-essential email: the address bounces or complained.");
                WorkbenchTelemetry.EmailsSent.Add(1, new("email.provider", provider), new("outcome", "suppressed"));
                return;
            }
        }

        try
        {
            await inner.SendAsync(message, cancellationToken);
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && recorder is not null)
        {
            // 5xx: the relay says the mailbox doesn't exist or won't take mail (a hard bounce); 4xx: try again later.
            var kind = (int)ex.StatusCode >= 500 ? EmailIssueKind.Bounce : EmailIssueKind.SendFailure;
            try
            {
                await recorder.RecordAsync([new EmailDeliveryEvent(message.To, kind, "smtp", $"{(int)ex.StatusCode} {ex.Message}", clock.GetUtcNow().UtcDateTime)], CancellationToken.None);
            }
            catch (Exception recordError) when (recordError is not OperationCanceledException)
            {
                logger.LogWarning(recordError, "Couldn't record a refused recipient.");
            }

            throw;
        }
    }

    /// <summary>MultiTenant code outside a tenant (the host site) has no tenant database to look in.</summary>
    private bool CanUseTenantDatabase() => TenantScope.Current is not null || services.GetService<TenancyOptions>() is not { IsMultiTenant: true };
}

/// <summary>
/// Records provider events in the right tenant's database: the tenant tag on the mail, else the tenant of the request
/// (a webhook URL on a tenant's own address), else (SingleTenant) the one database. MultiTenant events without a known
/// tenant are dropped.
/// </summary>
public sealed class EmailEventRecorder(IServiceProvider services, TenancyOptions tenancy, ILogger<EmailEventRecorder> logger)
{
    public async Task<int> RecordAsync(IReadOnlyCollection<TaggedEmailEvent> events, CancellationToken cancellationToken = default)
    {
        var recorder = services.GetRequiredService<EmailDeliverabilityRecorder>();
        var recorded = 0;
        foreach (var group in events.GroupBy(e => e.Tenant ?? TenantScope.Current?.Slug))
        {
            var batch = group.Select(e => e.Event).ToList();
            if (!tenancy.IsMultiTenant)
            {
                recorded += await recorder.RecordAsync(batch, cancellationToken);
                continue;
            }

            var entry = group.Key is { Length: > 0 } slug ? await services.GetRequiredService<TenantCatalog>().FindAsync(slug, cancellationToken) : null;
            if (entry is null)
            {
                logger.LogWarning("Dropped {Count} email events without a known tenant.", batch.Count);
                continue;
            }

            using (TenantScope.Enter(entry.Info))
            {
                recorded += await recorder.RecordAsync(batch, cancellationToken);
            }
        }

        return recorded;
    }
}

/// <summary>
/// SendGrid's signed Event Webhook (docs: "Getting Started with the Event Webhook Security Features"). The request is
/// signed with ECDSA P-256 over the timestamp header followed by the raw body; the verification key is the public key
/// SendGrid shows when signing is turned on (<c>Email:SendGrid:WebhookVerificationKey</c>).
/// </summary>
public static class SendGridEventWebhook
{
    public const string SignatureHeader = "X-Twilio-Email-Event-Webhook-Signature";
    public const string TimestampHeader = "X-Twilio-Email-Event-Webhook-Timestamp";

    public static bool VerifySignature(string publicKeyBase64, string? signatureBase64, string? timestamp, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(timestamp))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            var signed = new byte[Encoding.UTF8.GetByteCount(timestamp) + body.Length];
            var written = Encoding.UTF8.GetBytes(timestamp, signed);
            body.CopyTo(signed.AsSpan(written));
            return key.VerifyData(signed, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bounces (not "blocked", which is temporary), drops of addresses already on SendGrid's bounce or spam lists, and
    /// spam reports. Delivered, opened and deferred events are ignored.
    /// </summary>
    public static IReadOnlyList<TaggedEmailEvent> Parse(ReadOnlySpan<byte> body, DateTime nowUtc)
    {
        using var json = JsonDocument.Parse(body.ToArray());
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("SendGrid sends an array of events.");
        }

        var events = new List<TaggedEmailEvent>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Str(item, "email") is not { } address)
            {
                continue;
            }

            var type = Str(item, "event")?.ToLowerInvariant();
            var reason = Str(item, "reason");
            EmailIssueKind? kind = type switch
            {
                "bounce" when Str(item, "type")?.Equals("blocked", StringComparison.OrdinalIgnoreCase) == true => EmailIssueKind.SendFailure,
                "bounce" => EmailIssueKind.Bounce,
                "spamreport" => EmailIssueKind.Complaint,
                "dropped" when reason?.Contains("spam", StringComparison.OrdinalIgnoreCase) == true => EmailIssueKind.Complaint,
                "dropped" when reason?.Contains("bounce", StringComparison.OrdinalIgnoreCase) == true || reason?.Contains("invalid", StringComparison.OrdinalIgnoreCase) == true => EmailIssueKind.Bounce,
                _ => null,
            };
            if (kind is null)
            {
                continue;
            }

            var at = item.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : nowUtc;
            var detail = string.Join(" ", new[] { Str(item, "status"), reason ?? Str(item, "response") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            events.Add(new TaggedEmailEvent(Str(item, EmailEventTags.Tenant), new EmailDeliveryEvent(address, kind.Value, "sendgrid", detail, at)));
        }

        return events;
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;
}

/// <summary>An Amazon SNS HTTP(S) message: a subscription confirmation or a notification carrying an SES event.</summary>
public sealed record SnsMessage(
    string Type, string MessageId, string TopicArn, string Message, string Timestamp, string SignatureVersion, string Signature,
    string SigningCertUrl, string? Subject, string? Token, string? SubscribeUrl)
{
    public static SnsMessage Parse(ReadOnlySpan<byte> body)
    {
        using var json = JsonDocument.Parse(body.ToArray());
        var root = json.RootElement;
        string Req(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new JsonException($"The SNS message has no {name}.");
        string? Opt(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new SnsMessage(Req("Type"), Req("MessageId"), Req("TopicArn"), Req("Message"), Req("Timestamp"), Opt("SignatureVersion") ?? "1",
            Req("Signature"), Req("SigningCertURL"), Opt("Subject"), Opt("Token"), Opt("SubscribeURL"));
    }

    /// <summary>The string SNS signs (the "Verifying the signatures of Amazon SNS messages" page): name/value lines in a fixed order.</summary>
    public string StringToSign()
    {
        var text = new StringBuilder();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                text.Append(name).Append('\n').Append(value).Append('\n');
            }
        }

        Add("Message", Message);
        Add("MessageId", MessageId);
        if (Type == "Notification")
        {
            Add("Subject", Subject);
            Add("Timestamp", Timestamp);
            Add("TopicArn", TopicArn);
            Add("Type", Type);
        }
        else
        {
            Add("SubscribeURL", SubscribeUrl);
            Add("Timestamp", Timestamp);
            Add("Token", Token);
            Add("TopicArn", TopicArn);
            Add("Type", Type);
        }

        return text.ToString();
    }
}

/// <summary>Where SNS signing certificates come from: downloaded over HTTPS from SNS itself, and cached.</summary>
public interface ISnsCertificateSource
{
    Task<X509Certificate2> GetAsync(Uri url, CancellationToken cancellationToken);
}

public sealed class HttpSnsCertificateSource(HttpClient http) : ISnsCertificateSource
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.Ordinal);

    public HttpSnsCertificateSource()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
    }

    public async Task<X509Certificate2> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(url.AbsoluteUri, out var cached) && cached.NotAfter > DateTime.Now)
        {
            return cached;
        }

        var pem = await http.GetStringAsync(url, cancellationToken);
        return _cache[url.AbsoluteUri] = X509Certificate2.CreateFromPem(pem);
    }
}

/// <summary>
/// Verifies SNS messages: the certificate URL must be an https address on an SNS host (sns.&lt;region&gt;.amazonaws.com),
/// the certificate must be in date, and its RSA key must verify the signature (SHA1 for SignatureVersion 1, SHA256 for 2).
/// Only topics listed in <c>Email:Ses:BounceTopicArns</c> are accepted: anyone can create an SNS topic, and SNS signs
/// their messages too.
/// </summary>
public sealed partial class SnsMessageVerifier(ISnsCertificateSource certificates, TimeProvider clock)
{
    public static bool IsSnsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && SnsHost().IsMatch(uri.Host);

    public async Task<bool> VerifyAsync(SnsMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsSnsUrl(message.SigningCertUrl) || !message.SigningCertUrl.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hash = message.SignatureVersion switch
        {
            "1" => HashAlgorithmName.SHA1,
            "2" => HashAlgorithmName.SHA256,
            _ => (HashAlgorithmName?)null,
        };
        if (hash is null)
        {
            return false;
        }

        try
        {
            var certificate = await certificates.GetAsync(new Uri(message.SigningCertUrl), cancellationToken);
            var now = clock.GetLocalNow().DateTime;
            if (now < certificate.NotBefore || now > certificate.NotAfter)
            {
                return false;
            }

            using var rsa = certificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(
                Encoding.UTF8.GetBytes(message.StringToSign()), Convert.FromBase64String(message.Signature), hash.Value, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^sns\.[a-z0-9-]+\.amazonaws\.com(\.cn)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SnsHost();
}

/// <summary>
/// SES bounce and complaint events, as published to SNS by a configuration set (<c>eventType</c>) or by identity
/// notifications (<c>notificationType</c>). Permanent bounces and complaints are recorded; transient bounces count as
/// failed sends.
/// </summary>
public static class SesEventParser
{
    public static IReadOnlyList<TaggedEmailEvent> Parse(string message, DateTime nowUtc)
    {
        using var json = JsonDocument.Parse(message);
        var root = json.RootElement;
        var type = Str(root, "eventType") ?? Str(root, "notificationType");
        var tenant = root.TryGetProperty("mail", out var mail) && mail.TryGetProperty("tags", out var tags)
            && tags.TryGetProperty(EmailEventTags.Tenant, out var values) && values.ValueKind == JsonValueKind.Array
            && values.GetArrayLength() > 0 && values[0].ValueKind == JsonValueKind.String
                ? values[0].GetString()
                : null;
        var events = new List<TaggedEmailEvent>();
        if (type == "Bounce" && root.TryGetProperty("bounce", out var bounce))
        {
            var permanent = Str(bounce, "bounceType") == "Permanent";
            var at = Time(bounce, nowUtc);
            foreach (var r in Array(bounce, "bouncedRecipients"))
            {
                if (Str(r, "emailAddress") is { } address)
                {
                    var detail = Str(r, "diagnosticCode") ?? $"{Str(bounce, "bounceType")} {Str(bounce, "bounceSubType")}".Trim();
                    events.Add(new TaggedEmailEvent(tenant, new EmailDeliveryEvent(address, permanent ? EmailIssueKind.Bounce : EmailIssueKind.SendFailure, "ses", detail, at)));
                }
            }
        }
        else if (type == "Complaint" && root.TryGetProperty("complaint", out var complaint))
        {
            var at = Time(complaint, nowUtc);
            var detail = Str(complaint, "complaintFeedbackType") is { } feedback ? $"Complaint: {feedback}" : "Marked as spam";
            foreach (var r in Array(complaint, "complainedRecipients"))
            {
                if (Str(r, "emailAddress") is { } address)
                {
                    events.Add(new TaggedEmailEvent(tenant, new EmailDeliveryEvent(address, EmailIssueKind.Complaint, "ses", detail, at)));
                }
            }
        }

        return events;
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [];

    private static DateTime Time(JsonElement parent, DateTime fallback) =>
        Str(parent, "timestamp") is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : fallback;

    private static string? Str(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
