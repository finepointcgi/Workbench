using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Workbench.Domain.Notifications;
using Workbench.Infrastructure.Notifications;

namespace Workbench.Tests.Notifications;

/// <summary>Signature checks and event mapping for the SendGrid and SES (SNS) bounce webhooks.</summary>
public sealed class EmailWebhookTests
{
    internal const string TopicArn = "arn:aws:sns:us-east-1:111122223333:workbench-ses-events";
    internal const string CertUrl = "https://sns.us-east-1.amazonaws.com/SimpleNotificationService-0000000000000000000000.pem";

    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // --- SendGrid -------------------------------------------------------------------------------------------------

    internal static (ECDsa Key, string PublicKey) SendGridKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    internal static string SignSendGrid(ECDsa key, string timestamp, byte[] body) =>
        Convert.ToBase64String(key.SignData([.. Encoding.UTF8.GetBytes(timestamp), .. body], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    [Fact]
    public void SendGrid_signatures_verify_over_the_timestamp_and_exact_body()
    {
        var (key, publicKey) = SendGridKey();
        var body = Encoding.UTF8.GetBytes("""[{"email":"a@example.com","event":"bounce"}]""");
        var signature = SignSendGrid(key, "1761220800", body);

        Assert.True(SendGridEventWebhook.VerifySignature(publicKey, signature, "1761220800", body));
        Assert.False(SendGridEventWebhook.VerifySignature(publicKey, signature, "1761220801", body)); // Timestamp changed.
        Assert.False(SendGridEventWebhook.VerifySignature(publicKey, signature, "1761220800", [.. body, (byte)' '])); // Body changed.
        Assert.False(SendGridEventWebhook.VerifySignature(SendGridKey().PublicKey, signature, "1761220800", body)); // Someone else's key.
        Assert.False(SendGridEventWebhook.VerifySignature(publicKey, null, "1761220800", body));
        Assert.False(SendGridEventWebhook.VerifySignature(publicKey, "not base64!", "1761220800", body));
        Assert.False(SendGridEventWebhook.VerifySignature("not a key", signature, "1761220800", body));
    }

    [Fact]
    public void SendGrid_bounces_drops_and_spam_reports_map_to_issues_and_other_events_are_ignored()
    {
        var body = Encoding.UTF8.GetBytes("""
            [
              {"email":"gone@example.com","event":"bounce","type":"bounce","status":"5.1.1","reason":"550 5.1.1 user unknown","timestamp":1761220800,"wb_tenant":"acme"},
              {"email":"busy@example.com","event":"bounce","type":"blocked","reason":"421 try later","timestamp":1761220800},
              {"email":"angry@example.com","event":"spamreport","timestamp":1761220800},
              {"email":"listed@example.com","event":"dropped","reason":"Bounced Address","timestamp":1761220800},
              {"email":"listed2@example.com","event":"dropped","reason":"Spam Reporting Address","timestamp":1761220800},
              {"email":"fine@example.com","event":"delivered","timestamp":1761220800},
              {"email":"fine@example.com","event":"deferred","timestamp":1761220800},
              {"event":"bounce"}
            ]
            """);

        var events = SendGridEventWebhook.Parse(body, Now);

        Assert.Equal(
            [("gone@example.com", EmailIssueKind.Bounce), ("busy@example.com", EmailIssueKind.SendFailure), ("angry@example.com", EmailIssueKind.Complaint),
             ("listed@example.com", EmailIssueKind.Bounce), ("listed2@example.com", EmailIssueKind.Complaint)],
            events.Select(e => (e.Event.Address, e.Event.Kind)));
        Assert.Equal("acme", events[0].Tenant);
        Assert.Null(events[1].Tenant);
        Assert.Equal("5.1.1 550 5.1.1 user unknown", events[0].Event.Detail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761220800).UtcDateTime, events[0].Event.OccurredAtUtc);
        Assert.All(events, e => Assert.Equal("sendgrid", e.Event.Provider));
    }

    [Fact]
    public void SendGrid_payloads_that_are_not_an_array_are_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => SendGridEventWebhook.Parse("""{"email":"a@example.com"}"""u8, Now));
        Assert.ThrowsAny<JsonException>(() => SendGridEventWebhook.Parse("not json"u8, Now));
    }

    // --- SNS / SES ------------------------------------------------------------------------------------------------

    internal static X509Certificate2 SnsCertificate(DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=sns.amazonaws.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>A signed SNS message as SNS would POST it.</summary>
    internal static string SnsJson(X509Certificate2 certificate, string type, string message, string version = "2", string topicArn = TopicArn,
        string certUrl = CertUrl, string? subscribeUrl = null)
    {
        var unsigned = new SnsMessage(type, Guid.NewGuid().ToString(), topicArn, message, "2026-10-23T12:00:00.000Z", version, "", certUrl,
            type == "Notification" ? null : null, type == "Notification" ? null : "token-123", subscribeUrl);
        using var rsa = certificate.GetRSAPrivateKey()!;
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(unsigned.StringToSign()),
            version == "1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var json = new Dictionary<string, string?>
        {
            ["Type"] = type,
            ["MessageId"] = unsigned.MessageId,
            ["TopicArn"] = topicArn,
            ["Message"] = message,
            ["Timestamp"] = unsigned.Timestamp,
            ["SignatureVersion"] = version,
            ["Signature"] = signature,
            ["SigningCertURL"] = certUrl,
        };
        if (type != "Notification")
        {
            json["Token"] = "token-123";
            json["SubscribeURL"] = subscribeUrl;
        }

        return JsonSerializer.Serialize(json);
    }

    internal sealed class FixedCertificates(X509Certificate2 certificate) : ISnsCertificateSource
    {
        public List<Uri> Requested { get; } = [];

        public Task<X509Certificate2> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            Requested.Add(url);
            return Task.FromResult(certificate);
        }
    }

    internal const string SesBounce = """
        {"eventType":"Bounce","bounce":{"bounceType":"Permanent","bounceSubType":"General","timestamp":"2026-10-23T11:59:00.000Z",
         "bouncedRecipients":[{"emailAddress":"Gone@Example.com","diagnosticCode":"smtp; 550 5.1.1 user unknown"}]},
         "mail":{"timestamp":"2026-10-23T11:58:00.000Z","tags":{"wb_tenant":["acme"],"ses:configuration-set":["workbench"]}}}
        """;

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task Sns_messages_verify_with_SHA1_and_SHA256_signatures(string version)
    {
        using var certificate = SnsCertificate();
        var certificates = new FixedCertificates(certificate);
        var verifier = new SnsMessageVerifier(certificates, TimeProvider.System);
        var message = SnsMessage.Parse(Encoding.UTF8.GetBytes(SnsJson(certificate, "Notification", SesBounce, version)));

        Assert.True(await verifier.VerifyAsync(message));
        Assert.Equal([new Uri(CertUrl)], certificates.Requested);
        Assert.False(await verifier.VerifyAsync(message with { Message = message.Message.Replace("Permanent", "Transient") }));
        Assert.False(await verifier.VerifyAsync(message with { SignatureVersion = "3" }));
    }

    [Fact]
    public async Task Subscription_confirmations_sign_the_subscribe_url_and_token()
    {
        using var certificate = SnsCertificate();
        var verifier = new SnsMessageVerifier(new FixedCertificates(certificate), TimeProvider.System);
        var message = SnsMessage.Parse(Encoding.UTF8.GetBytes(SnsJson(certificate, "SubscriptionConfirmation", "You have chosen to subscribe",
            subscribeUrl: "https://sns.us-east-1.amazonaws.com/?Action=ConfirmSubscription&Token=token-123")));

        Assert.True(await verifier.VerifyAsync(message));
        Assert.False(await verifier.VerifyAsync(message with { SubscribeUrl = "https://sns.us-east-1.amazonaws.com/?Action=ConfirmSubscription&Token=other" }));
    }

    [Theory]
    [InlineData("http://sns.us-east-1.amazonaws.com/cert.pem")] // Not https.
    [InlineData("https://sns.us-east-1.amazonaws.com.evil.example/cert.pem")]
    [InlineData("https://evil.example/sns.us-east-1.amazonaws.com/cert.pem")]
    [InlineData("https://s3.amazonaws.com/attacker-bucket/cert.pem")]
    [InlineData("https://sns.us-east-1.amazonaws.com:8443/cert.pem")]
    [InlineData("https://sns.us-east-1.amazonaws.com/cert.txt")]
    public async Task Certificates_are_only_fetched_from_sns_hosts(string certUrl)
    {
        using var certificate = SnsCertificate();
        var certificates = new FixedCertificates(certificate);
        var verifier = new SnsMessageVerifier(certificates, TimeProvider.System);
        var message = SnsMessage.Parse(Encoding.UTF8.GetBytes(SnsJson(certificate, "Notification", SesBounce, certUrl: certUrl)));

        Assert.False(await verifier.VerifyAsync(message));
        Assert.Empty(certificates.Requested);
    }

    [Fact]
    public async Task An_expired_certificate_is_refused()
    {
        using var certificate = SnsCertificate(notAfter: DateTimeOffset.UtcNow.AddDays(1));
        var clock = new FixedClock(DateTimeOffset.UtcNow.AddDays(30));
        var verifier = new SnsMessageVerifier(new FixedCertificates(certificate), clock);

        Assert.False(await verifier.VerifyAsync(SnsMessage.Parse(Encoding.UTF8.GetBytes(SnsJson(certificate, "Notification", SesBounce)))));
    }

    [Fact]
    public void Ses_permanent_bounces_and_complaints_are_issues_and_transient_bounces_are_failed_sends()
    {
        var bounce = Assert.Single(SesEventParser.Parse(SesBounce, Now));
        Assert.Equal(("acme", "Gone@Example.com", EmailIssueKind.Bounce, "ses"), (bounce.Tenant, bounce.Event.Address, bounce.Event.Kind, bounce.Event.Provider));
        Assert.Equal("smtp; 550 5.1.1 user unknown", bounce.Event.Detail);
        Assert.Equal(new DateTime(2026, 10, 23, 11, 59, 0, DateTimeKind.Utc), bounce.Event.OccurredAtUtc);

        var transient = Assert.Single(SesEventParser.Parse(SesBounce.Replace("Permanent", "Transient"), Now));
        Assert.Equal(EmailIssueKind.SendFailure, transient.Event.Kind);

        // Identity notifications use notificationType instead of eventType.
        var complaints = SesEventParser.Parse("""
            {"notificationType":"Complaint","complaint":{"complaintFeedbackType":"abuse","timestamp":"2026-10-23T11:00:00Z",
             "complainedRecipients":[{"emailAddress":"a@example.com"},{"emailAddress":"b@example.com"}]},"mail":{}}
            """, Now);
        Assert.Equal(["a@example.com", "b@example.com"], complaints.Select(c => c.Event.Address));
        Assert.All(complaints, c => Assert.Equal(EmailIssueKind.Complaint, c.Event.Kind));
        Assert.All(complaints, c => Assert.Null(c.Tenant));

        Assert.Empty(SesEventParser.Parse("""{"eventType":"Delivery","delivery":{}}""", Now));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
