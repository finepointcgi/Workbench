using System.Net;
using System.Text.Json.Nodes;
using MailKit.Security;
using Workbench.Application.Notifications;
using Workbench.Infrastructure.Notifications;

namespace Workbench.Tests.Operations;

public sealed class EmailProviderTests
{
    [Theory]
    [InlineData("Auto", 587, true, SecureSocketOptions.StartTls)]
    [InlineData("Auto", 465, true, SecureSocketOptions.SslOnConnect)]
    [InlineData("Auto", 25, false, SecureSocketOptions.None)]
    [InlineData("StartTls", 2525, false, SecureSocketOptions.StartTls)]
    [InlineData("SslOnConnect", 587, true, SecureSocketOptions.SslOnConnect)]
    [InlineData("None", 587, true, SecureSocketOptions.None)]
    public void Smtp_security_is_required_tls_unless_turned_off(string security, int port, bool enableSsl, SecureSocketOptions expected)
    {
        var settings = new EmailSettings { SmtpSecurity = security, SmtpPort = port, SmtpEnableSsl = enableSsl };
        Assert.Equal(expected, settings.SmtpSocketOptions());
    }

    [Fact]
    public async Task SendGrid_posts_both_bodies_and_attachments_with_the_api_key()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var sender = new SendGridEmailSender(
            new EmailSettings { Mode = "SendGrid", SendGridApiKey = "SG.test-key", From = "noreply@contoso.test", ReplyTo = "help@contoso.test" },
            new HttpClient(handler));

        await sender.SendAsync(new EmailMessage("pm@example.com", "Report ready", "Plain", "<p>Html</p>",
            [new EmailAttachment("report.csv", "text/csv", "a,b"u8.ToArray())]));

        Assert.Equal(SendGridEmailSender.Endpoint, handler.Request!.RequestUri);
        Assert.Equal("Bearer SG.test-key", handler.Request.Headers.Authorization!.ToString());
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("pm@example.com", (string?)body["personalizations"]![0]!["to"]![0]!["email"]);
        Assert.Equal("noreply@contoso.test", (string?)body["from"]!["email"]);
        Assert.Equal("help@contoso.test", (string?)body["reply_to"]!["email"]);
        Assert.Equal(["text/plain", "text/html"], body["content"]!.AsArray().Select(c => (string?)c!["type"]));
        Assert.Equal(Convert.ToBase64String("a,b"u8.ToArray()), (string?)body["attachments"]![0]!["content"]);
        Assert.False((bool)body["tracking_settings"]!["click_tracking"]!["enable"]!);
    }

    [Fact]
    public async Task SendGrid_messages_carry_the_tenant_so_bounces_come_back_to_it()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var sender = new SendGridEmailSender(new EmailSettings { SendGridApiKey = "SG.test-key" }, new HttpClient(handler));

        using (Workbench.Application.Tenancy.TenantScope.Enter(new(Guid.NewGuid(), "acme", "Acme")))
        {
            await sender.SendAsync(new EmailMessage("pm@example.com", "S", "T", "H"));
        }

        Assert.Equal("acme", (string?)JsonNode.Parse(handler.Body!)!["custom_args"]![EmailEventTags.Tenant]);

        await sender.SendAsync(new EmailMessage("pm@example.com", "S", "T", "H"));
        Assert.Null(JsonNode.Parse(handler.Body!)!["custom_args"]); // SingleTenant: no tag.
    }

    [Fact]
    public async Task A_refused_message_fails_and_is_counted_as_a_failed_delivery()
    {
        var status = new EmailDeliveryStatus();
        var sender = new InstrumentedEmailSender(
            new SendGridEmailSender(new EmailSettings { SendGridApiKey = "SG.bad" }, new HttpClient(new RecordingHandler(HttpStatusCode.Unauthorized))),
            "sendgrid", status, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new EmailMessage("pm@example.com", "S", "T", "H")));

        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("SG.bad", error.Message);
        Assert.NotNull(status.LastFailure);
        Assert.Null(status.LastSuccess);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("{\"errors\":[{\"message\":\"denied\"}]}") };
        }
    }
}
