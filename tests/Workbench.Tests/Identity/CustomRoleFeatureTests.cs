using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Integrations;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Tests.Collaboration;
using Workbench.Tests.Integrations;
using Workbench.Tests.Odcs;
using Workbench.Tests.Reports;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>Everything that used to key off the fixed org roles takes custom roles too: approvals, SSO groups, invites, API keys and shared reports.</summary>
public sealed class CustomRoleFeatureTests
{
    private static async Task<Guid> AddRoleAsync(IWorkbenchDbContextFactory factory, string name, params string[] permissions)
    {
        await using var db = await factory.CreateAsync();
        var role = new Role { Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAtUtc = DateTime.UtcNow };
        role.Permissions.AddRange(permissions.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        db.Set<Role>().Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private static async Task GiveAsync(IWorkbenchDbContextFactory factory, string email, params Guid[] roleIds)
    {
        await using var db = await factory.CreateAsync();
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new AppUser { Email = email, DisplayName = email };
            db.Set<AppUser>().Add(user);
        }

        await RoleStore.ReplaceAsync(db, user, roleIds, "test", DateTime.UtcNow, default);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_approval_step_routed_to_a_custom_role_waits_for_someone_holding_it()
    {
        var host = await OdcTestHost.CreateAsync();
        var controllers = await AddRoleAsync(host.Factory, "Controllers", Permission.GlReportsView);
        await GiveAsync(host.Factory, "controller@example.com", controllers);
        await GiveAsync(host.Factory, "finance@example.com", BuiltInRoles.IdOf(OrgRoles.Finance));

        var admin = new TestUser { Email = "admin@example.com" };
        var workflows = new ApprovalWorkflowService(host.Factory, new ApprovalEngine(TimeProvider.System), new AuditRecorder(admin, TimeProvider.System), new AccessControl(host.Factory, admin));
        await workflows.SaveAsync(ApprovalDocumentTypes.ExpenseReport, new SaveApprovalWorkflowRequest("Controllers review", new ApprovalWorkflowDefinition(
        [
            new ApprovalStepDefinition("Supervisor", 1, new ApproverRule(ApproverRuleKind.Supervisor), WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
            new ApprovalStepDefinition("Controllers", 2, new ApproverRule(ApproverRuleKind.OrgRole, CustomRoleId: controllers)),
        ])));

        // A role that doesn't exist (or a built-in one passed as custom) is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => workflows.SaveAsync(ApprovalDocumentTypes.ExpenseReport, new SaveApprovalWorkflowRequest("Bad", new ApprovalWorkflowDefinition(
            [new ApprovalStepDefinition("Nobody", 1, new ApproverRule(ApproverRuleKind.OrgRole, CustomRoleId: Guid.NewGuid()))]))));

        var report = await host.SubmittedDevReportAsync(40m);
        host.As(OdcTestHost.Pm);
        Assert.Equal(OdcApprovalState.Submitted, (await host.Approvals.ApproveReportAsync(report.Id)).Status);

        // Finance isn't in the role; the controller is.
        host.As("finance@example.com");
        Assert.Empty((await host.Approvals.GetMyQueueAsync()).Reports);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveReportAsync(report.Id));
        host.As("controller@example.com");
        Assert.Single((await host.Approvals.GetMyQueueAsync()).Reports);
        Assert.Equal(OdcApprovalState.Approved, (await host.Approvals.ApproveReportAsync(report.Id)).Status);

        await using var db = await host.Factory.CreateAsync();
        Assert.Equal(["controller@example.com"], await ApproverRoles.MembersAsync(db, ApproverRoles.CustomKey(controllers), default));
        Assert.Equal("Controllers", await ApproverRoles.LabelAsync(db, ApproverRoles.CustomKey(controllers), default));
    }

    [Fact]
    public async Task Sso_groups_invites_and_mfa_take_custom_roles()
    {
        await using var seeded = await CreateAsync();
        var factory = seeded.Host.Get<IWorkbenchDbContextFactory>();
        var auditors = await AddRoleAsync(factory, "Auditors", Permission.SetupAuditLog);
        var securityDesk = await AddRoleAsync(factory, "Security desk", Permission.AdminSecurity);
        await SignInPolicyTests.SetAsync(seeded, s => { s.SyncRolesFromGroups = true; s.MfaRequirement = MfaRequirementLevel.AdminsOnly; },
            new SsoGroupMapping { GroupValue = "wb-auditors", Roles = (int)OrgRoles.Timekeeper, CustomRoleIds = auditors.ToString() },
            new SsoGroupMapping { GroupValue = "wb-security", CustomRoleIds = securityDesk.ToString() });
        var gate = seeded.Host.Get<SignInGate>();
        var mfa = new SignInClaims(["pwd", "mfa"], [], ["wb-auditors"]);

        Assert.True((await gate.EvaluateAsync(Viewer, null, "sso", claims: mfa)).Allowed);
        var roles = seeded.As(Admin).Host.Get<IRoleService>();
        Assert.Equal(["Timekeeper", "Auditors"], (await roles.GetUserRolesAsync(Viewer)).Roles.Select(r => r.Name));

        // A custom role with an Administration permission counts as an administrator for "MFA for admins".
        Assert.False((await gate.EvaluateAsync(Owner, null, "sso", claims: new SignInClaims(["pwd"], [], ["wb-security"]))).Allowed);
        Assert.True((await gate.EvaluateAsync(Owner, null, "sso", claims: new SignInClaims(["pwd", "mfa"], [], ["wb-security"]))).Allowed);
        Assert.Equal(["Security desk"], (await roles.GetUserRolesAsync(Owner)).Roles.Select(r => r.Name));

        // Invites carry custom roles, granted when the person first signs in.
        await seeded.As(Admin).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("new.auditor@example.com", "New Auditor", OrgRoles.Employee, [auditors]));
        Assert.True((await gate.EvaluateAsync("new.auditor@example.com", null, "dev")).Allowed);
        var held = await roles.GetUserRolesAsync("new.auditor@example.com");
        Assert.Equal(["Auditors"], held.Roles.Select(r => r.Name));
        Assert.Equal([Permission.SetupAuditLog], held.Permissions);

        // The settings API saves custom roles on group mappings.
        var saved = await seeded.As(Admin).Host.Get<ISignInSettingsService>().SaveProvisioningAsync(new SaveProvisioningRequest(
            JitProvisioningMode.InviteOnly, null, OrgRoles.Employee, "groups", true, [new SaveSsoGroupMapping("wb-auditors", OrgRoles.Employee, "Auditors", [auditors])]));
        Assert.Equal(["Auditors"], saved.GroupMappings.Single().CustomRoles!.Select(r => r.Name));
    }

    [Fact]
    public async Task An_api_key_acts_with_its_custom_roles_and_keeps_them()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var factory = host.Get<IWorkbenchDbContextFactory>();
        var readers = await AddRoleAsync(factory, "Ledger readers", Permission.GlReportsView);
        var created = await host.Get<IApiKeyService>().CreateAsync(new CreateApiKeyRequest("BI refresh", OrgRoles.Employee, ["time:read"], CustomRoleIds: [readers]));
        Assert.Equal(["Ledger readers"], created.ApiKey.CustomRoles!.Select(r => r.Name));

        await using (var db = await factory.CreateAsync())
        {
            var resolved = await RoleStore.ResolveAsync(db, created.ApiKey.ServiceUserEmail, default);
            Assert.Equal([Permission.GlReportsView], resolved.Permissions);

            // Someone strips the role from the service user; the next request puts it back.
            var user = await db.Set<AppUser>().SingleAsync(u => u.Email == created.ApiKey.ServiceUserEmail);
            await RoleStore.ReplaceAsync(db, user, [], "test", DateTime.UtcNow, default);
            await db.SaveChangesAsync();
        }

        await host.Get<ApiKeyAuthenticator>().AuthenticateAsync(created.Key, null, CancellationToken.None);
        await using (var db = await factory.CreateAsync())
        {
            Assert.True((await RoleStore.ResolveAsync(db, created.ApiKey.ServiceUserEmail, default)).Permissions.Has(Permission.GlReportsView));
        }

        // A key can't manage roles, even one holding the permission.
        var managers = await AddRoleAsync(factory, "Role managers", Permission.AdminRoles);
        var powerful = await host.Get<IApiKeyService>().CreateAsync(new CreateApiKeyRequest("Provisioning", OrgRoles.Employee, ["time:read"], CustomRoleIds: [managers]));
        host.As(powerful.ApiKey.ServiceUserEmail);
        var refused = await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchForbiddenException>(() => host.Get<IRoleService>().ListAsync());
        Assert.Contains("API keys", refused.Message);
    }

    [Fact]
    public async Task A_report_shared_with_a_custom_role_is_visible_to_its_holders_only()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var factory = kit.Seeded.Host.Get<IWorkbenchDbContextFactory>();
        var readers = await AddRoleAsync(factory, "Report readers");
        await GiveAsync(factory, Viewer, readers);

        kit.Seeded.As(Admin);
        var report = await kit.Saved.CreateAsync(new SaveReportRequest("Labor for readers", null, ReportVisibility.Shared, [], StandardReportKeys.LaborDistribution,
            ReportsKit.Day, null, [readers]));
        Assert.Equal(["Report readers"], report.SharedWithRoles!.Select(r => r.Name));

        kit.Seeded.As(Viewer);
        Assert.Contains(await kit.Saved.ListAsync(), r => r.Id == report.Id);
        kit.Seeded.As(Owner);
        Assert.DoesNotContain(await kit.Saved.ListAsync(), r => r.Id == report.Id);
    }
}
