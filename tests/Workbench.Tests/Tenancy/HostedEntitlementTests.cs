using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Licensing;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tenancy;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Tests.Tenancy;

/// <summary>Entitlement transitions, read-only mode, the license server's endpoint and the host admin's tenant actions (docs/admin-guide/hosted-signup.md).</summary>
public sealed class HostedEntitlementTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static TenantEntitlement Row(Action<TenantEntitlement>? change = null)
    {
        var row = new TenantEntitlement { TenantId = Guid.NewGuid(), Plan = "trial", Modules = "capture,finance", Seats = 10, LightSeats = 5, IsTrial = true, TrialEndsUtc = Now.AddDays(14), UpdatedAtUtc = Now };
        change?.Invoke(row);
        return row;
    }

    [Fact]
    public void A_trial_works_until_its_last_moment_and_is_read_only_after()
    {
        var running = TenantEntitlements.Evaluate(Row(), Now, 7);
        Assert.False(running.ReadOnly);
        Assert.True(running.Trial);
        Assert.Contains("finance", running.Modules);
        Assert.Contains("core", running.Modules);

        Assert.False(TenantEntitlements.Evaluate(Row(), Now.AddDays(13.9), 7).ReadOnly);
        var lapsed = TenantEntitlements.Evaluate(Row(), Now.AddDays(14.1), 7);
        Assert.True(lapsed.ReadOnly);
        Assert.Contains("trial ended", lapsed.Message);
    }

    [Fact]
    public void Past_due_keeps_working_with_a_warning_until_the_grace_ends_then_goes_read_only()
    {
        var paid = Row(r => { r.IsTrial = false; r.Plan = "business"; r.PaidThroughUtc = Now.AddDays(30); });
        Assert.False(TenantEntitlements.Evaluate(paid, Now, 7).PastDue);

        // Payment failed today: full access, a warning.
        paid.Status = EntitlementStatus.PastDue;
        paid.PastDueSinceUtc = Now;
        var warned = TenantEntitlements.Evaluate(paid, Now.AddDays(3), 7);
        Assert.False(warned.ReadOnly);
        Assert.True(warned.PastDue);
        Assert.Contains("overdue", warned.Message);
        Assert.True(TenantEntitlements.Evaluate(paid, Now.AddDays(40), 7).ReadOnly); // term ended + grace long past

        // A term that ended with no news from the license server: grace, then read-only.
        var ended = Row(r => { r.IsTrial = false; r.PaidThroughUtc = Now.AddDays(-3); });
        Assert.True(TenantEntitlements.Evaluate(ended, Now, 7).PastDue);
        Assert.True(TenantEntitlements.Evaluate(ended, Now.AddDays(5), 7).ReadOnly);
    }

    [Theory]
    [InlineData(EntitlementStatus.Cancelled)]
    [InlineData(EntitlementStatus.Suspended)]
    public void Cancelled_and_suspended_are_read_only_even_inside_a_trial_or_term(EntitlementStatus status)
    {
        var view = TenantEntitlements.Evaluate(Row(r => r.Status = status), Now, 7);
        Assert.True(view.ReadOnly);
        Assert.Contains("read-only", view.Message);
    }

    [Fact]
    public async Task The_edition_follows_the_entitlement_and_lapse_turns_business_off()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var entitlements = web.Services.GetRequiredService<TenantEntitlements>();
        var id = (await web.Services.GetRequiredService<TenantCatalog>().FindAsync("acme"))!.Info.Id;
        Task<EditionState> State() => web.InTenantAsync("acme", sp => sp.GetRequiredService<EditionService>().GetAsync(default));

        // No entitlement row: the old behaviour (no plan: Community).
        Assert.Equal(Workbench.Domain.Licensing.Edition.Community, (await State()).Edition);

        await entitlements.ApplyAsync(id, new SetEntitlementRequest("business", ["finance", "govcon", "nonsense"], 25, 10, "active", false, PaidThroughUtc: DateTime.UtcNow.AddDays(30)), "host@workbench.local", "host");
        var paid = await State();
        Assert.Equal(Workbench.Domain.Licensing.Edition.Business, paid.Edition);
        Assert.Equal(25, paid.SeatLimit);
        Assert.Equal(10, paid.LightSeatLimit);
        Assert.False(paid.Trial);
        Assert.Contains("govcon", paid.Modules!);
        Assert.DoesNotContain("nonsense", paid.Modules!);
        Assert.DoesNotContain("enterprise", paid.Modules!);

        await entitlements.ApplyAsync(id, new SetEntitlementRequest(Status: "cancelled"), "license-server", "license-server");
        var cancelled = await State();
        Assert.Equal(Workbench.Domain.Licensing.Edition.Community, cancelled.Edition);
        Assert.True(cancelled.ReadOnly);
        Assert.Contains("cancelled", cancelled.Problem);

        await entitlements.ApplyAsync(id, new SetEntitlementRequest(Status: "active"), "license-server", "license-server");
        Assert.False((await State()).ReadOnly);
    }

    [Fact]
    public async Task A_lapsed_organization_can_read_and_export_but_not_change_anything_and_extending_the_trial_restores_it()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var entitlements = web.Services.GetRequiredService<TenantEntitlements>();
        var id = (await web.Services.GetRequiredService<TenantCatalog>().FindAsync("acme"))!.Info.Id;
        var admin = web.Client("acme", "admin@acme.example");
        await entitlements.ApplyAsync(id, new SetEntitlementRequest("trial", ["finance"], 5, 0, "active", true, TrialEndsUtc: DateTime.UtcNow.AddDays(3)), "host@workbench.local", "host");

        // The first administrator signs in once (that accepts the invitation and gives them the Admin role).
        var signedIn = await web.InTenantAsync("acme", sp => sp.GetRequiredService<Workbench.Application.Identity.SignInGate>()
            .EvaluateAsync("admin@acme.example", "Admin", Workbench.Application.Identity.SignInGate.PasswordMethod, true, default));
        Assert.True(signedIn.Allowed);
        var before = await MultiTenantWeb.ReadAsync<SheetSummary>(await admin.PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("Before the lapse", null, false)));

        // Lapse: the trial ended yesterday.
        await entitlements.ApplyAsync(id, new SetEntitlementRequest(TrialEndsUtc: DateTime.UtcNow.AddDays(-1)), "host@workbench.local", "host");

        var blocked = await admin.PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("After the lapse", null, false));
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Contains("read-only", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("api/v1/sheets/" + Guid.NewGuid())).StatusCode); // reading still works
        Assert.Contains(await MultiTenantWeb.ReadAsync<List<SheetSummary>>(await admin.GetAsync("api/v1/sheets")), s => s.Id == before.Id);

        // The way out: an administrator's export. Anyone else is refused. Secrets are left out.
        var export = await admin.GetAsync("api/v1/organization/export");
        Assert.True(export.StatusCode == HttpStatusCode.OK, await export.Content.ReadAsStringAsync());
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
        using (var zip = new ZipArchive(await export.Content.ReadAsStreamAsync()))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
            using (var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open()))
            {
                Assert.DoesNotContain("\"error\": \"", await manifest.ReadToEndAsync()); // no table failed to export
            }

            Assert.Contains(zip.Entries, e => e.FullName == "Sheet.jsonl");
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("PasswordCredential") || e.FullName.Contains("AccountToken") || e.FullName.Contains("ApiKey"));
            using var reader = new StreamReader(zip.GetEntry("Sheet.jsonl")!.Open());
            Assert.Contains("Before the lapse", await reader.ReadToEndAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Client("acme", null).GetAsync("api/v1/organization/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Client(null, MultiTenantWeb.HostAdmin).GetAsync("api/v1/organization/export")).StatusCode);

        // The host admin extends the trial: writes work again, and nothing was deleted in between.
        var extended = await entitlements.ExtendTrialAsync(id, 14, MultiTenantWeb.HostAdmin, "customer asked");
        Assert.True(extended.TrialEndsUtc > DateTime.UtcNow.AddDays(13));
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("After the extension", null, false))).StatusCode);
        Assert.Contains(await MultiTenantWeb.ReadAsync<List<SheetSummary>>(await admin.GetAsync("api/v1/sheets")), s => s.Id == before.Id);

        var audit = await web.Services.GetRequiredService<TenantProvisioner>().ListAuditAsync(200);
        Assert.Contains(audit, e => e.Action == "entitlement.update" && e.Actor == MultiTenantWeb.HostAdmin && e.Detail!.Contains("trial extended 14 days: customer asked"));
    }

    [Fact]
    public async Task The_license_server_endpoint_needs_its_key_is_idempotent_and_ignores_stale_events()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var host = web.Client(null, null);
        var body = new SetEntitlementRequest("business", ["finance", "capture"], 25, 10, "active", false, PaidThroughUtc: DateTime.UtcNow.AddMonths(1), ExternalRef: "sub_123", EventAtUtc: new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PutAsJsonAsync("api/v1/host/entitlements/acme", body)).StatusCode);
        host.DefaultRequestHeaders.Add("X-Host-Key", "wrong-key-wrong-key-wrong-key-wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("api/v1/host/entitlements")).StatusCode);
        host.DefaultRequestHeaders.Remove("X-Host-Key");
        host.DefaultRequestHeaders.Add("X-Host-Key", MultiTenantWeb.EntitlementKey);

        var first = await MultiTenantWeb.ReadAsync<HostTenantEntitlementDto>(await host.PutAsJsonAsync("api/v1/host/entitlements/acme", body));
        var again = await MultiTenantWeb.ReadAsync<HostTenantEntitlementDto>(await host.PutAsJsonAsync("api/v1/host/entitlements/acme", body));
        Assert.Equal(first.Entitlement.Modules, again.Entitlement.Modules);
        Assert.Equal(["capture", "finance"], again.Entitlement.Modules);
        Assert.Equal(25, again.Entitlement.Seats);
        Assert.Equal("sub_123", again.Entitlement.ExternalRef);
        Assert.Equal("license-server", again.Entitlement.Source);

        // Payment failed (newer), then an older "active" event arrives late: the newer fact stands.
        await host.PutAsJsonAsync("api/v1/host/entitlements/acme", new SetEntitlementRequest(Status: "past_due", EventAtUtc: new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc)));
        await host.PutAsJsonAsync("api/v1/host/entitlements/acme", new SetEntitlementRequest(Status: "active", EventAtUtc: new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc)));
        var now = await MultiTenantWeb.ReadAsync<HostTenantEntitlementDto>(await host.GetAsync("api/v1/host/entitlements/acme"));
        Assert.Equal("past_due", now.Entitlement.Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutAsJsonAsync("api/v1/host/entitlements/acme", new SetEntitlementRequest(Status: "paused"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PutAsJsonAsync("api/v1/host/entitlements/nobody", body)).StatusCode);
        Assert.Single(await MultiTenantWeb.ReadAsync<List<HostTenantEntitlementDto>>(await host.GetAsync("api/v1/host/entitlements")));

        // Never reachable from a tenant's address.
        var tenantSide = web.Client("acme", null);
        tenantSide.DefaultRequestHeaders.Add("X-Host-Key", MultiTenantWeb.EntitlementKey);
        Assert.Equal(HttpStatusCode.NotFound, (await tenantSide.GetAsync("api/v1/host/entitlements")).StatusCode);

        var audit = await web.Services.GetRequiredService<TenantProvisioner>().ListAuditAsync(200);
        Assert.Contains(audit, e => e.Actor == "license-server" && e.Action.StartsWith("entitlement."));
    }

    [Fact]
    public async Task Without_a_configured_key_the_license_server_endpoint_does_not_exist()
    {
        await using var web = await MultiTenantWeb.HostedAsync(s => s["Tenancy:Signup:EntitlementApiKey"] = "");
        var host = web.Client(null, null);
        host.DefaultRequestHeaders.Add("X-Host-Key", MultiTenantWeb.EntitlementKey);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("api/v1/host/entitlements")).StatusCode);
    }

    [Fact]
    public async Task Only_host_admins_on_the_host_site_manage_tenants_and_every_change_is_audited()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var id = (await web.Services.GetRequiredService<TenantCatalog>().FindAsync("acme"))!.Info.Id;
        var change = new SetEntitlementRequest(Modules: ["finance", "enterprise"], Seats: 12, LightSeats: 3, Status: "active", Reason: "upgrade");

        // A tenant's own Admin, an ordinary user, an anonymous caller and a host admin on a tenant's address are all refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client(null, "admin@acme.example").PutAsJsonAsync($"api/v1/host/tenants/{id}/entitlement", change)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client(null, "admin@acme.example").PostAsJsonAsync($"api/v1/host/tenants/{id}/extend-trial", new ExtendTrialRequest(7))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Client(null, null).PutAsJsonAsync($"api/v1/host/tenants/{id}/entitlement", change)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Client("acme", MultiTenantWeb.HostAdmin).PutAsJsonAsync($"api/v1/host/tenants/{id}/entitlement", change)).StatusCode);

        var host = web.Client(null, MultiTenantWeb.HostAdmin);
        var updated = await MultiTenantWeb.ReadAsync<HostTenantDto>(await host.PutAsJsonAsync($"api/v1/host/tenants/{id}/entitlement", change));
        Assert.Equal(["enterprise", "finance"], updated.Entitlement!.Modules);
        Assert.Equal(12, updated.Entitlement.Seats);

        var extended = await MultiTenantWeb.ReadAsync<HostTenantDto>(await host.PostAsJsonAsync($"api/v1/host/tenants/{id}/extend-trial", new ExtendTrialRequest(10, "sales call")));
        Assert.True(extended.Entitlement!.Trial);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync($"api/v1/host/tenants/{id}/extend-trial", new ExtendTrialRequest(0))).StatusCode);

        // The list shows plan, status, trial end and seats used against seats bought; suspend and reactivate still work and are audited.
        var tenants = await MultiTenantWeb.ReadAsync<List<HostTenantDto>>(await host.GetAsync("api/v1/host/tenants"));
        var acme = tenants.Single(t => t.Slug == "acme");
        Assert.Equal(12, acme.Entitlement!.Seats);
        Assert.NotNull(acme.Entitlement.TrialEndsUtc);
        Assert.NotNull(acme.Entitlement.SeatsInUse);
        Assert.Null(tenants.Single(t => t.Slug == "beta").Entitlement);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsJsonAsync($"api/v1/host/tenants/{id}/suspend", new HostTenantStatusRequest("non-payment"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client("acme", "admin@acme.example").GetAsync("api/v1/sheets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsJsonAsync($"api/v1/host/tenants/{id}/reactivate", new HostTenantStatusRequest(null))).StatusCode);

        var audit = await MultiTenantWeb.ReadAsync<List<HostAuditEventDto>>(await host.GetAsync("api/v1/host/audit"));
        Assert.Contains(audit, e => e.Action.StartsWith("entitlement.") && e.Actor == MultiTenantWeb.HostAdmin && e.Detail!.Contains("upgrade"));
        Assert.Contains(audit, e => e.Action == "tenant.suspend" && e.Detail!.Contains("non-payment"));
    }
}
