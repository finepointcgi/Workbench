using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Projects;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.ProjectAccountingFixture;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Projects;

/// <summary>
/// The Projects hub over the real registrations (<see cref="ProjectAccountingFixture"/>: pm@ manages 1001 and isn't Finance;
/// analyst@ charges to 1001, viewer@ and outsider@ to 2002; reviewer@ has no employee record).
/// </summary>
public sealed class ProjectHubServiceTests
{
    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    public async Task Finance_and_admin_see_every_project_with_money(string email)
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        var list = await seeded.As(email).Host.Get<IProjectHubService>().ListProjectsAsync();

        Assert.True(list.SeesAll);
        Assert.Equal(["1001", "2002"], list.Projects.Select(p => p.Code));
        Assert.All(list.Projects, p => Assert.NotNull(p.Money));
    }

    [Fact]
    public async Task A_project_manager_who_isnt_finance_sees_the_projects_they_manage_with_money()
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        var list = await seeded.As(Owner).Host.Get<IProjectHubService>().ListProjectsAsync();

        Assert.False(list.SeesAll);
        var row = Assert.Single(list.Projects);
        Assert.Equal("1001", row.Code);
        Assert.True(row.IsManager);
        Assert.Equal("Pat Morgan", row.ProjectManagerName);
        Assert.NotNull(row.Money);
        Assert.Equal(400_000m, row.Money!.FundedValue); // contract C1: 400,000 funded of 1,000,000
        Assert.Equal(0.4m, row.Money.PercentFunded);
        Assert.True(list.ShowsMoney);
    }

    [Theory]
    [InlineData(Editor, "1001")]
    [InlineData(Outsider, "2002")]
    public async Task Everyone_else_sees_the_projects_they_charge_to_without_money(string email, string code)
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        var list = await seeded.As(email).Host.Get<IProjectHubService>().ListProjectsAsync();

        var row = Assert.Single(list.Projects);
        Assert.Equal(code, row.Code);
        Assert.True(row.ChargesTo);
        Assert.Null(row.Money);
        Assert.Null(row.OpenItems);
        Assert.False(list.ShowsMoney);
    }

    [Fact]
    public async Task Charge_assignments_and_allocations_count_as_charging()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var wbs2 = db.Set<WbsElement>().Single(w => w.ProjectId == f.Project2);
            db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = wbs2.Id, EmployeeId = f.AnalystId });
            db.Set<Domain.Resourcing.ResourceAllocation>().Add(new Domain.Resourcing.ResourceAllocation { EmployeeId = f.PmId, ProjectId = f.Project2, WeekStart = new DateOnly(2026, 10, 19), Hours = 8, CreatedBy = Finance });
            await db.SaveChangesAsync();
        }

        var analyst = await seeded.As(Editor).Host.Get<IProjectHubService>().ListProjectsAsync();
        var pm = await seeded.As(Owner).Host.Get<IProjectHubService>().ListProjectsAsync();

        Assert.Equal(["1001", "2002"], analyst.Projects.Select(p => p.Code));
        Assert.Equal(["1001", "2002"], pm.Projects.Select(p => p.Code));
        Assert.NotNull(pm.Projects[0].Money); // manages 1001
        Assert.Null(pm.Projects[1].Money); // only allocated to 2002
    }

    [Fact]
    public async Task Members_of_a_projects_plan_workspace_see_it_without_charging_to_it()
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        // reviewer@ has no employee record; viewer@ charges 2002. Both are members of 1001's plan workspace.
        var reviewer = await seeded.As(Commenter).Host.Get<IProjectHubService>().ListProjectsAsync();
        var viewer = await seeded.As(Viewer).Host.Get<IProjectHubService>().ListProjectsAsync();

        var row = Assert.Single(reviewer.Projects);
        Assert.Equal("1001", row.Code);
        Assert.False(row.ChargesTo);
        Assert.Equal([("1001", false), ("2002", true)], viewer.Projects.Select(p => (p.Code, p.ChargesTo)));
        Assert.All(viewer.Projects, p => Assert.Null(p.Money));
    }

    [Fact]
    public async Task Someone_on_no_project_gets_an_empty_list()
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        Assert.Empty((await seeded.As(Timekeeper).Host.Get<IProjectHubService>().ListProjectsAsync()).Projects);
    }

    [Theory]
    [InlineData(Finance, false, true, true)] // Finance isn't a member of the Program Office workspace
    [InlineData(Admin, true, true, true)]
    [InlineData(Owner, true, true, false)] // the PM: money tabs, plan (workspace owner), no setup
    [InlineData(Editor, true, false, false)] // charges to 1001 and edits the plan sheet
    [InlineData(Viewer, true, false, false)] // not charging, but a member of the plan sheet's workspace
    public async Task Project_tabs_follow_the_server_rules(string email, bool plan, bool money, bool setup)
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        var hub = await seeded.As(email).Host.Get<IProjectHubService>().GetProjectAsync(f.Project1);

        var tabs = hub.Tabs;
        Assert.Equal(plan, tabs.Plan);
        Assert.All(new[] { tabs.Financials, tabs.Budgets, tabs.Revenue, tabs.EarnedValue, tabs.Ceilings, tabs.Team }, t => Assert.Equal(money, t));
        Assert.True(tabs.Costs);
        Assert.Equal(setup, tabs.Setup);
        Assert.Equal(money, hub.Project.Money is not null);
        Assert.Equal(plan, hub.Schedule is not null);
    }

    [Fact]
    public async Task A_project_without_a_plan_sheet_has_no_plan_tab()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        var hub = await seeded.As(Viewer).Host.Get<IProjectHubService>().GetProjectAsync(f.Project2);

        Assert.False(hub.Tabs.Plan);
        Assert.False(hub.Tabs.Financials);
        Assert.Null(hub.Schedule);
    }

    [Theory]
    [InlineData(Outsider)] // charges to 2002 only, not a sheet member
    [InlineData(Timekeeper)] // no employee record, not a sheet member
    public async Task People_not_on_a_project_get_not_found(string email)
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(email).Host.Get<IProjectHubService>().GetProjectAsync(f.Project1));
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Owner)]
    public async Task Finance_and_the_pm_see_the_team(string email)
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        var team = await seeded.As(email).Host.Get<IProjectHubService>().GetTeamAsync(f.Project1);

        Assert.Equal(["Pat Morgan", "Alex Rivera"], team.Select(m => m.Name));
        Assert.True(team[0].IsManager);
        Assert.Equal(8m, team[1].ApprovedHours);
        Assert.Equal(Day, team[1].LastCharged);
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(Viewer)]
    public async Task Nobody_else_sees_the_team(string email)
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(email).Host.Get<IProjectHubService>().GetTeamAsync(f.Project1));
    }

    [Fact]
    public async Task The_schedule_counts_open_late_and_blocked_tasks_and_lists_milestones()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var status = new Column { SheetId = seeded.SheetId, Name = "Status", Type = ColumnType.Dropdown, Options = ["Not started", "Blocked", "Done"] };
            db.Set<Column>().Add(status);
            var key = status.Id.ToString();
            var parent = new Item { SheetId = seeded.SheetId, Title = "Phase 1" };
            db.Set<Item>().AddRange(
                parent,
                new Item { SheetId = seeded.SheetId, ParentId = parent.Id, Title = "Late task", Order = 1, Finish = new DateOnly(2020, 1, 1) },
                new Item { SheetId = seeded.SheetId, ParentId = parent.Id, Title = "Blocked task", Order = 2, Finish = new DateOnly(2099, 1, 1), Values = { [key] = "Blocked" } },
                new Item { SheetId = seeded.SheetId, ParentId = parent.Id, Title = "Done task", Order = 3, Finish = new DateOnly(2020, 1, 1), Values = { [key] = "Done" } },
                new Item { SheetId = seeded.SheetId, ParentId = parent.Id, Title = "Design review", Order = 4, Start = new DateOnly(2099, 2, 1), Finish = new DateOnly(2099, 2, 1), IsMilestone = true });
            await db.SaveChangesAsync();
        }

        var hub = await seeded.As(Owner).Host.Get<IProjectHubService>().GetProjectAsync(f.Project1);
        var list = await seeded.As(Owner).Host.Get<IProjectHubService>().ListProjectsAsync();

        var s = Assert.IsType<ProjectScheduleDto>(hub.Schedule);
        Assert.True(s.HasStatusColumn);
        Assert.Equal(5, s.TotalTasks); // "Kickoff" (seeded, no dates) plus four leaves; "Phase 1" is a summary
        Assert.Equal(1, s.DoneTasks);
        Assert.Equal(4, s.OpenTasks);
        Assert.Equal(1, s.LateTasks);
        Assert.Equal(1, s.BlockedTasks);
        Assert.Equal("Design review", Assert.Single(s.UpcomingMilestones).Title);
        Assert.Contains(hub.Alerts, a => a.Kind == "schedule" && a.Tab == "plan");
        Assert.Equal(4, list.Projects.Single().OpenItems);
    }

    [Fact]
    public async Task Funding_used_up_shows_as_an_alert_and_makes_the_project_over()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<Contract>().Single(c => c.Id == f.Contract1).FundedValue = 500m; // analyst@'s 8 h × 87.50 alone is 700
            await db.SaveChangesAsync();
        }

        var hub = await seeded.As(Owner).Host.Get<IProjectHubService>().GetProjectAsync(f.Project1);

        Assert.Contains(hub.Alerts, a => a.Kind == "funding");
        Assert.True(hub.Project.Money!.PercentFundingUsed >= 1m);
        Assert.Equal(ProjectHealth.Over, hub.Project.Money.Health);
    }
}
