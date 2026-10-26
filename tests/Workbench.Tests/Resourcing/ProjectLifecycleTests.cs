using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Revenue;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// A project's life, in order: Finance starts it (Planning) and gives it funding and labor categories, assigns the PM, the PM
/// plans with those categories and submits, Finance signs off, and the project is Active (time can be charged).
/// Today is Wed Oct 14 2026; contract C1 is $1,000,000 ceiling, $500,000 funded. Dana is an SE2, Alex a BA2.
/// </summary>
public sealed class ProjectLifecycleTests
{
    private const string Requester = "requester@example.com";

    /// <summary>Finance and a project manager who requests projects each get a workspace to hold the plans they start.</summary>
    private static async Task<ResourcingHost> HostAsync()
    {
        var host = await CreateAsync();
        await using var db = await host.Db.CreateAsync();
        await RoleStore.AddUserAsync(db, new AppUser { Email = Requester, DisplayName = "Rae Request" }, OrgRoles.ProjectManager);
        db.Set<Employee>().Add(new Employee
        {
            EmployeeNumber = "E9", Email = Requester, DisplayName = "Rae Request", HireDate = new DateOnly(2020, 1, 1),
            OrgUnitId = await db.Set<OrgUnit>().Select(o => o.Id).FirstAsync(),
        });
        foreach (var (name, email) in new[] { ("Finance", Finance), ("Requests", Requester) })
        {
            var workspace = new Workspace { Name = name };
            db.Workspaces.Add(workspace);
            db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = email, Role = WorkspaceRole.Owner });
        }

        await db.SaveChangesAsync();
        return host;
    }

    private static async Task<Guid> ContractAsync(ResourcingHost host)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Set<Contract>().Select(c => c.Id).SingleAsync();
    }

    private static async Task<ProjectDto> StartAsync(ResourcingHost host, string email, bool setManager = true)
    {
        var contract = await ContractAsync(host);
        return await host.As(email).Get<IProjectStartService>().StartAsync(new StartProjectRequest("1002", "Data platform", contract, ProjectStartFrom.Template,
            TemplateKey: "phases", Start: new DateOnly(2026, 10, 19), ProjectManagerId: setManager ? host.PmId : null));
    }

    private static async Task<Guid> TaskAsync(ResourcingHost host, ProjectDto project, string title)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Items.Where(i => i.SheetId == project.SheetId && i.Title == title).Select(i => i.Id).SingleAsync();
    }

    [Fact]
    public async Task A_new_project_is_planning_and_finance_sets_it_up_before_the_pm_can_submit_then_approval_makes_it_active()
    {
        await using var host = await HostAsync();
        var project = await StartAsync(host, Finance);
        IProjectPlanService PmPlans() => host.As(Pm).Get<IProjectPlanService>();
        IProjectPlanService FinancePlans() => host.As(Finance).Get<IProjectPlanService>();
        var kickoff = await TaskAsync(host, project, "Kickoff");

        Assert.Equal(ProjectState.Planning, project.Status);
        var plan = await FinancePlans().GetAsync(project.Id);
        Assert.Equal(["funding", "manager"], plan.Steps!.Where(s => s.Done).Select(s => s.Key));
        Assert.Equal("categories", plan.Steps!.Single(s => s.Current).Key);
        Assert.False(plan.SetupDone);

        // The PM can plan, but not submit, until Finance has added the labor categories.
        await host.As(Pm).Get<IResourcePlanService>().ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, ItemId: kickoff, EmployeeId: host.DevId, Hours: 8m)]);
        var early = await Assert.ThrowsAsync<ArgumentException>(() => PmPlans().SubmitAsync(project.Id));
        Assert.Contains("labor categories", early.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<Exception>(() => PmPlans().SaveCategoriesAsync(project.Id, [host.Se2])); // the PM doesn't choose them

        var set = await FinancePlans().SaveCategoriesAsync(project.Id, [host.Se2]);
        Assert.True(set.SetupDone);
        Assert.Equal(["SE2"], set.Categories!.Where(c => c.Selected).Select(c => c.Code));
        Assert.Equal("plan", set.Steps!.Single(s => s.Current).Key);

        var submitted = await PmPlans().SubmitAsync(project.Id, "Ready");
        Assert.Equal(PlanApprovalStatus.Submitted, submitted.Status);

        var approved = await FinancePlans().ApproveAsync(project.Id);
        Assert.Equal((ProjectState.Active, 1), (approved.ProjectStatus, approved.Approvals));
        Assert.All(approved.Steps!, s => Assert.True(s.Done));
        Assert.Equal(ProjectState.Active, (await host.Get<IProjectService>().GetProjectAsync(project.Id))!.Status);
    }

    [Fact]
    public async Task Staffing_uses_the_projects_labor_categories_but_a_project_with_none_takes_any()
    {
        await using var host = await HostAsync();
        var project = await StartAsync(host, Finance);
        var kickoff = await TaskAsync(host, project, "Kickoff");
        IResourcePlanService Staffing() => host.As(Pm).Get<IResourcePlanService>();
        IProjectPlanService FinancePlans() => host.As(Finance).Get<IProjectPlanService>();

        // No list: anyone, in any category.
        await Staffing().ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, ItemId: kickoff, EmployeeId: host.DevId, Hours: 8m)]);

        // Finance picks BA2 only: Alex (a BA2) goes on; Dana (an SE2) can't be added in another category.
        await FinancePlans().SaveCategoriesAsync(project.Id, [host.Ba2]);
        await Staffing().ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, ItemId: kickoff, EmployeeId: host.AnalystId, Hours: 8m)]);
        var other = await TaskAsync(host, project, "Final report");
        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            Staffing().ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, ItemId: other, EmployeeId: host.Pm2Id, Hours: 8m)]));
        Assert.Contains("isn't one of 1002's labor categories", refused.Message, StringComparison.Ordinal);
        Assert.Contains("BA2", refused.Message, StringComparison.Ordinal);

        // Dana was already on, so her hours can still change.
        var dana = (await Staffing().GetAsync(project.Id)).Task(kickoff)!.People.Single(p => p.EmployeeId == host.DevId);
        await Staffing().ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: dana.AssignmentId, Hours: 10m)]);

        // Emptying the list lifts the limit again.
        await FinancePlans().SaveCategoriesAsync(project.Id, []);
        await Staffing().ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, ItemId: other, EmployeeId: host.Pm2Id, Hours: 8m)]);
    }

    [Fact]
    public async Task A_project_started_from_a_contract_earns_revenue_the_way_its_contract_is_priced()
    {
        await using var host = await HostAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            (await db.Set<Contract>().SingleAsync()).Type = ContractType.TimeAndMaterials;
            await db.SaveChangesAsync();
        }

        var project = await StartAsync(host, Finance);

        var setup = Assert.Single((await host.As(Finance).Get<IRevenueSetupService>().GetProjectAsync(project.Id)).Setups);
        Assert.Equal(RevenueMethodKind.TimeAndMaterialsIncurred, setup.Method);
    }

    [Fact]
    public async Task A_project_a_pm_requests_waits_for_finance_and_an_internal_project_is_active_at_once()
    {
        await using var host = await HostAsync();
        var requested = await StartAsync(host, Requester, setManager: false);
        Assert.Equal(ProjectState.Planning, requested.Status);
        var plan = await host.As(Finance).Get<IProjectPlanService>().GetAsync(requested.Id);
        Assert.False(plan.SetupDone);

        // A project that was already running isn't held up by it.
        Assert.True((await host.As(Finance).Get<IProjectPlanService>().GetAsync(host.Project1)).SetupDone);

        var internalWork = await host.As(Finance).Get<IProjectStartService>().StartAsync(new StartProjectRequest("9001", "Training", Start: new DateOnly(2026, 10, 19)));
        Assert.Equal(ProjectState.Active, internalWork.Status);
    }
}
