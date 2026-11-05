using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application;
using Workbench.Application.Licensing;
using Workbench.Contracts;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Licensing;
using Workbench.Tests.MyWork;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Licensing;

/// <summary>Editions: signed keys, Business-only actions, seats, expiry with grace, and the hosted plan.</summary>
public sealed class LicensingTests
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly DateTimeOffset Today = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private static string Issue(int seats = 25, string expires = "2027-11-01", string edition = "business", ECDsa? signer = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, licensee = "Acme Federal", edition, seats, expires });
        var sig = (signer ?? Signer).SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"WB1.{B64(payload)}.{B64(sig)}";
    }

    private static Task<Identity.SeededHost> HostAsync(string? hostedPlan = null) => CreateAsync(s =>
    {
        s.Replace(ServiceDescriptor.Singleton(new LicenseOptions { PublicKeyPem = PublicKey }));
        s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new MyWorkServiceTests.FixedClock(Today)));
        if (hostedPlan is not null)
        {
            s.Replace(ServiceDescriptor.Singleton<IHostedPlanSource>(new FakePlan(hostedPlan)));
        }
    });

    private sealed class FakePlan(string plan) : IHostedPlanSource
    {
        public Task<string?> GetPlanAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(plan);
    }

    [Fact]
    public void A_key_signed_by_us_verifies_and_anything_else_does_not()
    {
        Assert.True(LicenseKeys.TryVerify(Issue(seats: 7), PublicKey, out var claims, out _));
        Assert.Equal(("Acme Federal", Edition.Business, 7, new DateOnly(2027, 11, 1)), (claims!.Licensee, claims.Edition, claims.Seats, claims.ExpiresOn));

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(LicenseKeys.TryVerify(Issue(signer: stranger), PublicKey, out _, out var wrongSigner));
        Assert.Contains("wasn't issued", wrongSigner);

        var parts = Issue(seats: 7).Split('.');
        var forged = $"{parts[0]}.{Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"v":1,"licensee":"Me","edition":"business","seats":9999,"expires":"2099-01-01"}""")).TrimEnd('=')}.{parts[2]}";
        Assert.False(LicenseKeys.TryVerify(forged, PublicKey, out _, out _));
        Assert.False(LicenseKeys.TryVerify("not a key", PublicKey, out _, out var shape));
        Assert.Contains("doesn't look like", shape);
        Assert.False(LicenseKeys.TryVerify("WB1.@@@.@@@", PublicKey, out _, out var damaged));
        Assert.Contains("damaged", damaged);
    }

    [Fact]
    public async Task Without_a_license_the_edition_is_Community_and_Business_actions_are_refused()
    {
        await using var seeded = await HostAsync();
        var admin = seeded.As(Admin).Host;

        var status = await admin.Get<ILicenseService>().GetStatusAsync();
        var refused = await Assert.ThrowsAsync<WorkbenchEditionException>(() => admin.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("B", "Second Co")));

        Assert.Equal(("Community", EditionSource.None), (status.Edition, status.Source));
        Assert.All(status.Features, f => Assert.False(f.Included));
        Assert.Contains("Business", refused.Message);
        Assert.IsAssignableFrom<WorkbenchForbiddenException>(refused); // Existing handlers for "forbidden" still catch it.
    }

    [Fact]
    public async Task A_valid_key_turns_Business_on_and_removing_it_turns_it_off_without_touching_data()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();

        var on = await licenses.ActivateAsync(new ActivateLicenseRequest(Issue()));
        var created = await seeded.Host.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("B", "Second Co"));
        var off = await licenses.RemoveAsync();
        var stillListed = await seeded.Host.Get<ICompanyService>().ListAsync();

        Assert.Equal(("Business", "Acme Federal"), (on.Edition, on.Licensee));
        Assert.All(on.Features, f => Assert.True(f.Included));
        Assert.Equal("Community", off.Edition);
        Assert.Contains(stillListed, c => c.Id == created.Id); // Reading is never gated.
    }

    [Fact]
    public async Task Only_an_administrator_can_change_the_license_but_everyone_can_see_it()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Viewer).Host.Get<ILicenseService>();

        Assert.Equal("Community", (await licenses.GetStatusAsync()).Edition);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => licenses.ActivateAsync(new ActivateLicenseRequest(Issue())));
    }

    [Fact]
    public async Task A_bad_or_expired_key_is_refused_with_a_reason()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();

        var junk = await Assert.ThrowsAsync<ArgumentException>(() => licenses.ActivateAsync(new ActivateLicenseRequest("WB1.x.y")));
        var old = await Assert.ThrowsAsync<ArgumentException>(() => licenses.ActivateAsync(new ActivateLicenseRequest(Issue(expires: "2026-01-01"))));

        Assert.NotEmpty(junk.Message);
        Assert.Contains("expired", old.Message);
        Assert.Equal("Community", (await licenses.GetStatusAsync()).Edition);
    }

    [Fact]
    public async Task An_expired_license_keeps_working_for_the_grace_period_then_stops_but_never_hides_data()
    {
        await using var seeded = await HostAsync();
        var licenses = seeded.As(Admin).Host.Get<ILicenseService>();
        await licenses.ActivateAsync(new ActivateLicenseRequest(Issue(expires: "2026-10-25"))); // 7 days ago, inside the 14-day grace

        var grace = await licenses.GetStatusAsync();
        Assert.Equal(("Business", true), (grace.Edition, grace.InGracePeriod));
        Assert.Contains("Renew by", grace.Problem);

        await using var late = await CreateAsync(s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new LicenseOptions { PublicKeyPem = PublicKey }));
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new MyWorkServiceTests.FixedClock(Today.AddDays(30))));
        });
        late.As(Admin);
        await using (var db = await late.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<LicenseSettings>().Add(new LicenseSettings { Key = Issue(expires: "2026-10-25") });
            await db.SaveChangesAsync();
        }

        var lapsed = await late.Host.Get<ILicenseService>().GetStatusAsync();
        Assert.Equal("Community", lapsed.Edition);
        Assert.Contains("untouched", lapsed.Problem);
        Assert.NotEmpty(await late.Host.Get<ICompanyService>().ListAsync());
    }

    [Fact]
    public async Task Every_active_person_takes_a_seat_whatever_their_role_and_the_license_limits_them()
    {
        await using var seeded = await HostAsync();
        var admin = seeded.As(Admin).Host;
        var license = admin.Get<ILicenseService>();
        Assert.Equal(5, (await license.GetStatusAsync()).SeatsUsed); // Three with roles and two employees: all take a seat.

        await Assert.ThrowsAsync<ArgumentException>(() => license.ActivateAsync(new ActivateLicenseRequest(Issue(seats: 4)))); // Fewer seats than people.
        await license.ActivateAsync(new ActivateLicenseRequest(Issue(seats: 5)));

        var invites = admin.Get<IInviteService>();
        var users = admin.Get<IUserAdminService>();
        var full = await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("new.hire@example.com", "New Hire", OrgRoles.Employee)));
        Assert.Contains("5 seats", full.Message);

        var existing = await users.SetRolesAsync(Finance, OrgRoles.Finance | OrgRoles.Timekeeper); // Changing roles costs nothing.
        Assert.True(existing.Roles.HasFlag(OrgRoles.Timekeeper));

        await users.DeactivateAsync(Owner); // Frees a seat at once.
        Assert.Equal(4, (await license.GetStatusAsync()).SeatsUsed);
        await invites.CreateAsync(new CreateInviteRequest("new.hire@example.com", "New Hire", OrgRoles.Employee));
        await Assert.ThrowsAsync<WorkbenchEditionException>(() => invites.CreateAsync(new CreateInviteRequest("second@example.com", "Second", OrgRoles.Employee))); // The pending invite holds the seat.
        await Assert.ThrowsAsync<WorkbenchEditionException>(() => users.ReactivateAsync(Owner)); // So does coming back.

        await license.ActivateAsync(new ActivateLicenseRequest(Issue(seats: 6)));
        await users.ReactivateAsync(Owner);
        Assert.Equal(5, (await license.GetStatusAsync()).SeatsUsed);
    }

    [Fact]
    public async Task A_hosted_plan_of_business_unlocks_Business_with_no_key()
    {
        await using var seeded = await HostAsync(hostedPlan: "Business");
        var status = await seeded.As(Admin).Host.Get<ILicenseService>().GetStatusAsync();
        var created = await seeded.Host.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("B", "Second Co"));

        Assert.Equal(("Business", EditionSource.HostedPlan, (int?)null), (status.Edition, status.Source, status.SeatLimit));
        Assert.Equal("B", created.Code);
    }

    [Fact]
    public void Every_Business_feature_is_described_and_gated_somewhere()
    {
        Assert.Equal(Enum.GetValues<Feature>().Length, FeatureCatalog.All.Count);
        Assert.All(FeatureCatalog.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
        Assert.All(Enum.GetValues<Feature>(), f => Assert.False(FeatureCatalog.IsIncluded(Edition.Community, f)));
        Assert.All(Enum.GetValues<Feature>(), f => Assert.True(FeatureCatalog.IsIncluded(Edition.Business, f)));
    }
}
