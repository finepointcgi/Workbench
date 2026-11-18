using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Licensing;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Security;
using Workbench.Domain.Licensing;
using Workbench.Tests.MyWork;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Licensing;

/// <summary>Seats per module (a person uses one when their roles grant any permission in it) and the Community cap.</summary>
public sealed class ModuleSeatTests
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly DateTimeOffset Today = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Issue(string modules, string? moduleSeats, int seats = 25, int lightSeats = 5)
    {
        var payload = Encoding.UTF8.GetBytes(
            $$"""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme","issuedAt":"2026-10-01","expires":"2027-11-01","seats":{{seats}},"lightSeats":{{lightSeats}},"modules":[{{modules}}]{{(moduleSeats is null ? "" : $",\"moduleSeats\":{moduleSeats}")}}}""");
        return $"WB2.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    /// <summary>Billing, enterprise and accounting enforced (a pricing choice made for these tests), the rest as shipped. (Accounting has no permission in the shipped product, so seats are counted on billing and enterprise.)</summary>
    private static Task<Identity.SeededHost> HostAsync(int communityMax = 0) => CreateAsync(s =>
    {
        var modules = ModuleCatalog.Default.Select(m => m.Id is ModuleIds.Billing or ModuleIds.Accounting or ModuleIds.Enterprise ? m with { Enforced = true, WholeModule = true } : m).ToList();
        s.Replace(ServiceDescriptor.Singleton(new LicenseOptions { PublicKeyPem = PublicKey, Modules = modules, CommunityMaxPeople = communityMax }));
        s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new MyWorkServiceTests.FixedClock(Today)));
    });

    [Fact]
    public async Task A_person_uses_a_seat_in_each_module_their_roles_reach_and_the_license_page_shows_it()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();

        var status = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue("\"billing\",\"enterprise\"", "{\"enterprise\":3,\"billing\":4}")));

        var enterprise = status.Modules!.Single(m => m.Id == "enterprise");
        var billing = status.Modules!.Single(m => m.Id == "billing");
        Assert.Equal((2, 3), (enterprise.SeatsUsed, enterprise.SeatsAllowed)); // The administrator and the Finance user (Finance holds the custom workflow permission).
        Assert.Equal((0, 4), (billing.SeatsUsed, billing.SeatsAllowed)); // Its one permission (revenue.manage) comes with the billing module, which is not loaded here: nobody reaches the module in this build.
        Assert.Null(status.Modules!.Single(m => m.Id == "capture").SeatsAllowed); // Not enforced: nothing limits it.
        Assert.Equal(0, status.Modules!.Single(m => m.Id == "core").SeatsUsed);
    }

    [Fact]
    public async Task Adding_a_person_beyond_a_modules_seats_is_refused_with_what_to_do_but_others_still_fit()
    {
        await using var seeded = await HostAsync();
        var host = seeded.As(Admin).Host;
        await host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue("\"enterprise\"", "{\"enterprise\":3}")));
        var invites = host.Get<IInviteService>();

        await invites.CreateAsync(new CreateInviteRequest("second.finance@example.com", "Second Finance", OrgRoles.Finance)); // The third enterprise seat; pending invitations hold one.
        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("third.finance@example.com", "Third Finance", OrgRoles.Finance)));

        Assert.Contains("Enterprise", refused.Message);
        Assert.Contains("(3)", refused.Message);
        Assert.Contains("Setup › License", refused.Message);
        Assert.Contains("without", refused.Message);
        await invites.CreateAsync(new CreateInviteRequest("light@example.com", "Light Person", OrgRoles.Employee)); // Employee-level access reaches no module.
        await invites.CreateAsync(new CreateInviteRequest("tk@example.com", "Another Timekeeper", OrgRoles.Timekeeper)); // Core permissions only.
    }

    [Fact]
    public async Task Changing_someones_roles_into_a_full_module_is_refused_and_revoking_the_invite_frees_the_seat()
    {
        await using var seeded = await HostAsync();
        var host = seeded.As(Admin).Host;
        await host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue("\"enterprise\"", "{\"enterprise\":2}")));
        var users = host.Get<IUserAdminService>();
        var invites = host.Get<IInviteService>();

        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => users.SetRolesAsync(Owner, OrgRoles.Finance));
        Assert.Contains("Enterprise", refused.Message);
        await users.SetRolesAsync(Owner, OrgRoles.Timekeeper); // A core role is fine.

        await users.SetRolesAsync(Finance, OrgRoles.Employee); // The Finance person leaves enterprise: a seat is free.
        await users.SetRolesAsync(Owner, OrgRoles.Finance);
        await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("again@example.com", "Again", OrgRoles.Finance)));

        await users.SetRolesAsync(Owner, OrgRoles.Finance | OrgRoles.Timekeeper); // Someone who already uses the seat keeps it when their roles change.
    }

    [Fact]
    public async Task Reactivating_someone_needs_the_seats_of_every_module_they_hold()
    {
        await using var seeded = await HostAsync();
        var host = seeded.As(Admin).Host;
        var users = host.Get<IUserAdminService>();
        await users.DeactivateAsync(Finance);
        await host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue("\"enterprise\"", "{\"enterprise\":1}")));
        await users.SetRolesAsync(Owner, OrgRoles.Employee);
        await Assert.ThrowsAsync<WorkbenchEditionException>(() => users.ReactivateAsync(Finance)); // The administrator holds the one enterprise seat.
    }

    [Fact]
    public async Task A_key_without_moduleSeats_behaves_as_before_seats_cover_every_module()
    {
        await using var seeded = await HostAsync();
        var host = seeded.As(Admin).Host;
        var status = await host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue("\"billing\",\"accounting\"", null, seats: 4, lightSeats: 2)));

        Assert.Equal(4, status.Modules!.Single(m => m.Id == "accounting").SeatsAllowed); // seats stands in for the module's.
        await host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("finance2@example.com", "Finance Two", OrgRoles.Finance)); // The 4th full seat.
        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("finance3@example.com", "Finance Three", OrgRoles.Finance)));
        Assert.Contains("seats", refused.Message); // The existing full-seat check answers first; behaviour is unchanged.
    }

    [Fact]
    public async Task A_legacy_finance_key_limits_both_billing_and_accounting_by_its_moduleSeats()
    {
        await using var seeded = await HostAsync();
        var status = await seeded.As(Admin).Host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue("\"finance\"", "{\"finance\":9}")));

        Assert.Equal((9, 9), (status.Modules!.Single(m => m.Id == "billing").SeatsAllowed, status.Modules!.Single(m => m.Id == "accounting").SeatsAllowed));
    }

    [Fact]
    public async Task The_community_cap_refuses_adding_people_once_reached_and_nothing_else()
    {
        await using var seeded = await HostAsync(communityMax: 5); // The seeded organization has five people.
        var host = seeded.As(Admin).Host;
        var invites = host.Get<IInviteService>();
        var users = host.Get<IUserAdminService>();

        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("sixth@example.com", "Sixth", OrgRoles.Employee)));
        Assert.Contains("limited to 5 people", refused.Message);
        Assert.Contains("Setup › License", refused.Message);

        await users.SetRolesAsync(Owner, OrgRoles.Timekeeper); // Changing roles adds nobody.
        await users.DeactivateAsync(Viewer); // Deactivating frees a place...
        await invites.CreateAsync(new CreateInviteRequest("sixth@example.com", "Sixth", OrgRoles.Employee)); // ...and the invitation takes it.
        await Assert.ThrowsAsync<WorkbenchEditionException>(() => users.ReactivateAsync(Viewer)); // Pending invitations hold a place.
    }

    [Fact]
    public async Task The_community_cap_is_off_by_default_and_never_applies_to_a_licensed_server()
    {
        await using (var unlimited = await HostAsync())
        {
            await unlimited.As(Admin).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("sixth@example.com", "Sixth", OrgRoles.Employee));
        }

        await using var capped = await HostAsync(communityMax: 5);
        var host = capped.As(Admin).Host;
        await host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue("\"billing\"", null)));
        await host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("sixth@example.com", "Sixth", OrgRoles.Employee)); // A license key lifts the cap.
    }
}
