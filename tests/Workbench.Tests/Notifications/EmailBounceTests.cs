using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Workbench.Application;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using Workbench.Infrastructure.Notifications;
using Workbench.Tests.Automation;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;
using DomainKind = Workbench.Domain.Notifications.EmailIssueKind;

namespace Workbench.Tests.Notifications;

/// <summary>What a bounce or complaint changes: who still gets mail, and what admins and the person see.</summary>
public sealed class EmailBounceTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static EmailDeliveryEvent Event(string address, DomainKind kind, DateTime? at = null, string provider = "sendgrid") =>
        new(address, kind, provider, "550 5.1.1 user unknown", at ?? Now);

    private static (SuppressingEmailSender Sender, RecordingEmailSender Inner, EmailDeliverabilityRecorder Recorder, IWorkbenchDbContextFactory Db) Sender(IEmailSender? inner = null)
    {
        var db = TestDb.CreateFactory();
        var recorder = new EmailDeliverabilityRecorder(db);
        var services = new ServiceCollection().AddSingleton(recorder).AddSingleton(new TenancyOptions()).BuildServiceProvider();
        var recording = new RecordingEmailSender();
        return (new SuppressingEmailSender(inner ?? recording, "smtp", services, TimeProvider.System, NullLogger<SuppressingEmailSender>.Instance), recording, recorder, db);
    }

    private static EmailMessage Mail(string to, bool essential = false) => new(to, "Subject", "text", "<p>html</p>") { Essential = essential };

    [Fact]
    public async Task Events_for_one_address_collapse_into_one_row_and_keep_the_worst_kind()
    {
        var (_, _, recorder, db) = Sender();

        await recorder.RecordAsync([Event("Gone@Example.com ", DomainKind.SendFailure, Now.AddMinutes(-5)), Event("gone@example.com", DomainKind.Bounce)]);
        await recorder.RecordAsync([Event("gone@example.com", DomainKind.SendFailure, Now.AddMinutes(1))]);

        await using var context = await db.CreateAsync();
        var row = Assert.Single(await context.Set<EmailAddressIssue>().ToListAsync());
        Assert.Equal("gone@example.com", row.Address);
        Assert.Equal(DomainKind.Bounce, row.Kind);
        Assert.Equal(3, row.EventCount);
        Assert.Equal(Now.AddMinutes(-5), row.FirstAtUtc);
        Assert.Equal(Now.AddMinutes(1), row.LastAtUtc);
        Assert.True(row.IsSuppressed);
    }

    [Fact]
    public async Task Non_essential_mail_to_a_bouncing_address_is_held_back_and_essential_mail_still_goes()
    {
        var (sender, sent, recorder, _) = Sender();
        await recorder.RecordAsync([Event("gone@example.com", DomainKind.Bounce), Event("angry@example.com", DomainKind.Complaint), Event("busy@example.com", DomainKind.SendFailure)]);

        await sender.SendAsync(Mail("gone@example.com"));
        await sender.SendAsync(Mail("GONE@example.com"));
        await sender.SendAsync(Mail("angry@example.com"));
        await sender.SendAsync(Mail("gone@example.com", essential: true));
        await sender.SendAsync(Mail("busy@example.com")); // A failed send alone doesn't stop mail.
        await sender.SendAsync(Mail("fine@example.com"));

        Assert.Equal(["gone@example.com", "busy@example.com", "fine@example.com"], sent.Sent.Select(m => m.To));
        Assert.True(sent.Sent.First().Essential);
    }

    [Fact]
    public async Task Clearing_resumes_mail_old_events_cannot_undo_it_and_a_new_bounce_reopens_it()
    {
        var (sender, sent, recorder, db) = Sender();
        await recorder.RecordAsync([Event("gone@example.com", DomainKind.Bounce, Now.AddHours(-1))]);
        await using (var context = await db.CreateAsync())
        {
            var row = await context.Set<EmailAddressIssue>().SingleAsync();
            row.ClearedAtUtc = Now;
            row.ClearedBy = Admin;
            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await recorder.RecordAsync([Event("gone@example.com", DomainKind.Bounce, Now.AddMinutes(-30))])); // A replayed or late webhook.
        await sender.SendAsync(Mail("gone@example.com"));
        Assert.Single(sent.Sent);

        await recorder.RecordAsync([Event("gone@example.com", DomainKind.Complaint, Now.AddMinutes(10))]);
        await sender.SendAsync(Mail("gone@example.com"));
        Assert.Single(sent.Sent);

        await using var check = await db.CreateAsync();
        var reopened = await check.Set<EmailAddressIssue>().SingleAsync();
        Assert.Null(reopened.ClearedAtUtc);
        Assert.Equal((DomainKind.Complaint, 1, Now.AddMinutes(10)), (reopened.Kind, reopened.EventCount, reopened.FirstAtUtc));
    }

    [Theory]
    [InlineData(SmtpStatusCode.MailboxUnavailable, DomainKind.Bounce)] // 550: the mailbox doesn't exist.
    [InlineData(SmtpStatusCode.MailboxBusy, DomainKind.SendFailure)] // 450: try again later.
    public async Task Smtp_recipient_refusals_are_recorded_and_still_reported_as_failures(SmtpStatusCode status, DomainKind expected)
    {
        var (sender, _, recorder, db) = Sender(new RefusingSmtpSender(status));

        await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendAsync(Mail("gone@example.com")));

        await using var context = await db.CreateAsync();
        var row = await context.Set<EmailAddressIssue>().SingleAsync();
        Assert.Equal((expected, "smtp"), (row.Kind, row.Provider));
        Assert.StartsWith($"{(int)status} ", row.Detail);
        Assert.Equal(expected == DomainKind.Bounce, await recorder.IsSuppressedAsync("gone@example.com"));
    }

    [Fact]
    public async Task Without_a_tenant_in_MultiTenant_mode_mail_goes_out_unchecked()
    {
        var services = new ServiceCollection()
            .AddSingleton(new EmailDeliverabilityRecorder(new ThrowingFactory()))
            .AddSingleton(new TenancyOptions { Mode = TenancyMode.MultiTenant })
            .BuildServiceProvider();
        var sent = new RecordingEmailSender();
        var sender = new SuppressingEmailSender(sent, "ses", services, TimeProvider.System, NullLogger<SuppressingEmailSender>.Instance);

        await sender.SendAsync(Mail("host-admin@example.com")); // e.g. "Your organization is ready" from the host site.

        Assert.Single(sent.Sent);
    }

    [Fact]
    public async Task A_failing_suppression_lookup_never_stops_mail()
    {
        var services = new ServiceCollection().AddSingleton(new EmailDeliverabilityRecorder(new ThrowingFactory())).BuildServiceProvider();
        var sent = new RecordingEmailSender();
        var sender = new SuppressingEmailSender(sent, "smtp", services, TimeProvider.System, NullLogger<SuppressingEmailSender>.Instance);

        await sender.SendAsync(Mail("someone@example.com"));

        Assert.Single(sent.Sent);
    }

    [Fact]
    public async Task Admins_see_bouncing_addresses_with_the_user_they_belong_to_and_can_clear_them()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await seeded.Host.Get<EmailDeliverabilityRecorder>().RecordAsync([Event(Owner, DomainKind.Bounce), Event("contact@vendor.example", DomainKind.Complaint)]);

        var issues = await seeded.As(Admin).Host.Get<IEmailDeliverabilityService>().ListAsync();

        Assert.Equal(["contact@vendor.example", Owner], issues.Select(i => i.Address)); // Complaints first.
        Assert.Equal("Pat", issues.Single(i => i.Address == Owner).UserDisplayName);
        Assert.Null(issues.Single(i => i.Address == "contact@vendor.example").UserDisplayName);
        Assert.All(issues, i => Assert.True(i.Suppressed));
        Assert.Contains("bouncing", issues.Single(i => i.Address == Owner).Message);

        await seeded.Host.Get<IEmailDeliverabilityService>().ClearAsync("contact@vendor.example");

        Assert.Equal([Owner], (await seeded.Host.Get<IEmailDeliverabilityService>().ListAsync()).Select(i => i.Address));
        var all = await seeded.Host.Get<IEmailDeliverabilityService>().ListAsync(includeCleared: true);
        Assert.Equal(Admin, all.Single(i => i.Address == "contact@vendor.example").ClearedBy);
    }

    [Fact]
    public async Task People_see_and_clear_only_their_own_address()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await seeded.Host.Get<EmailDeliverabilityRecorder>().RecordAsync([Event(Owner, DomainKind.Bounce), Event(Viewer, DomainKind.Bounce)]);
        var service = seeded.As(Owner).Host.Get<IEmailDeliverabilityService>();

        Assert.Equal(Owner, (await service.GetMineAsync())?.Address);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ClearAsync(Viewer));

        await service.ClearAsync(Owner.ToUpperInvariant());

        Assert.Null(await service.GetMineAsync());
        Assert.False(await seeded.Host.Get<EmailDeliverabilityRecorder>().IsSuppressedAsync(Owner));
        Assert.True(await seeded.Host.Get<EmailDeliverabilityRecorder>().IsSuppressedAsync(Viewer));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.ClearAsync(Owner));
    }

    // --- The webhook endpoints in the real web host ---------------------------------------------------------------

    [Fact]
    public async Task The_webhooks_record_signed_events_and_refuse_everything_else()
    {
        var (key, publicKey) = EmailWebhookTests.SendGridKey();
        using var certificate = EmailWebhookTests.SnsCertificate();
        await using var factory = new WorkbenchWebFactory("Development", settings: new Dictionary<string, string>
        {
            ["Email:SendGrid:WebhookVerificationKey"] = publicKey,
            ["Email:Ses:BounceTopicArns"] = EmailWebhookTests.TopicArn,
        }, seededTemplate: true);
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ISnsCertificateSource>(new EmailWebhookTests.FixedCertificates(certificate))));
        using var client = app.CreateClient();

        // SendGrid: signed, unsigned, tampered.
        var body = Encoding.UTF8.GetBytes("""[{"email":"hook-bounce@example.com","event":"bounce","reason":"550 no such user","timestamp":1761220800}]""");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(SendGrid(body, EmailWebhookTests.SignSendGrid(key, "1761220800", body), "1761220800"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(SendGrid(body, null, "1761220800"))).StatusCode);
        var forged = Encoding.UTF8.GetBytes("""[{"email":"victim@example.com","event":"spamreport","timestamp":1761220800}]""");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(SendGrid(forged, EmailWebhookTests.SignSendGrid(key, "1761220800", body), "1761220800"))).StatusCode);

        // SES through SNS: a signed bounce, a topic nobody configured, a bad signature.
        var bounce = EmailWebhookTests.SesBounce.Replace("Gone@Example.com", "ses-bounce@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/webhooks/email/ses", Json(EmailWebhookTests.SnsJson(certificate, "Notification", bounce)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/webhooks/email/ses",
            Json(EmailWebhookTests.SnsJson(certificate, "Notification", bounce.Replace("ses-bounce", "other"), topicArn: "arn:aws:sns:us-east-1:999999999999:attacker")))).StatusCode);
        using var otherCertificate = EmailWebhookTests.SnsCertificate();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/webhooks/email/ses",
            Json(EmailWebhookTests.SnsJson(otherCertificate, "Notification", bounce.Replace("ses-bounce", "forged"))))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/webhooks/email/ses", Json("{}"))).StatusCode);

        await using var db = await app.Services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
        var rows = await db.Set<EmailAddressIssue>().OrderBy(i => i.Address).ToListAsync();
        Assert.Equal([("hook-bounce@example.com", DomainKind.Bounce, "sendgrid"), ("ses-bounce@example.com", DomainKind.Bounce, "ses")],
            rows.Select(r => (r.Address, r.Kind, r.Provider)));
    }

    [Fact]
    public async Task Unconfigured_webhooks_answer_404()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/webhooks/email/sendgrid", Json("[]"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/webhooks/email/ses", Json("{}"))).StatusCode);
    }

    private static HttpRequestMessage SendGrid(byte[] body, string? signature, string timestamp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/email/sendgrid") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        if (signature is not null)
        {
            request.Headers.Add(SendGridEventWebhook.SignatureHeader, signature);
        }

        request.Headers.Add(SendGridEventWebhook.TimestampHeader, timestamp);
        return request;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "text/plain"); // SNS posts JSON as text/plain.

    private sealed class RefusingSmtpSender(SmtpStatusCode status) : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            throw new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, status, MailboxAddress.Parse(message.To), "Recipient refused");
    }

    private sealed class ThrowingFactory : IWorkbenchDbContextFactory
    {
        public Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) => throw new TenantNotResolvedException();
    }
}
