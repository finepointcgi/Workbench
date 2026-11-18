using AngleSharp.Dom;
using Bunit;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application;
using Workbench.Application.Licensing;
using Workbench.Contracts;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Security;
using Workbench.Domain.Licensing;
using Workbench.Tests.MyWork;
using Workbench.Ui.Navigation;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Licensing;

/// <summary>Modules, WB2 keys, light seats, trials and the pages a module hides.</summary>
public sealed class ModuleEntitlementTests
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly DateTimeOffset Today = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Sign(string payloadJson, string prefix = "WB2")
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        return $"{prefix}.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    private static string Issue(int seats = 25, int lightSeats = 0, string expires = "2027-11-01", bool trial = false, params string[] modules) =>
        Sign($$"""
            {"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme Federal","issuedAt":"2026-10-01","expires":"{{expires}}","seats":{{seats}},"lightSeats":{{lightSeats}},"modules":[{{string.Join(",", modules.Select(m => $"\"{m}\""))}}],"trial":{{(trial ? "true" : "false")}}}
            """);

    private static Task<Identity.SeededHost> HostAsync(IReadOnlyList<ModuleInfo>? modules = null, DateTimeOffset? now = null) => CreateAsync(s =>
    {
        s.Replace(ServiceDescriptor.Singleton(new LicenseOptions { PublicKeyPem = PublicKey, Modules = modules ?? ModuleCatalog.Default }));
        s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new MyWorkServiceTests.FixedClock(now ?? Today)));
    });

    private static IReadOnlyList<ModuleInfo> BillingEnforced() =>
        ModuleCatalog.Default.Select(m => m.Id == ModuleIds.Billing ? m with { Enforced = true } : m).ToList();

    [Fact]
    public void A_hand_built_WB2_sample_parses_to_exactly_the_fields_in_the_spec()
    {
        // Written out by hand, the way the license server will issue it: no serializer, fields in spec order.
        var key = Sign("""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme Federal","issuedAt":"2026-10-01","expires":"2027-10-01","seats":25,"lightSeats":10,"modules":["billing","capture","made-up-later"],"trial":true}""");

        Assert.StartsWith("WB2.", key);
        Assert.True(LicenseKeys.TryVerify(key, PublicKey, out var claims, out var problem), problem);
        Assert.Equal(("Acme Federal", Edition.Business, 25, 10, new DateOnly(2027, 10, 1), true, 2), (claims!.Licensee, claims.Edition, claims.Seats, claims.LightSeats, claims.ExpiresOn, claims.Trial, claims.Version));
        Assert.Equal(Guid.Parse("0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b"), claims.LicenseId);
        Assert.Equal(new DateOnly(2026, 10, 1), claims.IssuedOn);
        Assert.Equal(["billing", "capture", "core"], claims.Modules!.Order().ToArray()); // core is implied; an id this build doesn't know is ignored.
    }

    [Fact]
    public void A_WB2_key_without_the_optional_fields_defaults_them_and_a_forged_one_is_refused()
    {
        Assert.True(LicenseKeys.TryVerify(Sign("""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"X","issuedAt":"2026-10-01","expires":"2027-10-01","seats":3}"""), PublicKey, out var claims, out _));
        Assert.Equal((0, false), (claims!.LightSeats, claims.Trial));
        Assert.Equal(["core"], claims.Modules!.ToArray());

        Assert.False(LicenseKeys.TryVerify(Sign("""{"v":3,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"X","issuedAt":"2026-10-01","expires":"2027-10-01","seats":3}"""), PublicKey, out _, out var version));
        Assert.Contains("damaged", version);
        var parts = Issue(modules: "billing").Split('.');
        Assert.False(LicenseKeys.TryVerify($"WB2.{B64(Encoding.UTF8.GetBytes("""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Me","issuedAt":"2026-10-01","expires":"2099-01-01","seats":9999,"modules":["enterprise"]}"""))}.{parts[2]}", PublicKey, out _, out _));
    }

    [Fact]
    public void The_modules_table_is_complete_and_only_modules_gated_today_are_enforced()
    {
        Assert.Equal(["core", "billing", "accounting", "capture", "workforce", "projectcontrols", "materials", "govcon", "enterprise", "ai"], ModuleCatalog.Default.Select(m => m.Id).ToArray());
        Assert.Equal(["enterprise", "govcon"], ModuleCatalog.Default.Where(m => m.Enforced).Select(m => m.Id).Order().ToArray());
        Assert.All(ModuleCatalog.Default, m => Assert.False(string.IsNullOrWhiteSpace(m.Description)));
        Assert.All(FeatureCatalog.All, f => Assert.True(ModuleCatalog.IsKnown(f.Module)));
        (string, string)[] expected = [("MultiCompany", "enterprise"), ("SingleSignOn", "enterprise"), ("Integrations", "enterprise"), ("CustomerLinks", "enterprise"), ("SupplierPortal", "accounting"), ("ApprovalWorkflows", "enterprise"), ("IncurredCost", "govcon")];
        Assert.Equal(expected.Order(), FeatureCatalog.All.Select(f => (f.Feature.ToString(), f.Module)).Order());
    }

    [Fact]
    public async Task A_WB1_key_means_every_module_exactly_as_Business_always_did()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();
        var payload = Encoding.UTF8.GetBytes("""{"v":1,"licensee":"Old Customer","edition":"business","seats":25,"expires":"2027-11-01"}""");
        var wb1 = $"WB1.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";

        var status = await licenses.ActivateAsync(new ActivateLicenseRequest(wb1));

        Assert.Equal("Business", status.Edition);
        Assert.All(status.Features, f => Assert.True(f.Included));
        Assert.All(status.Modules!, m => Assert.True(m.Licensed));
        Assert.Equal((0, false), (status.LightSeatLimit, status.Trial));
        Assert.NotNull(await seeded.Host.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("B", "Second Co")));
    }

    [Fact]
    public async Task A_WB2_key_unlocks_only_the_features_of_the_modules_it_lists()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();

        var status = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue(modules: ModuleIds.Enterprise)));
        var gate = seeded.Host.Get<FeatureGate>();

        Assert.Equal("Business", status.Edition);
        Assert.Equal(new[] { "Integrations", "MultiCompany", "SingleSignOn", "ApprovalWorkflows", "CustomerLinks" }.Order(), status.Features.Where(f => f.Included).Select(f => f.Key).Order());
        Assert.NotNull(await seeded.Host.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("B", "Second Co")));
        Assert.True(await gate.HasAsync(Feature.MultiCompany, default));
        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => gate.RequireAsync(Feature.IncurredCost, default));
        Assert.Contains("Government contracting module", refused.Message);
        Assert.False(await gate.HasAsync(Feature.SupplierPortal, default)); // A module that isn't enforced still gates its own feature, as it always has.
    }

    [Fact]
    public async Task A_module_that_is_not_enforced_is_open_to_everyone_even_on_Community()
    {
        await using var seeded = await HostAsync();
        var status = await seeded.As(Admin).Host.Get<ILicenseService>().GetStatusAsync();

        Assert.Equal("Community", status.Edition);
        Assert.All(new[] { "core", "billing", "accounting", "capture", "workforce", "projectcontrols", "materials", "ai" }, id => Assert.True(status.HasModule(id)));
        Assert.All(new[] { "govcon", "enterprise" }, id => Assert.False(status.HasModule(id)));
        Assert.All(status.Modules!.Where(m => !m.Enforced), m => Assert.False(m.Licensed && m.Id != "core"));
        await seeded.Host.Get<FeatureGate>().RequireModuleAsync(ModuleIds.Billing, default); // Doesn't throw.
    }

    [Fact]
    public async Task Flipping_a_modules_flag_is_all_it_takes_to_enforce_it()
    {
        await using var seeded = await HostAsync(BillingEnforced());
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();
        var gate = seeded.Host.Get<FeatureGate>();

        var community = await licenses.GetStatusAsync();
        Assert.False(community.HasModule("billing"));
        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => gate.RequireModuleAsync(ModuleIds.Billing, default));
        Assert.Contains("Billing module", refused.Message);

        var capture = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue(modules: ModuleIds.Capture)));
        Assert.False(capture.HasModule("billing"));
        Assert.True(capture.HasModule("capture"));

        var withFinance = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue(modules: ModuleIds.Billing)));
        Assert.True(withFinance.HasModule("billing"));
        await gate.RequireModuleAsync(ModuleIds.Billing, default);

        // Its pages follow the same answer.
        var hidden = NavCatalog.MenuEntries(new NavAccess(null, UnavailableModules: new HashSet<string> { "billing" }));
        Assert.DoesNotContain(hidden, e => e.ModuleId == "billing");
    }

    [Fact]
    public void Pages_of_an_unavailable_module_leave_the_menu_and_the_palette_but_other_pages_stay()
    {
        var everything = new NavAccess(new CurrentUserInfo("a@example.com", "A", OrgRoles.Admin, null));
        var without = everything with { UnavailableModules = new HashSet<string> { "billing", "accounting", "capture", "workforce", "projectcontrols", "materials", "govcon", "enterprise" } };

        var before = NavCatalog.PaletteEntries(everything).ToList();
        var after = NavCatalog.PaletteEntries(without).ToList();

        Assert.All(new[] { "billing", "accounting", "workforce", "materials", "enterprise" }, id => Assert.Contains(before, e => e.ModuleId == id)); // Project controls has no menu page of its own: it lives on the project; the pages of government contracting and capture come from their modules.
        Assert.DoesNotContain(after, e => e.ModuleId != "core");
        Assert.Contains(after, e => e.Href == "time");
        Assert.Contains(after, e => e.Href == "hr"); // The directory is core; only HR administration is Workforce.
        Assert.Equal(before.Count(e => e.ModuleId == "core"), after.Count);
        Assert.Equal("accounting", NavCatalog.OwnerOf("accounting/journal/123")?.ModuleId); // The page guard finds the module of a URL.
    }

    [Fact]
    public async Task Light_people_take_light_seats_first_then_spill_into_full_seats()
    {
        await using var seeded = await HostAsync();
        var admin = seeded.As(Admin).Host;
        var license = admin.Get<ILicenseService>();
        var invites = admin.Get<IInviteService>();
        var users = admin.Get<IUserAdminService>();

        // Admin, Finance and Timekeeper are full people; Pat and Val only hold the Employee role, so they are light.
        var before = await license.GetStatusAsync();
        Assert.Equal((5, 0), (before.SeatsUsed, before.LightSeatsUsed)); // No light seats on Community: counted as before.

        await Assert.ThrowsAsync<ArgumentException>(() => license.ActivateAsync(new ActivateLicenseRequest(Issue(seats: 2, lightSeats: 2)))); // Three full people don't fit in two full seats.
        var status = await license.ActivateAsync(new ActivateLicenseRequest(Issue(seats: 3, lightSeats: 2)));
        Assert.Equal((3, 3, 2, 2), (status.SeatsUsed, status.SeatLimit, status.LightSeatsUsed, status.LightSeatLimit));

        var light = await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("temp@example.com", "Temp", OrgRoles.Employee)));
        Assert.Contains("3 full seats and 2 light seats", light.Message); // Light seats are taken and the full ones too.

        await users.DeactivateAsync(Owner); // A light seat is free again.
        await invites.CreateAsync(new CreateInviteRequest("temp@example.com", "Temp", OrgRoles.Employee));
        var full = await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("boss@example.com", "Boss", OrgRoles.Finance))); // A full person needs a full seat.
        Assert.Contains("seats", full.Message);
    }

    [Fact]
    public async Task A_light_person_who_gains_a_role_becomes_a_full_seat_and_a_custom_role_with_permissions_counts_as_full()
    {
        await using var seeded = await HostAsync();
        var admin = seeded.As(Admin).Host;
        var license = admin.Get<ILicenseService>();
        await license.ActivateAsync(new ActivateLicenseRequest(Issue(seats: 10, lightSeats: 2)));
        Assert.Equal((3, 2), ((await license.GetStatusAsync()).SeatsUsed, (await license.GetStatusAsync()).LightSeatsUsed));

        await admin.Get<IUserAdminService>().SetRolesAsync(Owner, OrgRoles.Finance);

        var after = await license.GetStatusAsync();
        Assert.Equal((4, 1), (after.SeatsUsed, after.LightSeatsUsed));
    }

    [Fact]
    public async Task A_trial_reports_the_days_left_and_a_lapsed_trial_falls_back_at_once_with_no_grace()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();

        var soon = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue(expires: "2026-11-05", trial: true, modules: ModuleIds.Enterprise)));
        Assert.Equal(("Business", true, 4), (soon.Edition, soon.Trial, soon.DaysLeft));

        await Assert.ThrowsAsync<ArgumentException>(() => licenses.ActivateAsync(new ActivateLicenseRequest(Issue(expires: "2026-10-30", trial: true)))); // A lapsed trial can't be entered (no grace).

        await using var late = await HostAsync(now: Today.AddDays(10));
        late.As(Admin);
        await using (var db = await late.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<LicenseSettings>().Add(new LicenseSettings { Key = Issue(expires: "2026-11-05", trial: true, modules: ModuleIds.Enterprise) });
            await db.SaveChangesAsync();
        }

        var lapsed = await late.Host.Get<ILicenseService>().GetStatusAsync();
        Assert.Equal(("Community", true, false, -6), (lapsed.Edition, lapsed.Trial, lapsed.InGracePeriod, lapsed.DaysLeft));
        Assert.Contains("trial ended", lapsed.Problem);
        Assert.NotEmpty(await late.Host.Get<ICompanyService>().ListAsync()); // Reading is never gated.
    }

    [Fact]
    public async Task A_paid_WB2_key_keeps_its_14_day_grace_after_expiry()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();

        var status = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue(expires: "2026-10-25", modules: ModuleIds.Enterprise)));

        Assert.Equal(("Business", true, -7), (status.Edition, status.InGracePeriod, status.DaysLeft));
        Assert.True(status.HasModule("enterprise"));
    }
}

/// <summary>The trial banner and the "needs the module" message.</summary>
public sealed class LicenseBannerTests
{
    private static LicenseStatusDto Status(bool trial, int? daysLeft) => new("Business", EditionSource.Key, "Acme", null, false, 5, 1, null, [],
        [new ModuleStatusDto("accounting", "Accounting", "…", true, false, false)], 0, 0, trial, daysLeft);

    private static Bunit.BunitContext Context(LicenseStatusDto status, OrgRoles roles)
    {
        var ctx = Navigation.NavTestKit.NewContext(new Navigation.NavUser(roles));
        ctx.Services.AddSingleton(Navigation.Stub<ILicenseService>.Create(("GetStatusAsync", _ => Task.FromResult(status))));
        return ctx;
    }

    private static NavAccess Access(Bunit.BunitContext ctx) => NavAccess.LoadAsync(ctx.Services).GetAwaiter().GetResult();

    [Theory]
    [InlineData(7, "ends in 7 days")]
    [InlineData(1, "ends in 1 day")]
    [InlineData(0, "ends today")]
    [InlineData(-3, "has ended")]
    public void Administrators_see_a_banner_for_a_trial_with_a_week_or_less_left_or_lapsed(int days, string text)
    {
        using var ctx = Context(Status(true, days), OrgRoles.Admin);

        var banner = ctx.Render<Workbench.Ui.Layout.LicenseBanner>(p => p.Add(c => c.Access, Access(ctx)));

        Assert.Contains(text, banner.Find("[data-testid=license-banner]").TextContent);
    }

    [Theory]
    [InlineData(true, 8, OrgRoles.Admin)]
    [InlineData(false, 2, OrgRoles.Admin)]
    [InlineData(true, 2, OrgRoles.Finance)]
    public void The_banner_stays_away_for_a_longer_trial_a_paid_key_or_someone_who_cant_change_the_license(bool trial, int days, OrgRoles roles)
    {
        using var ctx = Context(Status(trial, days), roles);

        var banner = ctx.Render<Workbench.Ui.Layout.LicenseBanner>(p => p.Add(c => c.Access, Access(ctx)));

        Assert.Empty(banner.FindAll("[data-testid=license-banner]"));
    }

    [Fact]
    public void An_unlicensed_page_says_which_module_it_needs_and_what_to_do()
    {
        using var ctx = Context(Status(false, 100), OrgRoles.Admin);
        var entry = NavCatalog.OwnerOf("accounting/journal")!;

        var message = ctx.Render<Workbench.Ui.Layout.ModuleNeeded>(p => p.Add(c => c.Entry, entry));

        var text = message.Find("[data-testid=module-needed]").TextContent;
        Assert.Contains("Journal entries needs the Accounting module", text);
        Assert.Contains("Setup › License", text);
    }
}
