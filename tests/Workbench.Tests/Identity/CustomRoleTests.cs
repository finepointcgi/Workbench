using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Ui.Navigation;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>Custom roles (Admin › Roles): grants and denials, several roles at once, lockout protection and the audit trail.</summary>
public sealed class CustomRoleTests
{
    private static async Task<RoleDto> CreateRoleAsync(SeededHost seeded, string name, params string[] permissions) =>
        await seeded.As(Admin).Host.Get<IRoleService>().CreateAsync(new SaveRoleRequest(name, $"{name} for tests", permissions));

    private static Task<UserRolesDto> AssignAsync(SeededHost seeded, string email, params Guid[] roleIds) =>
        seeded.As(Admin).Host.Get<IRoleService>().SetUserRolesAsync(email, roleIds);

    [Fact]
    public async Task A_custom_role_grants_only_its_permissions_across_services()
    {
        await using var seeded = await CreateAsync();
        var clerk = await CreateRoleAsync(seeded, "Journal clerk", Permission.GlJournalsPost, Permission.VendorsManage);
        await AssignAsync(seeded, Viewer, clerk.Id);

        seeded.As(Viewer);
        Assert.NotNull(await seeded.Host.Get<IJournalService>().ListAsync(new JournalQuery()));
        Assert.NotNull(await seeded.Host.Get<IVendorService>().ListAsync());

        // Everything else Finance has stays closed: the chart of accounts, AP bills, payroll, HR, users and roles.
        var denied = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IChartOfAccountsService>().ListAccountsAsync());
        Assert.Contains("Manage the chart of accounts", denied.Message);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IPayGroupService>().ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IUserAdminService>().ListUsersAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IRoleService>().ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IAccountingReportService>().GetTrialBalanceAsync(Guid.NewGuid()));

        var me = await seeded.Host.Get<ICurrentUserContext>().GetAsync();
        Assert.Equal(OrgRoles.Employee, me.Roles);
        Assert.Equal(["Journal clerk"], me.AssignedRoles!.Select(r => r.Name));
        Assert.True(me.Can(Permission.GlJournalsPost));
        Assert.False(me.Can(Permission.GlAccountsManage));
    }

    [Fact]
    public async Task The_menu_and_page_guards_follow_the_role()
    {
        var user = new CurrentUserInfo("x@example.com", "X", OrgRoles.Employee, null, [Permission.GlJournalsPost, Permission.SetupPeople]);
        var menu = NavCatalog.MenuEntries(new NavAccess(user)).Select(e => e.Href).ToList();
        var palette = NavCatalog.PaletteEntries(new NavAccess(user)).Select(e => e.Href).ToList();

        Assert.Contains("accounting/journal", menu);
        Assert.Contains("setup/people", menu);
        Assert.Contains("setup/org-units", menu);
        Assert.DoesNotContain("accounting/accounts", menu);
        Assert.DoesNotContain("billing", menu);
        Assert.DoesNotContain("admin/users", palette);
        Assert.DoesNotContain("payroll", palette);
        Assert.Contains("time", menu); // Everyone's pages stay.

        // Area layouts let in exactly the pages whose menu entry the user can see.
        static bool Setup(PermissionSet p) => p.Any(k => PermissionCatalog.Find(k)?.Area == PermissionCatalog.AreaSetup);
        Assert.True(NavCatalog.PageAllowed("setup/people", user, Setup));
        Assert.True(NavCatalog.PageAllowed("setup/people/0193f1c2-0000-7000-8000-000000000001", user, Setup));
        Assert.False(NavCatalog.PageAllowed("setup/contracts", user, Setup));
        Assert.False(NavCatalog.PageAllowed("setup/fiscal-calendar", user, Setup));
        Assert.True(NavCatalog.PageAllowed("accounting/journal", user, _ => false));
        Assert.False(NavCatalog.PageAllowed("accounting/accounts", user, _ => false));
        Assert.Contains("Manage the chart of accounts", NavCatalog.Explain("accounting/accounts"));

        // Admin › Roles explains what each permission opens.
        Assert.Contains(NavCatalog.PagesRevealedBy(Permission.GlJournalsPost), e => e.Href == "accounting/journal");
        Assert.Contains(NavCatalog.PagesRevealedBy(Permission.AdminRoles), e => e.Href == "admin/roles");
    }

    [Fact]
    public async Task Several_roles_add_up_and_taking_one_away_takes_its_permissions()
    {
        await using var seeded = await CreateAsync();
        var vendors = await CreateRoleAsync(seeded, "Vendor desk", Permission.VendorsManage);
        var payments = await CreateRoleAsync(seeded, "Payments", Permission.ApPaymentsManage);
        var both = await AssignAsync(seeded, Viewer, vendors.Id, payments.Id, BuiltInRoles.IdOf(OrgRoles.Timekeeper));

        Assert.Equal(3, both.Roles.Count);
        Assert.Contains(Permission.VendorsManage, both.Permissions);
        Assert.Contains(Permission.ApPaymentsManage, both.Permissions);
        Assert.Contains(Permission.TimeViewAll, both.Permissions); // From the built-in Timekeeper role.
        Assert.Contains(both.Conflicts, c => c.First == Permission.VendorsManage && c.Second == Permission.ApPaymentsManage);

        seeded.As(Viewer);
        Assert.NotNull(await seeded.Host.Get<IVendorService>().ListAsync());
        var me = await seeded.Host.Get<ICurrentUserContext>().GetAsync();
        Assert.Equal(OrgRoles.Timekeeper, me.Roles);
        Assert.True(me.CanAny(Permission.VendorsManage) && me.Can(Permission.ApPaymentsManage) && me.Can(Permission.TimeViewAll));

        await AssignAsync(seeded, Viewer, payments.Id);
        seeded.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IVendorService>().ListAsync());
        var after = await seeded.Host.Get<ICurrentUserContext>().GetAsync();
        Assert.False(after.Can(Permission.TimeViewAll));
        Assert.True(after.Can(Permission.ApPaymentsManage));
    }

    [Fact]
    public async Task Built_in_roles_assigned_the_old_way_and_custom_roles_live_side_by_side()
    {
        await using var seeded = await CreateAsync();
        var vendors = await CreateRoleAsync(seeded, "Vendor desk", Permission.VendorsManage);
        await AssignAsync(seeded, Viewer, vendors.Id);

        // The old API sets built-in roles and keeps custom ones.
        var dto = await seeded.As(Admin).Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Hr);
        Assert.Equal(OrgRoles.Hr, dto.Roles);
        var held = await seeded.Host.Get<IRoleService>().GetUserRolesAsync(Viewer);
        Assert.Equal(["HR", "Vendor desk"], held.Roles.Select(r => r.Name));
    }

    [Fact]
    public async Task Nobody_can_lock_the_organization_out()
    {
        await using var seeded = await CreateAsync();
        var roles = seeded.As(Admin).Host.Get<IRoleService>();
        var admin = BuiltInRoles.Admin.Id;

        // Built-in roles can't be changed or deleted; Admin always holds every permission.
        var all = await roles.ListAsync();
        Assert.Equal(PermissionCatalog.All.Count, all.Single(r => r.Id == admin).Permissions.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => roles.UpdateAsync(admin, new SaveRoleRequest("Admin", null, [Permission.AdminUsers])));
        await Assert.ThrowsAsync<ArgumentException>(() => roles.DeleteAsync(admin));
        await Assert.ThrowsAsync<ArgumentException>(() => roles.DeleteAsync(BuiltInRoles.IdOf(OrgRoles.Finance)));

        // Nobody removes their own Admin role, and the last active administrator keeps it, even against a user
        // administrator holding a custom role.
        var self = await Assert.ThrowsAsync<ArgumentException>(() => roles.SetUserRolesAsync(Admin, []));
        Assert.Contains("own Admin", self.Message);
        var userAdmin = await CreateRoleAsync(seeded, "User admin", Permission.AdminUsers);
        await AssignAsync(seeded, Owner, userAdmin.Id);
        var last = await Assert.ThrowsAsync<ArgumentException>(() => seeded.As(Owner).Host.Get<IRoleService>().SetUserRolesAsync(Admin, []));
        Assert.Contains("at least one active administrator", last.Message);
        var deactivate = await Assert.ThrowsAsync<ArgumentException>(() => seeded.Host.Get<IUserAdminService>().DeactivateAsync(Admin, "test"));
        Assert.Contains("at least one active administrator", deactivate.Message);

        // With a second administrator, the first can lose the role.
        await AssignAsync(seeded, Finance, admin);
        await seeded.As(Owner).Host.Get<IRoleService>().SetUserRolesAsync(Admin, []);
        seeded.As(Admin);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IRoleService>().ListAsync());
    }

    [Fact]
    public async Task Nobody_gives_out_permissions_they_do_not_hold()
    {
        await using var seeded = await CreateAsync();
        var userAdmin = await CreateRoleAsync(seeded, "User admin", Permission.AdminUsers, Permission.AdminRoles, Permission.TimeViewAll);
        var payroll = await CreateRoleAsync(seeded, "Payroll", Permission.PayrollRun);
        await AssignAsync(seeded, Owner, userAdmin.Id);

        seeded.As(Owner);
        var roles = seeded.Host.Get<IRoleService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => roles.CreateAsync(new SaveRoleRequest("Sneaky", null, [Permission.PayrollRun])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => roles.SetUserRolesAsync(Viewer, [payroll.Id]));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => roles.SetUserRolesAsync(Owner, [userAdmin.Id, BuiltInRoles.Admin.Id]));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Finance));

        // Within what they hold, they can; and taking a role away needs no such check.
        var timekeeping = await roles.CreateAsync(new SaveRoleRequest("Timekeeping", null, [Permission.TimeViewAll]));
        Assert.Single((await roles.SetUserRolesAsync(Viewer, [timekeeping.Id])).Roles);
        Assert.Empty((await roles.SetUserRolesAsync(Finance, [])).Roles);
        Assert.Equal(OrgRoles.Employee, (await seeded.Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Employee)).Roles);
    }

    [Fact]
    public async Task Roles_are_validated_and_cloned()
    {
        await using var seeded = await CreateAsync();
        var roles = seeded.As(Admin).Host.Get<IRoleService>();

        await Assert.ThrowsAsync<ArgumentException>(() => roles.CreateAsync(new SaveRoleRequest("", null, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => roles.CreateAsync(new SaveRoleRequest("finance", null, []))); // A built-in name.
        await Assert.ThrowsAsync<ArgumentException>(() => roles.CreateAsync(new SaveRoleRequest("Odd", null, ["no.such.permission"])));

        var copy = await roles.CloneAsync(BuiltInRoles.IdOf(OrgRoles.Finance), "Finance without payroll");
        Assert.False(copy.IsBuiltIn);
        Assert.Equal(BuiltInRoles.For(OrgRoles.Finance).Permissions.Order(), copy.Permissions.Order());
        await Assert.ThrowsAsync<ArgumentException>(() => roles.CloneAsync(copy.Id, "FINANCE WITHOUT PAYROLL"));

        var trimmed = await roles.UpdateAsync(copy.Id, new SaveRoleRequest(copy.Name, "No pay data", copy.Permissions.Where(p => !p.StartsWith("payroll.", StringComparison.Ordinal)).ToList()));
        Assert.DoesNotContain(trimmed.Permissions, p => p.StartsWith("payroll.", StringComparison.Ordinal));
        Assert.Contains(trimmed.Conflicts, c => c.First == Permission.VendorsManage);

        // Members of a role, and API keys can't manage roles.
        await AssignAsync(seeded, Viewer, copy.Id);
        Assert.Equal([Viewer], (await roles.ListMembersAsync(copy.Id)).Select(m => m.Email));
        Assert.Equal(1, (await roles.GetAsync(copy.Id)).UserCount);
    }

    [Fact]
    public async Task Every_change_to_a_role_or_who_holds_it_is_audited()
    {
        await using var seeded = await CreateAsync();
        var roles = seeded.As(Admin).Host.Get<IRoleService>();

        var role = await roles.CreateAsync(new SaveRoleRequest("Approvers", "Approve AP bills", [Permission.ApBillsManage]));
        await roles.UpdateAsync(role.Id, new SaveRoleRequest("AP approvers", "Approve AP bills", [Permission.ApBillsManage, Permission.ApReportsView]));
        var copy = await roles.CloneAsync(role.Id, "AP approvers 2");
        await roles.SetUserRolesAsync(Viewer, [role.Id, BuiltInRoles.IdOf(OrgRoles.Timekeeper)]);
        await roles.DeleteAsync(role.Id); // Also takes it away from the viewer.
        await roles.DeleteAsync(copy.Id);

        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var audit = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == RoleStore.AuditRole || a.EntityType == RoleStore.AuditUserRoles)
            .OrderBy(a => a.OccurredAtUtc).ToListAsync();
        var roleEvents = audit.Where(a => a.EntityType == RoleStore.AuditRole).ToList();
        Assert.Equal(["Created", "Updated", "Created", "Deleted", "Deleted"], roleEvents.Select(a => a.Action));
        var update = roleEvents[1];
        Assert.Contains("\"name\":\"Approvers\"", update.BeforeJson);
        Assert.Contains("\"name\":\"AP approvers\"", update.AfterJson);
        Assert.Contains(Permission.ApReportsView, update.AfterJson);
        Assert.DoesNotContain(Permission.ApReportsView, update.BeforeJson);
        Assert.All(roleEvents, a => Assert.Equal(Admin, a.Actor));

        var viewer = await db.Set<Domain.Identity.AppUser>().AsNoTracking().SingleAsync(u => u.Email == Viewer);
        var assignments = audit.Where(a => a.EntityType == RoleStore.AuditUserRoles && a.EntityId == viewer.Id).ToList();
        Assert.Equal(2, assignments.Count);
        Assert.Contains("AP approvers", assignments[0].AfterJson);
        Assert.Contains("Timekeeper", assignments[0].AfterJson);
        Assert.Contains("AP approvers", assignments[1].BeforeJson);
        Assert.DoesNotContain("AP approvers", assignments[1].AfterJson);

        var events = await db.Set<SecurityEvent>().AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count(e => e.EventType == SecurityEventTypes.RoleCreated));
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.RoleChanged && e.Detail!.Contains("renamed from Approvers") && e.Detail.Contains("added See AP reports"));
        Assert.Equal(2, events.Count(e => e.EventType == SecurityEventTypes.RoleDeleted));
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.RolesChanged && e.Target == Viewer && e.Detail == "Employee → Timekeeper, AP approvers");
    }

    [Fact]
    public async Task The_access_review_export_shows_roles_permissions_and_role_changes()
    {
        await using var seeded = await CreateAsync();
        var role = await CreateRoleAsync(seeded, "Vendor desk", Permission.VendorsManage);
        await AssignAsync(seeded, Viewer, role.Id);

        var review = await seeded.As(Admin).Host.Get<IAccessReviewService>().StartAsync("2026 Q4");
        var item = review.Items.Single(i => i.UserEmail == Viewer);
        Assert.Equal("Vendor desk", item.OrgRoles);
        Assert.Equal(Permission.VendorsManage, item.Permissions);
        Assert.Contains("Employee → Vendor desk", item.RoleChanges);
        Assert.Contains(review.RoleChanges!, c => c.Contains("Created role \"Vendor desk\""));
        Assert.Equal("All workspaces (Admin)", review.Items.Single(i => i.UserEmail == Admin).WorkspaceRoles);
    }

    [Fact]
    public async Task A_role_in_use_by_an_api_key_or_invite_cannot_be_deleted()
    {
        await using var seeded = await CreateAsync();
        var role = await CreateRoleAsync(seeded, "Report reader", Permission.GlReportsView);
        await seeded.As(Admin).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("new.person@example.com", "New Person", OrgRoles.Employee, [role.Id]));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => seeded.Host.Get<IRoleService>().DeleteAsync(role.Id));
        Assert.Contains("pending invite", refused.Message);
        var invite = Assert.Single(await seeded.Host.Get<IInviteService>().ListAsync());
        Assert.Equal(["Report reader"], invite.CustomRoles!.Select(r => r.Name));
    }
}
