using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tenancy;
using Workbench.Domain;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Infrastructure.Tenancy;
using Workbench.Web.Hubs;
using Workbench.Web.Notifications;

namespace Workbench.Tests.Tenancy;

/// <summary>
/// MultiTenant mode end to end (ISO 27001 A.8.3): a user of tenant A never reads tenant B's data through the API, the
/// SignalR hubs, background jobs or a copied cookie; tenants resolve from the subdomain; host admins provision tenants.
/// </summary>
public sealed class TenancyIsolationTests
{
    [Fact]
    public async Task Data_created_in_one_tenant_never_appears_in_another()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        var acme = web.Client("acme");
        var beta = web.Client("beta");

        var created = await MultiTenantWeb.ReadAsync<SheetSummary>(await acme.PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Acme secret plan", null, false)));

        var acmeSheets = await MultiTenantWeb.ReadAsync<List<SheetSummary>>(await acme.GetAsync("api/v1/sheets"));
        var betaSheets = await MultiTenantWeb.ReadAsync<List<SheetSummary>>(await beta.GetAsync("api/v1/sheets"));
        Assert.Contains(acmeSheets, s => s.Id == created.Id);
        Assert.DoesNotContain(betaSheets, s => s.Id == created.Id || s.Name == "Acme secret plan");
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"api/v1/sheets/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"api/v1/sheets/{created.Id}/items")).StatusCode);

        // Each tenant really has its own database.
        var counts = await Task.WhenAll(
            web.InTenantAsync("acme", sp => CountAsync(sp, "Acme secret plan")),
            web.InTenantAsync("beta", sp => CountAsync(sp, "Acme secret plan")));
        Assert.Equal([1, 0], counts);
    }

    [Fact]
    public async Task Tenants_resolve_from_the_subdomain_and_unknown_addresses_get_nothing()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        await web.Client("acme").PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Only at acme", null, false));

        Assert.Equal(HttpStatusCode.NotFound, (await web.Client("zeta").GetAsync("api/v1/sheets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Client(null).GetAsync("api/v1/sheets")).StatusCode);
        var nested = web.Factory.CreateClient(new() { BaseAddress = new Uri("http://x.acme.localhost/") });
        Assert.Equal(HttpStatusCode.NotFound, (await nested.GetAsync("api/v1/sheets")).StatusCode);

        // Development only: the X-Tenant header picks the tenant.
        var header = web.Client(null);
        header.DefaultRequestHeaders.Add("X-Tenant", "acme");
        Assert.Contains(await MultiTenantWeb.ReadAsync<List<SheetSummary>>(await header.GetAsync("api/v1/sheets")), s => s.Name == "Only at acme");

        Assert.Equal("acme", Workbench.Web.Tenancy.TenantResolutionMiddleware.SlugFromHost("ACME.workbench.example", "workbench.example"));
        Assert.Null(Workbench.Web.Tenancy.TenantResolutionMiddleware.SlugFromHost("workbench.example.", "workbench.example"));
        Assert.Equal("", Workbench.Web.Tenancy.TenantResolutionMiddleware.SlugFromHost("evil.example", "workbench.example"));
        Assert.Equal("", Workbench.Web.Tenancy.TenantResolutionMiddleware.SlugFromHost("a.b.workbench.example", "workbench.example"));
        Assert.Equal("", Workbench.Web.Tenancy.TenantResolutionMiddleware.SlugFromHost("acmeworkbench.example", "workbench.example"));
    }

    [Fact]
    public async Task Outside_development_the_tenant_header_is_ignored_and_tenants_are_provisioned_by_the_host()
    {
        await using var web = await MultiTenantWeb.StagingAsync();
        var acme = web.Client("acme", "admin@acme.example");
        await MultiTenantWeb.ReadAsync<SheetSummary>(await acme.PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Acme only", null, false)));

        var spoof = web.Client("beta", "admin@acme.example");
        spoof.DefaultRequestHeaders.Add("X-Tenant", "acme");
        Assert.DoesNotContain(await MultiTenantWeb.ReadAsync<List<SheetSummary>>(await spoof.GetAsync("api/v1/sheets")), s => s.Name == "Acme only");

        var noHost = web.Client(null, "admin@acme.example");
        noHost.DefaultRequestHeaders.Add("X-Tenant", "acme");
        Assert.Equal(HttpStatusCode.NotFound, (await noHost.GetAsync("api/v1/sheets")).StatusCode);
    }

    [Fact]
    public async Task Hubs_only_relay_a_tenants_changes_and_notifications_to_that_tenant()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        var acmeSheet = await MultiTenantWeb.ReadAsync<SheetSummary>(await web.Client("acme").PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Hub test", null, false)));

        // The same sheet id in beta's database, so only the tenant separates the two SignalR groups.
        await web.InTenantAsync("beta", async sp =>
        {
            await using var db = await sp.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            var workspace = await db.Workspaces.FirstAsync();
            db.Sheets.Add(new Sheet { Id = acmeSheet.Id, WorkspaceId = workspace.Id, Name = "Beta twin", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        await using var acmeHub = web.Hub("acme", SheetsHub.Path);
        await using var betaHub = web.Hub("beta", SheetsHub.Path);
        var acmeChanges = new List<SheetChange>();
        var betaChanges = new List<SheetChange>();
        acmeHub.On<SheetChange>(SheetsHub.ChangedMethod, c => { lock (acmeChanges) { acmeChanges.Add(c); } });
        betaHub.On<SheetChange>(SheetsHub.ChangedMethod, c => { lock (betaChanges) { betaChanges.Add(c); } });
        await acmeHub.StartAsync();
        await betaHub.StartAsync();
        await acmeHub.InvokeAsync("JoinSheet", acmeSheet.Id);
        await betaHub.InvokeAsync("JoinSheet", acmeSheet.Id);

        // A sheet of another tenant can't be joined at all.
        var acmeOnly = await MultiTenantWeb.ReadAsync<SheetSummary>(await web.Client("acme").PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Acme only", null, false)));
        await Assert.ThrowsAsync<HubException>(() => betaHub.InvokeAsync("JoinSheet", acmeOnly.Id));

        await web.Client("acme").PostAsJsonAsync($"api/v1/sheets/{acmeSheet.Id}/items", new CreateItemRequest("Acme task"));
        await WaitUntilAsync(() => { lock (acmeChanges) { return acmeChanges.Count > 0; } });
        await Task.Delay(500);
        lock (betaChanges)
        {
            Assert.Empty(betaChanges);
        }

        // Notifications: the same email exists in both tenants; only acme's dev@ hears about acme.
        await using var acmeBell = web.Hub("acme", NotificationsHub.Path);
        await using var betaBell = web.Hub("beta", NotificationsHub.Path);
        var acmeNotes = new List<NotificationDto>();
        var betaNotes = new List<NotificationDto>();
        acmeBell.On<NotificationDto>(NotificationsHub.NotificationMethod, n => { lock (acmeNotes) { acmeNotes.Add(n); } });
        betaBell.On<NotificationDto>(NotificationsHub.NotificationMethod, n => { lock (betaNotes) { betaNotes.Add(n); } });
        await acmeBell.StartAsync();
        await betaBell.StartAsync();

        var betaInProcess = new List<NotificationDto>();
        await using var subscription = await web.InTenantAsync("beta", sp => Task.FromResult(sp.GetRequiredService<NotificationHub>()
            .Subscribe(MultiTenantWeb.DevAdmin, n => { lock (betaInProcess) { betaInProcess.Add(n); } return Task.CompletedTask; })));
        await web.InTenantAsync("acme", sp => sp.GetRequiredService<INotificationPublisher>().PublishAsync(MultiTenantWeb.DevAdmin, "test", "Acme only news"));
        await WaitUntilAsync(() => { lock (acmeNotes) { return acmeNotes.Count > 0; } });
        await Task.Delay(500);
        Assert.Equal("Acme only news", acmeNotes.Single().Title);
        lock (betaNotes)
        {
            Assert.Empty(betaNotes);
        }

        Assert.Empty(betaInProcess);
        Assert.Equal(0, await web.InTenantAsync("beta", async sp =>
        {
            await using var db = await sp.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return await db.Set<Workbench.Domain.Notifications.Notification>().CountAsync(n => n.Title == "Acme only news");
        }));
    }

    [Fact]
    public async Task Background_jobs_run_once_per_active_tenant_inside_that_tenant_and_fail_closed_without_one()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        await web.Client("acme").PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Job marker", null, false));

        var seen = new List<(string Tenant, int Markers)>();
        var runner = web.Services.GetRequiredService<ITenantJobRunner>();
        await runner.ForEachTenantAsync(async ct =>
        {
            await using var scope = web.Services.CreateAsyncScope();
            seen.Add((TenantScope.Current!.Slug, await CountAsync(scope.ServiceProvider, "Job marker")));
        }, CancellationToken.None);
        Assert.Equal([("acme", 1), ("beta", 0)], seen);

        // Without a tenant there is no database to open: nothing falls back to a shared one.
        using (TenantScope.Enter(null))
        {
            await Assert.ThrowsAsync<TenantNotResolvedException>(() => web.Services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync());
        }

        // A suspended tenant is skipped.
        var beta = (await web.Services.GetRequiredService<TenantCatalog>().FindAsync("beta"))!.Info;
        await web.Services.GetRequiredService<TenantProvisioner>().SuspendAsync(beta.Id, MultiTenantWeb.HostAdmin, "test");
        seen.Clear();
        await runner.ForEachTenantAsync(_ => { seen.Add((TenantScope.Current!.Slug, 0)); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(["acme"], seen.Select(s => s.Tenant));
    }

    [Fact]
    public async Task A_cookie_issued_for_one_tenant_is_refused_by_another()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        var cookie = await DevSignInAsync(web, "acme");

        var atAcme = web.Client("acme", email: null);
        atAcme.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await atAcme.GetAsync("api/v1/sheets")).StatusCode);

        // Copied to beta (as a stolen cookie would be), it doesn't decrypt there: beta's Data Protection purpose differs.
        var atBeta = web.Client("beta", email: null);
        atBeta.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await atBeta.GetAsync("api/v1/sheets")).StatusCode);
        var atHost = web.Client(null, email: null);
        atHost.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await atHost.GetAsync("api/v1/host/tenants")).StatusCode);
    }

    [Fact]
    public async Task Host_admins_create_suspend_and_reactivate_tenants_and_nobody_else_can()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        var host = web.Client(null, MultiTenantWeb.HostAdmin);

        var tenants = await MultiTenantWeb.ReadAsync<List<HostTenantDto>>(await host.GetAsync("api/v1/host/tenants"));
        Assert.Equal(["acme", "beta"], tenants.Select(t => t.Slug));
        Assert.All(tenants, t => Assert.True(t.DatabaseBytes > 0));
        Assert.True((await MultiTenantWeb.ReadAsync<HostAccessDto>(await host.GetAsync("api/v1/host/access"))).IsHostAdmin);

        // A tenant's Admin isn't a host admin, and host administration doesn't exist at a tenant's address.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client(null, MultiTenantWeb.DevAdmin).GetAsync("api/v1/host/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Client("acme", MultiTenantWeb.HostAdmin).GetAsync("api/v1/host/tenants")).StatusCode);

        var bad = await host.PostAsJsonAsync("api/v1/host/tenants", new CreateHostTenantRequest("Www", "Nope", "x@example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync("api/v1/host/tenants", new CreateHostTenantRequest("acme", "Again", "x@example.com"))).StatusCode);

        var created = await host.PostAsJsonAsync("api/v1/host/tenants", new CreateHostTenantRequest("gamma", "Gamma Corp", "Owner@Gamma.Example", "Gail", "Standard"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var gamma = (await created.Content.ReadFromJsonAsync<HostTenantDto>())!;
        Assert.Equal(("gamma", "Active"), (gamma.Slug, gamma.Status));

        // Its own database: migrated, the default chart of accounts and fiscal calendar, an Admin invite, no sample data.
        var (accounts, years, invite, sheets) = await web.InTenantAsync("gamma", async sp =>
        {
            await using var db = await sp.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return (await db.Set<Account>().CountAsync(), await db.Set<FiscalYear>().CountAsync(),
                await db.Set<UserInvite>().SingleAsync(), await db.Sheets.CountAsync());
        });
        Assert.True(accounts > 10);
        Assert.Equal(2, years);
        Assert.Equal(("owner@gamma.example", true, MultiTenantWeb.HostAdmin), (invite.Email, invite.IsAdmin, invite.InvitedBy));
        Assert.Equal(0, sheets);
        Assert.Equal(HttpStatusCode.OK, (await web.Client("gamma", "owner@gamma.example").GetAsync("api/v1/sheets")).StatusCode);

        var suspended = await MultiTenantWeb.ReadAsync<HostTenantDto>(await host.PostAsJsonAsync($"api/v1/host/tenants/{gamma.Id}/suspend", new HostTenantStatusRequest("Unpaid")));
        Assert.Equal("Suspended", suspended.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client("gamma", "owner@gamma.example").GetAsync("api/v1/sheets")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync($"api/v1/host/tenants/{gamma.Id}/suspend", new HostTenantStatusRequest(null))).StatusCode);

        await MultiTenantWeb.ReadAsync<HostTenantDto>(await host.PostAsJsonAsync($"api/v1/host/tenants/{gamma.Id}/reactivate", new HostTenantStatusRequest("Paid")));
        Assert.Equal(HttpStatusCode.OK, (await web.Client("gamma", "owner@gamma.example").GetAsync("api/v1/sheets")).StatusCode);

        var audit = await MultiTenantWeb.ReadAsync<List<HostAuditEventDto>>(await host.GetAsync("api/v1/host/audit"));
        Assert.Contains(audit, e => e.Action == "tenant.create.completed" && e.TenantId == gamma.Id && e.Actor == MultiTenantWeb.HostAdmin);
        Assert.Contains(audit, e => e.Action == "tenant.suspend" && e.Detail!.Contains("Unpaid"));
        Assert.Contains(audit, e => e.Action == "tenant.reactivate");
    }

    [Fact]
    public async Task The_host_page_lists_tenants_for_host_admins_only()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var html = await (await web.Client(null, MultiTenantWeb.HostAdmin).GetAsync("host", timeout.Token)).Content.ReadAsStringAsync(timeout.Token);
        Assert.Contains("acme", html);
        Assert.Contains("beta", html);
        Assert.Contains("Host audit log", html);

        var tenantAdmin = await (await web.Client(null, MultiTenantWeb.DevAdmin).GetAsync("host", timeout.Token)).Content.ReadAsStringAsync(timeout.Token);
        Assert.Contains("Only host administrators can manage tenants", tenantAdmin);
    }

    private static async Task<int> CountAsync(IServiceProvider services, string sheetName)
    {
        await using var db = await services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Sheets.CountAsync(s => s.Name == sheetName);
    }

    /// <summary>Signs in on the dev sign-in page of <paramref name="tenant"/> and returns the auth cookie as a Cookie header value.</summary>
    private static async Task<string> DevSignInAsync(MultiTenantWeb web, string tenant)
    {
        var client = web.Client(tenant, email: null);
        var page = await client.GetAsync("account/dev-signin");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var antiforgery = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var post = new HttpRequestMessage(HttpMethod.Post, "account/dev-signin")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["email"] = MultiTenantWeb.DevAdmin,
                ["returnUrl"] = "/",
            }),
        };
        post.Headers.Add("Cookie", antiforgery);
        var signedIn = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        return signedIn.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith(".Workbench.Auth=", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for the hub message.");
    }
}
