using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>The organization's sign-in rules: MFA enforcement, just-in-time provisioning, IdP-managed roles, settings and re-authentication.</summary>
public sealed class SignInPolicyTests
{
    private static readonly SignInClaims WithMfa = new(["pwd", "mfa"], [], null);
    private static readonly SignInClaims PasswordOnly = new(["pwd"], [], null);

    [Fact]
    public async Task Mfa_for_everyone_refuses_a_password_only_sign_in_with_a_clear_reason_and_logs_it()
    {
        await using var seeded = await CreateAsync();
        await SetAsync(seeded, s => s.MfaRequirement = MfaRequirementLevel.Everyone);
        var gate = seeded.Host.Get<SignInGate>();

        var refused = await gate.EvaluateAsync(Viewer, null, "sso", claims: PasswordOnly);
        var allowed = await gate.EvaluateAsync(Viewer, null, "sso", claims: WithMfa);

        Assert.False(refused.Allowed);
        Assert.Equal(SignInDenials.Mfa, refused.Code);
        Assert.Contains("multi-factor", refused.Reason);
        Assert.True(allowed.Allowed);
        var events = await EventsAsync(seeded);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.MfaRequired && e.Target == Viewer && e.Outcome == SecurityOutcomes.Denied && e.Detail!.Contains("amr=pwd"));
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignInSuccess && e.Actor == Viewer && e.Detail!.Contains("amr=pwd,mfa"));
    }

    [Fact]
    public async Task Mfa_for_admins_only_lets_employees_in_without_it_but_not_admins()
    {
        await using var seeded = await CreateAsync();
        await SetAsync(seeded, s => s.MfaRequirement = MfaRequirementLevel.AdminsOnly);
        var gate = seeded.Host.Get<SignInGate>();

        Assert.True((await gate.EvaluateAsync(Viewer, null, "oidc", claims: PasswordOnly)).Allowed);
        Assert.False((await gate.EvaluateAsync(Admin, null, "oidc", claims: PasswordOnly)).Allowed);
        Assert.True((await gate.EvaluateAsync(Admin, null, "oidc", claims: WithMfa)).Allowed);

        // Bearer tokens (the desktop app) are held to the same rule, and the dev sign-in has no second factor to check.
        Assert.False((await gate.EvaluateAsync(Admin, null, "bearer", interactive: false, claims: PasswordOnly)).Allowed);
        Assert.True((await gate.EvaluateAsync(Admin, null, "dev")).Allowed);
    }

    [Fact]
    public async Task An_acr_value_the_organization_accepts_proves_mfa()
    {
        await using var seeded = await CreateAsync();
        await SetAsync(seeded, s =>
        {
            s.MfaRequirement = MfaRequirementLevel.Everyone;
            s.AcceptedAmrValues = "";
            s.AcceptedAcrValues = "urn:okta:loa:2fa:any c1";
        });
        var gate = seeded.Host.Get<SignInGate>();

        Assert.False((await gate.EvaluateAsync(Viewer, null, "sso", claims: WithMfa)).Allowed);
        Assert.True((await gate.EvaluateAsync(Viewer, null, "sso", claims: new SignInClaims(["pwd"], ["urn:okta:loa:2fa:any"], null))).Allowed);
        Assert.True((await gate.EvaluateAsync(Viewer, null, "sso", claims: new SignInClaims([], ["c1"], null))).Allowed);
    }

    [Fact]
    public async Task Mfa_is_checked_before_an_invite_is_accepted()
    {
        await using var seeded = await CreateAsync();
        await seeded.As(Admin).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("new@example.com", "New", OrgRoles.Admin));
        await SetAsync(seeded, s => s.MfaRequirement = MfaRequirementLevel.AdminsOnly);

        var refused = await seeded.Host.Get<SignInGate>().EvaluateAsync("new@example.com", null, "sso", claims: PasswordOnly);

        Assert.False(refused.Allowed);
        Assert.Equal(InviteStatus.Pending, (await seeded.Host.Get<IInviteService>().ListAsync()).Single().Status);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.False(await db.Set<AppUser>().AnyAsync(u => u.Email == "new@example.com"));
    }

    [Fact]
    public async Task Just_in_time_provisioning_creates_people_from_allowed_domains_with_default_and_group_roles()
    {
        await using var seeded = await CreateAsync();
        await SetAsync(seeded, s =>
        {
            s.JitMode = JitProvisioning.AllowedDomains;
            s.AllowedEmailDomains = "acme.example, @acme-federal.example";
            s.JitDefaultRoles = (int)OrgRoles.Timekeeper;
        }, new SsoGroupMapping { GroupValue = "finance-team", Roles = (int)OrgRoles.Finance });
        var gate = seeded.Host.Get<SignInGate>();

        var jit = await gate.EvaluateAsync("Pat@Acme-Federal.example", "Pat", "sso", claims: new SignInClaims(["mfa"], [], ["finance-team", "other"]));
        var outsider = await gate.EvaluateAsync("someone@elsewhere.example", null, "sso", claims: WithMfa);
        var viaDev = await gate.EvaluateAsync("dev-only@acme.example", null, "dev");

        Assert.True(jit.Allowed);
        Assert.False(outsider.Allowed);
        Assert.False(viaDev.Allowed); // just-in-time needs an identity provider to vouch for the person
        var user = (await seeded.As(Admin).Host.Get<IUserAdminService>().ListUsersAsync("pat@acme-federal.example")).Single();
        Assert.Equal(OrgRoles.Finance | OrgRoles.Timekeeper, user.Roles);
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.UserCreated && e.Target == "pat@acme-federal.example" && e.Detail!.Contains("Just-in-time"));
    }

    [Fact]
    public async Task When_the_idp_manages_roles_each_sign_in_syncs_them_but_never_removes_the_last_admin()
    {
        await using var seeded = await CreateAsync();
        await SetAsync(seeded, s => s.SyncRolesFromGroups = true,
            new SsoGroupMapping { GroupValue = "wb-finance", Roles = (int)OrgRoles.Finance },
            new SsoGroupMapping { GroupValue = "wb-admins", Roles = (int)OrgRoles.Admin });
        var gate = seeded.Host.Get<SignInGate>();

        await gate.EvaluateAsync(Viewer, null, "sso", claims: new SignInClaims([], [], ["wb-finance"]));
        await gate.EvaluateAsync(Admin, null, "sso", claims: new SignInClaims([], [], [])); // the only admin, now in no group
        await gate.EvaluateAsync(Finance, null, "sso", claims: new SignInClaims([], [], null)); // no groups claim: roles untouched

        var users = await seeded.As(Admin).Host.Get<IUserAdminService>().ListUsersAsync();
        Assert.Equal(OrgRoles.Finance, users.Single(u => u.Email == Viewer).Roles);
        Assert.Equal(OrgRoles.Admin, users.Single(u => u.Email == Admin).Roles);
        Assert.Equal(OrgRoles.Finance, users.Single(u => u.Email == Finance).Roles);
        var events = await EventsAsync(seeded);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.RolesChanged && e.Target == Viewer && e.Actor == SignInGate.SsoActor);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.RolesChanged && e.Target == Admin && e.Outcome == SecurityOutcomes.Failure);
    }

    [Fact]
    public async Task Requiring_the_organization_idp_refuses_the_server_sign_in()
    {
        await using var seeded = await CreateAsync();
        await SetAsync(seeded, s =>
        {
            s.SsoEnabled = true;
            s.Authority = "https://login.example.test/acme/v2.0";
            s.ClientId = "workbench";
            s.RequireOrganizationIdp = true;
        });
        var gate = seeded.Host.Get<SignInGate>();

        var server = await gate.EvaluateAsync(Viewer, null, "oidc");
        var organization = await gate.EvaluateAsync(Viewer, null, "sso");

        Assert.False(server.Allowed);
        Assert.Equal(SignInDenials.OrganizationIdp, server.Code);
        Assert.False((await gate.EvaluateAsync(Viewer, null, "bearer", interactive: false)).Allowed);
        Assert.True(organization.Allowed);
    }

    [Fact]
    public async Task Settings_are_for_admins_validated_and_the_client_secret_is_stored_encrypted()
    {
        await using var seeded = await CreateAsync();
        var service = seeded.As(Viewer).Host.Get<ISignInSettingsService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.GetAsync());

        seeded.As(Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveProviderAsync(Provider("http://login.example.test/acme")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveProviderAsync(Provider(null)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveProvisioningAsync(
            new SaveProvisioningRequest(JitProvisioningMode.AllowedDomains, "not a domain!", OrgRoles.Employee, null, false, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveProvisioningAsync(
            new SaveProvisioningRequest(JitProvisioningMode.InviteOnly, null, OrgRoles.Admin, null, false, [])));

        var saved = await service.SaveProviderAsync(Provider("https://login.microsoftonline.com/0000/v2.0"));
        var mappings = await service.SaveProvisioningAsync(new SaveProvisioningRequest(JitProvisioningMode.AllowedDomains, "Example.com", OrgRoles.Timekeeper, null, true,
            [new SaveSsoGroupMapping("11111111-aaaa", OrgRoles.Finance, "Finance team")]));

        Assert.True(saved.SsoEnabled);
        Assert.True(saved.HasClientSecret);
        Assert.Equal("example.com", mappings.AllowedEmailDomains);
        Assert.Equal(OrgRoles.Finance, Assert.Single(mappings.GroupMappings).Roles);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<SignInSettings>().SingleAsync();
        Assert.NotNull(row.ProtectedClientSecret);
        Assert.DoesNotContain("s3cret-value", row.ProtectedClientSecret);
        Assert.Equal("s3cret-value", (await seeded.Host.Get<SignInSettingsStore>().GetAsync()).GetClientSecret());
        var events = await EventsAsync(seeded);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignInSettingsChanged && e.Detail!.Contains("client secret replaced"));
        Assert.DoesNotContain(events, e => e.Detail != null && e.Detail.Contains("s3cret-value"));
    }

    [Fact]
    public async Task Lock_out_guards_need_the_admin_to_have_used_mfa_and_the_organization_idp()
    {
        var signIn = new FakeSignIn { Facts = new SignInFacts(DateTime.UtcNow, MfaPerformed: false, Method: "oidc") };
        await using var seeded = await CreateAsync(s => s.AddScoped<ISignInContext>(_ => signIn));
        var service = seeded.As(Admin).Host.Get<ISignInSettingsService>();
        await service.SaveProviderAsync(Provider("https://login.example.test/acme"));

        var mfa = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveMfaPolicyAsync(new SaveMfaPolicyRequest(MfaRequirement.Everyone, null, null, null)));
        var onlyIdp = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveProviderAsync(Provider("https://login.example.test/acme") with { RequireOrganizationIdp = true }));
        Assert.Contains("lock", mfa.Message);
        Assert.Contains("first", onlyIdp.Message);

        signIn.Facts = new SignInFacts(DateTime.UtcNow, MfaPerformed: true, Method: "sso");
        Assert.Equal(MfaRequirement.Everyone, (await service.SaveMfaPolicyAsync(new SaveMfaPolicyRequest(MfaRequirement.Everyone, null, null, null))).Mfa);
        Assert.True((await service.SaveProviderAsync(Provider("https://login.example.test/acme") with { RequireOrganizationIdp = true })).RequireOrganizationIdp);
    }

    [Fact]
    public async Task Sensitive_actions_need_a_recent_sign_in_and_tell_the_ui()
    {
        var signIn = new FakeSignIn { Facts = new SignInFacts(DateTime.UtcNow.AddMinutes(-20), true, "sso") };
        await using var seeded = await CreateAsync(s => s.AddScoped<ISignInContext>(_ => signIn));
        seeded.As(Admin);
        var raised = new List<ReauthenticationRequiredException>();
        seeded.Host.Get<IReauthenticationService>().Required += raised.Add;

        var payroll = await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => seeded.Host.Get<IPayrollProfileService>().RevealAsync(Guid.NewGuid()));
        var bank = await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => seeded.Host.Get<IBankAccountService>().RevealAccountNumberAsync(Guid.NewGuid()));
        var settings = await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => seeded.Host.Get<ISignInSettingsService>().SaveSessionPolicyAsync(new SaveSessionPolicyRequest(10, null)));
        var status = await seeded.Host.Get<IReauthenticationService>().GetStatusAsync();

        Assert.Equal(SensitiveActions.PayRateReveal, payroll.Action);
        Assert.Contains("sign in again to show pay rates", payroll.Message);
        Assert.Equal(SensitiveActions.BankDetails, bank.Action);
        Assert.Equal(SensitiveActions.SignInSettingsChange, settings.Action);
        Assert.Equal(3, raised.Count);
        Assert.False(status.IsRecent);
        Assert.Equal(15, status.WindowMinutes);
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.ReauthenticationRequired && e.Target == SensitiveActions.PayRateReveal);

        // After signing in again, the same calls reach the service (which then finds nothing).
        signIn.Facts = new SignInFacts(DateTime.UtcNow, true, "sso");
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.Host.Get<IPayrollProfileService>().RevealAsync(Guid.NewGuid()));
        Assert.True((await seeded.Host.Get<IReauthenticationService>().GetStatusAsync()).IsRecent);
    }

    [Fact]
    public async Task People_without_the_role_are_refused_before_being_asked_to_sign_in_again()
    {
        var signIn = new FakeSignIn { Facts = new SignInFacts(DateTime.UtcNow.AddHours(-2), false, "sso") };
        await using var seeded = await CreateAsync(s => s.AddScoped<ISignInContext>(_ => signIn));

        var refused = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Viewer).Host.Get<IPayrollProfileService>().RevealAsync(Guid.NewGuid()));

        Assert.IsNotType<ReauthenticationRequiredException>(refused);
    }

    [Fact]
    public async Task The_dcaa_checklist_shows_every_sf1408_criterion_and_keeps_answers()
    {
        await using var seeded = await CreateAsync();
        var service = seeded.As(Admin).Host.Get<IComplianceChecklistService>();

        var checklist = await service.GetAsync(ComplianceChecklists.DcaaSf1408);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAnswerAsync(ComplianceChecklists.DcaaSf1408, "preproduction", new SaveChecklistAnswerRequest(ChecklistStatus.NotApplicable, null)));
        await service.SaveAnswerAsync(ComplianceChecklists.DcaaSf1408, "timekeeping", new SaveChecklistAnswerRequest(ChecklistStatus.Partial, "Policy draft pending"));
        var answered = await service.SaveAnswerAsync(ComplianceChecklists.DcaaSf1408, "timekeeping", new SaveChecklistAnswerRequest(ChecklistStatus.Meets, null));

        Assert.Equal(13, checklist.Items.Count(i => i.Reference.StartsWith("SF 1408 item")));
        Assert.All(checklist.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.WorkbenchFeature)));
        Assert.Contains(checklist.Items, i => i.Key == "unallowable" && i.CpaReview && i.LiveCheckPassed == false);
        Assert.Equal(ChecklistStatus.Meets, answered.Status);
        Assert.Equal(Admin, answered.ReviewedBy);
        Assert.Equal(ChecklistStatus.Meets, (await service.GetAsync(ComplianceChecklists.DcaaSf1408)).Items.Single(i => i.Key == "timekeeping").Status);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(2, await db.Set<ComplianceChecklistEntry>().CountAsync()); // history kept
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Finance).Host.Get<IComplianceChecklistService>().GetAsync(ComplianceChecklists.DcaaSf1408));
    }

    private static SaveSsoProviderRequest Provider(string? authority) =>
        new(true, IdentityProviderKind.EntraId, "Acme", authority, "workbench-client", "s3cret-value", null, false);

    internal static async Task SetAsync(SeededHost seeded, Action<SignInSettings> change, params SsoGroupMapping[] mappings)
    {
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var settings = new SignInSettings { UpdatedAtUtc = DateTime.UtcNow };
            change(settings);
            db.Set<SignInSettings>().Add(settings);
            db.Set<SsoGroupMapping>().AddRange(mappings);
            await db.SaveChangesAsync();
        }

        seeded.Host.Get<SignInSettingsStore>().Invalidate();
    }

    private static async Task<List<SecurityEvent>> EventsAsync(SeededHost seeded)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<SecurityEvent>().AsNoTracking().ToListAsync();
    }

    private sealed class FakeSignIn : ISignInContext
    {
        public SignInFacts? Facts { get; set; }

        public SignInFacts? Current => Facts;
    }
}
