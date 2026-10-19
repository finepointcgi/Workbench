using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Notifications;
using Workbench.Application.Support;
using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Support;
using Workbench.Domain.Notifications;
using Workbench.Tests.Automation;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Support;

public sealed class SupportServiceTests
{
    private const string SupportAddress = "support@vendor.example";

    private static async Task<(SeededHost Host, RecordingEmailSender Emails)> CreateAsync(string? supportEmail = SupportAddress)
    {
        var emails = new RecordingEmailSender();
        var host = await SeededHost.CreateAsync(services =>
        {
            services.Configure<SupportOptions>(o => o.Email = supportEmail);
            services.AddSingleton<IEmailSender>(emails);
        });
        return (host, emails);
    }

    private static SupportScreenshot Png(int bytes = 64) => new("screen.png", new MemoryStream(new byte[bytes]));

    [Fact]
    public async Task Submitting_stores_the_request_emails_support_and_tells_admins()
    {
        var (seeded, emails) = await CreateAsync();
        await using var _ = seeded;
        var support = seeded.As(Owner).Host.Get<ISupportService>();

        var sent = await support.SubmitAsync(new SubmitSupportRequest(
            SupportKind.Problem, "  Save button does nothing  ", "Clicked Save on Tuesday's row.",
            "https://acme.example/time/2026-10-12?token=secret&q=salary#row", "1.0.0+abc1234"), Png());

        Assert.Equal(1, sent.Number);
        Assert.Equal("SR-000001", sent.Reference);
        Assert.Equal(SupportStatus.New, sent.Status);
        Assert.Equal("Save button does nothing", sent.Summary);
        Assert.Equal("/time/2026-10-12", sent.PagePath); // no host, query string or fragment
        Assert.Equal("1.0.0+abc1234", sent.AppVersion);
        Assert.Equal(Owner, sent.ReporterEmail);
        Assert.Equal("Pat", sent.ReporterName);
        Assert.Equal("screen.png", sent.ScreenshotFileName);
        Assert.Single(seeded.Host.Blobs.Blobs);

        var mail = Assert.Single(emails.Sent, m => m.To == SupportAddress);
        Assert.Contains("SR-000001", mail.Subject);
        Assert.Contains("/time/2026-10-12", mail.TextBody);
        Assert.DoesNotContain("secret", mail.TextBody + mail.HtmlBody + mail.Subject);
        Assert.Equal("screen.png", Assert.Single(mail.Attachments!).FileName);

        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var notified = await db.Set<Notification>().Where(n => n.Kind == SupportService.NotificationKind).Select(n => n.RecipientEmail).ToListAsync();
        Assert.Equal([Admin], notified);

        var second = await support.SubmitAsync(new SubmitSupportRequest(SupportKind.Feedback, "Dark mode for reports"));
        Assert.Equal(2, second.Number);
        Assert.False(second.HasScreenshot);
        Assert.Null(second.PagePath);
    }

    [Fact]
    public async Task Html_in_a_report_is_escaped_in_the_support_email()
    {
        var (seeded, emails) = await CreateAsync();
        await using var _ = seeded;
        await seeded.As(Editor).Host.Get<ISupportService>().SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "<script>alert(1)</script>", "<img src=x onerror=alert(2)>"));

        var mail = Assert.Single(emails.Sent, m => m.To == SupportAddress);
        Assert.DoesNotContain("<script", mail.HtmlBody);
        Assert.DoesNotContain("<img", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;", mail.HtmlBody);
    }

    [Fact]
    public async Task Without_a_support_address_requests_are_only_stored()
    {
        var (seeded, emails) = await CreateAsync(supportEmail: null);
        await using var _ = seeded;
        var sent = await seeded.As(Editor).Host.Get<ISupportService>().SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "Broken"));

        Assert.Equal(1, sent.Number);
        Assert.DoesNotContain(emails.Sent, m => m.Subject.Contains("SR-000001", StringComparison.Ordinal) && m.To != Admin);
    }

    [Fact]
    public async Task Invalid_reports_are_refused()
    {
        var (seeded, _) = await CreateAsync();
        await using var __ = seeded;
        var support = seeded.As(Editor).Host.Get<ISupportService>();

        await Assert.ThrowsAsync<ArgumentException>(() => support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "   ")));
        await Assert.ThrowsAsync<ArgumentException>(() => support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, new string('x', 201))));
        await Assert.ThrowsAsync<ArgumentException>(() => support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "x", new string('y', 4_001))));
        await Assert.ThrowsAsync<ArgumentException>(() => support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "x"), new SupportScreenshot("notes.exe", new MemoryStream([1]))));
        await Assert.ThrowsAsync<ArgumentException>(() => support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "x"), Png((int)SupportRules.MaxScreenshotBytes + 1)));
        Assert.Empty(seeded.Host.Blobs.Blobs);
        Assert.Empty(await seeded.As(Admin).Host.Get<ISupportService>().ListAllAsync());

        seeded.As("");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "x")));
    }

    [Fact]
    public async Task People_see_their_own_requests_and_administrators_see_all()
    {
        var (seeded, _) = await CreateAsync();
        await using var __ = seeded;
        var support = seeded.Host.Get<ISupportService>();
        seeded.As(Editor);
        var editors = await support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "Editor's problem"), Png());
        seeded.As(Viewer);
        var viewers = await support.SubmitAsync(new SubmitSupportRequest(SupportKind.Feedback, "Viewer's idea"));

        Assert.Equal(["Viewer's idea"], (await support.ListMineAsync()).Select(r => r.Summary));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => support.ListAllAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => support.GetAsync(editors.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => support.OpenScreenshotAsync(editors.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => support.UpdateAsync(viewers.Id, new UpdateSupportRequest(SupportStatus.Closed)));
        Assert.Equal(viewers.Id, (await support.GetAsync(viewers.Id)).Id);

        seeded.As(Admin);
        var all = await support.ListAllAsync();
        Assert.Equal(["Viewer's idea", "Editor's problem"], all.Select(r => r.Summary));
        Assert.Equal(2, (await support.ListAllAsync(SupportStatus.New)).Count);
        Assert.Empty(await support.ListAllAsync(SupportStatus.Closed));
        Assert.Equal(editors.Id, (await support.GetAsync(editors.Id)).Id);
        await using (var shot = await support.OpenScreenshotAsync(editors.Id))
        {
            Assert.Equal("image/png", shot.ContentType);
        }

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => support.OpenScreenshotAsync(viewers.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => support.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Administrators_update_the_status_and_the_reporter_is_told()
    {
        var (seeded, _) = await CreateAsync();
        await using var __ = seeded;
        var support = seeded.Host.Get<ISupportService>();
        seeded.As(Editor);
        var request = await support.SubmitAsync(new SubmitSupportRequest(SupportKind.Problem, "Report won't export"));

        seeded.As(Admin);
        var updated = await support.UpdateAsync(request.Id, new UpdateSupportRequest(SupportStatus.Resolved, "Fixed in 2026.10.4. Try again."));
        Assert.Equal(SupportStatus.Resolved, updated.Status);
        Assert.Equal("Fixed in 2026.10.4. Try again.", updated.Resolution);
        Assert.Equal(Admin, updated.HandledBy);
        await Assert.ThrowsAsync<ArgumentException>(() => support.UpdateAsync(request.Id, new UpdateSupportRequest(SupportStatus.Closed, new string('z', 2_001))));

        seeded.As(Editor);
        var mine = Assert.Single(await support.ListMineAsync());
        Assert.Equal(SupportStatus.Resolved, mine.Status);
        Assert.Equal("Fixed in 2026.10.4. Try again.", mine.Resolution);

        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var told = await db.Set<Notification>().Where(n => n.RecipientEmail == Editor && n.Kind == SupportService.NotificationKind).ToListAsync();
        Assert.Contains(told, n => n.Title.Contains("SR-000001", StringComparison.Ordinal) && n.Title.Contains("resolved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_host_view_is_only_for_host_administrators_in_multitenant_mode()
    {
        var (seeded, _) = await CreateAsync();
        await using var __ = seeded;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Admin).Host.Get<IHostSupportService>().ListAsync());
    }

    [Fact]
    public async Task The_host_view_lists_requests_from_every_tenant()
    {
        var tenants = new[] { new TenantInfo(Guid.NewGuid(), "acme", "Acme"), new TenantInfo(Guid.NewGuid(), "beta", "Beta") };
        var databases = tenants.ToDictionary(t => t.Slug, _ => TestDb.CreateFactory());
        var user = new Workbench.Tests.Collaboration.TestUser { Email = "host@workbench.local" };
        var tenancy = new TenancyOptions { Mode = TenancyMode.MultiTenant, HostAdmins = ["host@workbench.local"] };
        var host = new HostSupportService(tenancy, new FakeTenants(tenants), new PerTenantFactory(databases), user);

        foreach (var (slug, n) in new[] { ("acme", 1), ("beta", 2) })
        {
            await using var db = await databases[slug].CreateAsync();
            for (var i = 1; i <= n; i++)
            {
                db.Set<Workbench.Domain.Support.SupportRequest>().Add(new Workbench.Domain.Support.SupportRequest
                {
                    Number = i, Summary = $"{slug} {i}", ReporterEmail = "a@example.com", CreatedAtUtc = DateTime.UtcNow.AddMinutes(i), UpdatedAtUtc = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        var all = await host.ListAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal(["acme", "beta", "beta"], all.Select(r => r.TenantSlug).Order());
        Assert.Contains(all, r => r.TenantName == "Beta" && r.Request.Summary == "beta 2");

        user.Email = "someone@acme.example";
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.ListAsync());
    }

    private sealed class FakeTenants(TenantInfo[] tenants) : ITenantJobRunner
    {
        public async Task ForEachTenantAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            foreach (var tenant in tenants)
            {
                using (TenantScope.Enter(tenant))
                {
                    await work(cancellationToken);
                }
            }
        }
    }

    private sealed class PerTenantFactory(Dictionary<string, IWorkbenchDbContextFactory> databases) : IWorkbenchDbContextFactory
    {
        public Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            databases[TenantScope.Current?.Slug ?? throw new TenantNotResolvedException()].CreateAsync(cancellationToken);
    }
}
