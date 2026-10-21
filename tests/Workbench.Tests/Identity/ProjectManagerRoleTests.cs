using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// The Project manager org role over the real registrations: pm@ manages 1001 (on contract C1, plan sheet in Program
/// Office); 2002 (contract C2) has no manager. Tests give pm@ the role (or not) to compare it with "manages a project".
/// </summary>
public sealed class ProjectManagerRoleTests
{
    private static async Task<(SeededHost Seeded, ProjectAccountingFixture Fixture)> CreateAsync(bool pmRole = true)
    {
        var seeded = await SeededHost.CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        if (pmRole)
        {
            await GrantAsync(seeded, Owner, OrgRoles.ProjectManager);
        }

        return (seeded, fixture);
    }

    internal static async Task GrantAsync(SeededHost seeded, string email, OrgRoles roles)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new AppUser { Email = email, DisplayName = email };
            db.Set<AppUser>().Add(user);
        }

        // Sets the Project manager and Finance roles, leaving the others.
        const OrgRoles managed = OrgRoles.ProjectManager | OrgRoles.Finance;
        var held = BuiltInRoles.FlagsOf(await RoleStore.RoleIdsOfAsync(db, user.Id, default));
        await RoleStore.ReplaceBuiltInAsync(db, user, (held & ~managed) | (roles & managed), "test", DateTime.UtcNow, default);
        await db.SaveChangesAsync();
    }

    private static SaveProjectRequest Request(string code, Guid? contract = null, Guid? manager = null, ProjectState status = ProjectState.Active) =>
        new(code, $"Project {code}", contract, manager, status, null, null);

    // ---- Role assignment ----

    [Fact]
    public async Task Admins_assign_the_role_and_it_shows_in_the_users_context_and_directory()
    {
        await using var seeded = await SeededHost.CreateAsync();

        var dto = await seeded.As(Admin).Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.ProjectManager);
        var context = await seeded.As(Viewer).Host.Get<ICurrentUserContext>().GetAsync();

        Assert.Equal(OrgRoles.ProjectManager, dto.Roles);
        Assert.Equal(OrgRoles.ProjectManager, context.Roles);
        Assert.True(context.Roles.IsProjectManager());
        Assert.True(context.Roles.CanManageProjects());
        Assert.False(context.Roles.IsFinance());
        Assert.True(OrgRoles.Admin.IsProjectManager());
        Assert.False(OrgRoles.Finance.IsProjectManager());
        Assert.True(OrgRoles.Finance.CanManageProjects());
        Assert.False(OrgRoles.Employee.CanManageProjects());
        Assert.Contains(OrgRoles.ProjectManager, OrgRoleChecks.All);
        Assert.Equal("Project manager", OrgRoleChecks.Label(OrgRoles.ProjectManager));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Finance).Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.ProjectManager));
    }

    [Fact]
    public async Task Invites_and_sign_in_group_mappings_carry_the_role()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var admin = seeded.As(Admin).Host;

        var invite = await admin.Get<IInviteService>().CreateAsync(new CreateInviteRequest("newpm@example.com", "New PM", OrgRoles.ProjectManager));
        var settings = await admin.Get<ISignInSettingsService>().SaveProvisioningAsync(new SaveProvisioningRequest(
            JitProvisioningMode.InviteOnly, null, OrgRoles.ProjectManager, null, true, [new SaveSsoGroupMapping("pm-group", OrgRoles.ProjectManager | OrgRoles.Timekeeper, "PMs")]));

        Assert.Equal(OrgRoles.ProjectManager, invite.Roles);
        Assert.Equal(OrgRoles.ProjectManager | OrgRoles.Timekeeper, Assert.Single(settings.GroupMappings).Roles);
    }

    // ---- Creating projects ----

    [Theory]
    [InlineData(Admin)]
    [InlineData(Finance)]
    [InlineData(Owner)] // the Project manager role
    public async Task Admin_finance_and_project_managers_create_projects(string email)
    {
        var (seeded, _) = await CreateAsync();
        await using var _s = seeded;

        var project = await seeded.As(email).Host.Get<IProjectService>().CreateProjectAsync(Request("3003"));

        Assert.Equal("3003", project.Code);
        Assert.Equal(ProjectState.Active, project.Status);
    }

    [Theory]
    [InlineData(Editor)] // an Employee
    [InlineData(Viewer)]
    public async Task Employees_cant_create_projects(string email)
    {
        var (seeded, _) = await CreateAsync();
        await using var _s = seeded;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(email).Host.Get<IProjectService>().CreateProjectAsync(Request("3003")));
    }

    [Fact]
    public async Task Managing_a_project_without_the_role_doesnt_let_you_create_or_set_up_projects()
    {
        var (seeded, fixture) = await CreateAsync(pmRole: false);
        await using var _s = seeded;
        var projects = seeded.As(Owner).Host.Get<IProjectService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.CreateProjectAsync(Request("3003")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.CreateWbsElementAsync(fixture.Project1, new SaveWbsElementRequest(null, "1001.02", "More")));

        // What managing gave before stays: the project's budgets.
        Assert.NotEmpty(await seeded.Host.Get<IBudgetService>().ListVersionsAsync(fixture.Project1));
    }

    [Fact]
    public async Task A_project_manager_is_the_manager_of_what_they_create_and_cant_name_someone_else()
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;
        var projects = seeded.As(Owner).Host.Get<IProjectService>();

        var created = await projects.CreateProjectAsync(Request("3003"));

        Assert.Equal(fixture.PmId, created.ProjectManagerId);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.CreateProjectAsync(Request("3004", manager: fixture.AnalystId)));
    }

    [Fact]
    public async Task A_project_on_a_contract_is_a_request_finance_activates()
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;

        var requested = await seeded.As(Owner).Host.Get<IProjectService>().CreateProjectAsync(Request("1003", fixture.Contract1));

        Assert.Equal(ProjectState.Planning, requested.Status);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var note = Assert.Single(await db.Set<Notification>().Where(n => n.Kind == PermissionCheckingProjectService.ProjectRequestKind).ToListAsync());
            Assert.Equal(Finance, note.RecipientEmail);
            Assert.Contains("1003", note.Title);
        }

        var projects = seeded.Host.Get<IProjectService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.UpdateProjectAsync(requested.Id, Request("1003", fixture.Contract1, fixture.PmId, ProjectState.Active)));

        var activated = await seeded.As(Finance).Host.Get<IProjectService>().UpdateProjectAsync(requested.Id, Request("1003", fixture.Contract1, fixture.PmId, ProjectState.Active));
        Assert.Equal(ProjectState.Active, activated.Status);
    }

    // ---- Setting up the projects they manage ----

    [Fact]
    public async Task A_project_manager_sets_up_the_wbs_charge_assignments_and_details_of_their_project()
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;
        var projects = seeded.As(Owner).Host.Get<IProjectService>();

        var element = await projects.CreateWbsElementAsync(fixture.Project1, new SaveWbsElementRequest(null, "1001.02", "Phase 2"));
        await projects.UpdateWbsElementAsync(element.Id, new SaveWbsElementRequest(null, "1001.02", "Phase two"));
        var assignment = await projects.AddAssignmentAsync(element.Id, new AddChargeAssignmentRequest(fixture.AnalystId));
        await projects.RemoveAssignmentAsync(assignment.Id);
        var renamed = await projects.UpdateProjectAsync(fixture.Project1, Request("1001", fixture.Contract1, fixture.PmId) with { Name = "Modernization II" });

        Assert.Equal("Modernization II", renamed.Name);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.UpdateProjectAsync(fixture.Project1, Request("1001", fixture.Contract2, fixture.PmId)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.UpdateProjectAsync(fixture.Project1, Request("1001", fixture.Contract1, fixture.AnalystId)));
    }

    [Fact]
    public async Task A_project_manager_cant_touch_projects_they_dont_manage()
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;
        var host = seeded.As(Owner).Host;
        var projects = host.Get<IProjectService>();
        Guid otherWbs;
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            otherWbs = await db.Set<WbsElement>().Where(w => w.ProjectId == fixture.Project2).Select(w => w.Id).FirstAsync();
        }

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.CreateWbsElementAsync(fixture.Project2, new SaveWbsElementRequest(null, "2002.02", "Mine now")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.UpdateWbsElementAsync(otherWbs, new SaveWbsElementRequest(null, "2002.01", "Renamed")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.AddAssignmentAsync(otherWbs, new AddChargeAssignmentRequest(fixture.AnalystId)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.UpdateProjectAsync(fixture.Project2, Request("2002", fixture.Contract2)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IBudgetService>().ListVersionsAsync(fixture.Project2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(fixture.Project2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPlanSyncService>().CreatePlanSheetAsync(fixture.Project2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IProjectHubService>().GetTeamAsync(fixture.Project2));
        Assert.False(await host.Get<ProjectAccess>().CanSetUpProjectAsync(fixture.Project2));
        Assert.True(await host.Get<ProjectAccess>().CanSetUpProjectAsync(fixture.Project1));
    }

    [Theory]
    [InlineData(Admin, true)]
    [InlineData(Finance, true)]
    [InlineData(Owner, true)] // the role, on their own project
    [InlineData(Editor, false)]
    public async Task Each_role_may_or_may_not_set_up_1001(string email, bool allowed)
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;
        var projects = seeded.As(email).Host.Get<IProjectService>();

        var create = () => projects.CreateWbsElementAsync(fixture.Project1, new SaveWbsElementRequest(null, $"1001.9{email.Length}", "Extra"));

        if (allowed)
        {
            Assert.NotNull(await create());
        }
        else
        {
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(create);
        }
    }

    [Fact]
    public async Task A_project_manager_edits_draft_budgets_and_sees_financials_of_their_project()
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;
        var host = seeded.As(Owner).Host;

        var draft = await host.Get<IBudgetService>().CreateVersionAsync(fixture.Project1, new CreateBudgetVersionRequest("EAC 1", BudgetVersionKind.Forecast));
        var summary = await host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(fixture.Project1);

        Assert.Equal("EAC 1", draft.Version.Name);
        Assert.Equal(fixture.Project1, summary.ProjectId);
    }

    [Fact]
    public async Task The_project_hub_offers_setup_to_its_manager_only_with_the_role()
    {
        var (seeded, fixture) = await CreateAsync(pmRole: false);
        await using var _s = seeded;
        var hub = seeded.As(Owner).Host.Get<IProjectHubService>();

        Assert.False((await hub.GetProjectAsync(fixture.Project1)).Tabs.Setup);
        await GrantAsync(seeded, Owner, OrgRoles.ProjectManager);
        Assert.True((await hub.GetProjectAsync(fixture.Project1)).Tabs.Setup);
    }

    // ---- Workspace ownership ----

    [Fact]
    public async Task A_project_managers_new_plan_sheet_goes_in_a_workspace_of_its_own_that_they_own()
    {
        var (seeded, _) = await CreateAsync();
        await using var _s = seeded;
        var host = seeded.As(Owner).Host;
        var project = await host.Get<IProjectService>().CreateProjectAsync(Request("3003"));
        await host.Get<IProjectService>().CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "3003.01", "Kickoff"));

        var withPlan = await host.Get<IPlanSyncService>().CreatePlanSheetAsync(project.Id);

        var workspace = await host.Get<IWorkspaceMembershipService>().GetForSheetAsync(withPlan.SheetId!.Value);
        Assert.Equal("3003 Project 3003", workspace.Name);
        Assert.Equal(WorkspaceRole.Owner, workspace.MyRole);
        Assert.Single(await host.Items.ListAsync(withPlan.SheetId.Value));
    }

    [Fact]
    public async Task Becoming_a_projects_manager_or_getting_the_role_makes_you_owner_of_its_workspace()
    {
        var (seeded, fixture) = await CreateAsync();
        await using var _s = seeded;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var p2 = await db.Set<Project>().FirstAsync(p => p.Id == fixture.Project2);
            p2.SheetId = seeded.FinanceSheetId; // the "Finance" workspace, which nobody belongs to
            await db.SaveChangesAsync();
        }

        await GrantAsync(seeded, Editor, OrgRoles.ProjectManager);
        await seeded.As(Finance).Host.Get<IProjectService>().UpdateProjectAsync(fixture.Project2, Request("2002", fixture.Contract2, fixture.AnalystId));
        Assert.Equal(WorkspaceRole.Owner, await seeded.As(Editor).Host.Get<AccessControl>().GetSheetRoleAsync(seeded.FinanceSheetId));

        // The viewer manages it next, and gets the role afterwards.
        await seeded.As(Finance).Host.Get<IProjectService>().UpdateProjectAsync(fixture.Project2, Request("2002", fixture.Contract2, fixture.ViewerId));
        Assert.Null(await seeded.As(Viewer).Host.Get<AccessControl>().GetSheetRoleAsync(seeded.FinanceSheetId));
        await seeded.As(Admin).Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.ProjectManager);
        Assert.Equal(WorkspaceRole.Owner, await seeded.As(Viewer).Host.Get<AccessControl>().GetSheetRoleAsync(seeded.FinanceSheetId));
    }

    // ---- Approvals ----

    [Fact]
    public async Task Project_managers_are_an_approval_org_role()
    {
        var (seeded, _) = await CreateAsync();
        await using var _s = seeded;
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();

        var members = await Workbench.Application.Approvals.ApprovalEngine.RoleMembersAsync(db, Contracts.Approvals.ApprovalOrgRole.ProjectManager, CancellationToken.None);

        Assert.Equal([Admin, Owner], members.Order());
        Assert.True(Workbench.Application.Approvals.ApprovalEngine.HasRole(OrgRoles.ProjectManager, Contracts.Approvals.ApprovalOrgRole.ProjectManager));
        Assert.False(Workbench.Application.Approvals.ApprovalEngine.HasRole(OrgRoles.Finance, Contracts.Approvals.ApprovalOrgRole.ProjectManager));
    }
}
