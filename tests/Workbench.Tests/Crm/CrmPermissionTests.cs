using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Domain.Security;
using Workbench.Ui.Navigation;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Crm;

/// <summary>
/// Pipeline values are business-sensitive: BD, Finance and administrators see the pipeline; capture and proposal managers see
/// and edit their own; administrators edit all; BD creates; Finance hands off; everyone else sees nothing.
/// </summary>
public sealed class CrmPermissionTests
{
    [Fact]
    public async Task Employees_without_a_role_see_nothing_and_are_logged_as_denied()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();
        kit.As(Viewer);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Opportunities.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Opportunities.GetAsync(o.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Pipeline.GetBoardAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Pipeline.GetForecastAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Pipeline.GetWinLossAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Accounts.ListAccountsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Leads.ListLeadsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Gates.ListGatesAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Opportunities.CreateAsync(new SaveOpportunityRequest("Mine")));

        // The menu's "do I manage any?" question is never a denial.
        Assert.Empty(await kit.Opportunities.ListAsync(new OpportunityFilter(MineOnly: true)));

        await using var db = await kit.Factory.CreateAsync();
        Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.Actor == Viewer && e.EventType == Contracts.Security.SecurityEventTypes.PermissionDenied));
    }

    [Fact]
    public async Task Business_development_and_finance_see_the_whole_pipeline_but_edit_only_what_they_manage()
    {
        await using var kit = await CrmKit.CreateAsync();
        var mine = await kit.NewAsync("Mine");
        var theirs = await kit.NewAsync("Theirs", capture: CrmKit.Bd2);

        foreach (var who in new[] { CrmKit.Bd, CrmKit.Bd2, Finance, Admin })
        {
            Assert.Equal(2, (await kit.As(who).Opportunities.ListAsync()).Count);
            Assert.Equal(2, (await kit.Pipeline.GetBoardAsync()).Lanes.Sum(l => l.Cards.Count));
            await kit.Pipeline.GetForecastAsync();
        }

        var asBd = await kit.As(CrmKit.Bd).Opportunities.GetAsync(theirs.Id);
        Assert.False(asBd.CanEdit);
        Assert.True((await kit.Opportunities.GetAsync(mine.Id)).CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Opportunities.UpdateAsync(theirs.Id, new SaveOpportunityRequest("Taken over")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Opportunities.ChangeStageAsync(theirs.Id, new ChangeStageRequest(OpportunityStage.Identified)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Opportunities.UpdateAsync(mine.Id, new SaveOpportunityRequest("Finance edit")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Opportunities.CreateAsync(new SaveOpportunityRequest("New")));

        Assert.Equal("Admin edit", (await kit.As(Admin).Opportunities.UpdateAsync(theirs.Id, new SaveOpportunityRequest("Admin edit", CaptureManagerEmail: CrmKit.Bd2))).Name);
    }

    [Fact]
    public async Task A_capture_or_proposal_manager_without_a_role_sees_and_edits_only_their_opportunity()
    {
        await using var kit = await CrmKit.CreateAsync();
        var managed = await kit.NewAsync("Managed by Val", proposal: Viewer);
        var other = await kit.NewAsync("Not Val's");
        kit.As(Viewer);

        Assert.Equal([managed.Id], (await kit.Opportunities.ListAsync()).Select(o => o.Id));
        Assert.Equal([managed.Id], (await kit.Pipeline.GetBoardAsync()).Lanes.SelectMany(l => l.Cards).Select(c => c.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Opportunities.GetAsync(other.Id));
        Assert.True((await kit.Opportunities.GetAsync(managed.Id)).CanEdit);
        var updated = await kit.Opportunities.UpdateAsync(managed.Id, CrmFormatRequest(await kit.Opportunities.GetAsync(managed.Id)) with { Pwin = 65 });
        Assert.Equal(65, updated.Pwin);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Note, "Kickoff with the proposal team", OpportunityId: managed.Id));
        Assert.Single(await kit.Accounts.ListActivitiesAsync(new CrmActivityFilter(OpportunityId: managed.Id)));

        // The forecast and win/loss are for the pipeline roles only; accounts are readable for their work.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Pipeline.GetForecastAsync());
        Assert.NotEmpty(await kit.Accounts.ListAccountsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("New agency", CrmAccountKind.Agency)));
    }

    [Fact]
    public async Task Only_finance_hands_off_and_only_finance_sets_the_bp_element()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(CrmKit.Bd).Opportunities.HandoffAsync(o.Id, new HandoffRequest("C9", "9009", CustomerCode: "X")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Pipeline.SetBpParentAsync(kit.BpParentId));
        Assert.Equal("IND.BP", (await kit.As(Finance).Pipeline.SetBpParentAsync(kit.BpParentId)).BpParentCode);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pipeline.SetBpParentAsync(kit.Fx.Wbs1)); // A direct element.
    }

    [Fact]
    public async Task The_business_development_role_is_assignable_and_named()
    {
        await using var kit = await CrmKit.CreateAsync();
        await kit.NewAsync();
        var dto = await kit.As(Admin).Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.BusinessDevelopment);
        Assert.Equal(OrgRoles.BusinessDevelopment, dto.Roles);
        Assert.Equal(OrgRoles.BusinessDevelopment, (await kit.As(Viewer).Get<ICurrentUserContext>().GetAsync()).Roles);
        Assert.NotEmpty(await kit.Opportunities.ListAsync());

        Assert.Contains(OrgRoles.BusinessDevelopment, OrgRoleChecks.All);
        Assert.True(OrgRoleChecks.Everything.HasFlag(OrgRoles.BusinessDevelopment));
        Assert.Equal("Business development", OrgRoleChecks.Label(OrgRoles.BusinessDevelopment));
        Assert.True(OrgRoles.Admin.IsBusinessDevelopment());
        Assert.False(OrgRoles.Finance.IsBusinessDevelopment());
        Assert.True(OrgRoles.Finance.CanViewPipeline());
        Assert.False(OrgRoles.ProjectManager.CanViewPipeline());
        Assert.True(Application.Approvals.ApprovalEngine.HasRole(OrgRoles.BusinessDevelopment, Contracts.Approvals.ApprovalOrgRole.BusinessDevelopment));
        Assert.Equal(OrgRoles.BusinessDevelopment, BuiltInRoles.FlagsOf([BuiltInRoles.IdOf(OrgRoles.BusinessDevelopment)]));
    }

    [Fact]
    public async Task Invites_and_jit_grant_the_role()
    {
        await using var kit = await CrmKit.CreateAsync();
        var invite = await kit.As(Admin).Get<Contracts.Security.IInviteService>()
            .CreateAsync(new Contracts.Security.CreateInviteRequest("newbd@example.com", "New BD", OrgRoles.BusinessDevelopment));
        Assert.Equal(OrgRoles.BusinessDevelopment, invite.Roles);
    }

    [Fact]
    public void The_business_development_menu_follows_the_role()
    {
        static List<string> Menu(NavAccess access) => NavCatalog.MenuEntries(access).Where(e => e.Section == NavCatalog.Growth).Select(e => e.Href).ToList();

        Assert.Empty(Menu(new NavAccess(new CurrentUserInfo("val@example.com", "Val", OrgRoles.Employee, null))));
        Assert.Empty(Menu(new NavAccess(new CurrentUserInfo("sam@example.com", "Sam", OrgRoles.ProjectManager, null))));
        Assert.Equal(6, Menu(new NavAccess(new CurrentUserInfo("bd@example.com", "Blake", OrgRoles.BusinessDevelopment, null))).Count);
        Assert.Equal(6, Menu(new NavAccess(new CurrentUserInfo("fin@example.com", "Fin", OrgRoles.Finance, null))).Count);
        Assert.Equal(["bd", "bd/opportunities"], Menu(new NavAccess(new CurrentUserInfo("val@example.com", "Val", OrgRoles.Employee, null), ManagesOpportunities: true)));
    }

    private static SaveOpportunityRequest CrmFormatRequest(OpportunityDto o) => Workbench.Ui.Crm.CrmFormat.ToRequest(o);
}
