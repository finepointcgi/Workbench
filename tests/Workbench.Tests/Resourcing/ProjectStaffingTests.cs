using Workbench.Application.Resourcing;
using Workbench.Contracts;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Projects;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// Budget by labor category, then staff people into it: the gap per WBS element × labor category, the qualification and
/// over-budget warnings, setup progress, the PM's to-dos and the project overview alerts.
/// </summary>
public sealed class ProjectStaffingTests
{
    private static readonly Guid WbsA = Guid.NewGuid();
    private static readonly Guid WbsB = Guid.NewGuid();
    private static readonly Guid Senior = Guid.NewGuid();
    private static readonly Guid Analyst2 = Guid.NewGuid();
    private static readonly Guid Admin1 = Guid.NewGuid();
    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid Bo = Guid.NewGuid();

    private static StaffingInputs Inputs(IReadOnlyList<StaffingBudget> budget, IReadOnlyList<StaffingCharge> charged, IReadOnlyList<StaffingAllocation> staffed) =>
        new(budget, charged, staffed,
            new Dictionary<Guid, HashSet<Guid>> { [Ann] = [Senior], [Bo] = [Analyst2] },
            id => id == WbsA ? ("1.1", "Design") : ("1.2", "Build"),
            id => id == Senior ? "SE Senior Engineer" : id == Analyst2 ? "BA2 Analyst" : id == Admin1 ? "ADM Admin" : "No labor category");

    [Fact]
    public void Gap_is_budget_less_charged_less_staffed_per_wbs_element_and_labor_category()
    {
        var week = ThisWeek;
        var inputs = Inputs(
            [new(WbsB, Senior, 1200), new(WbsA, Analyst2, 100)],
            [new(WbsB, Senior, 200)],
            [
                new(Ann, "Ann", WbsB, Senior, week, 300), new(Ann, "Ann", WbsB, Senior, week.AddDays(7), 300), // 600 of the 1,000 left
                new(Bo, "Bo", WbsA, Analyst2, week, 200), // 100 over
                new(Bo, "Bo", WbsA, Admin1, week, 10), // no budget for ADM
            ]);

        var (categories, slots) = StaffingCalculator.Calculate(inputs, 10m);

        var build = slots.Single(s => s.WbsElementId == WbsB && s.LaborCategoryId == Senior);
        Assert.Equal((1200m, 200m, 1000m, 600m, 400m, StaffingGapStatus.Understaffed), (build.BudgetHours, build.ChargedHours, build.LeftHours, build.StaffedHours, build.Gap, build.Status));
        var ann = Assert.Single(build.People);
        Assert.Equal(("Ann", 600m, true, week.AddDays(13)), (ann.Name, ann.Hours, ann.Qualifies, ann.Through));
        Assert.Equal(StaffingGapStatus.Overstaffed, slots.Single(s => s.LaborCategoryId == Analyst2).Status);
        var admin = slots.Single(s => s.LaborCategoryId == Admin1);
        Assert.Equal((StaffingGapStatus.NotBudgeted, false), (admin.Status, admin.People[0].Qualifies));

        Assert.Equal(400m, categories.Single(c => c.LaborCategoryId == Senior).Gap);
        Assert.Equal("1.1", slots[0].WbsCode); // ordered by WBS
    }

    [Fact]
    public void People_staffed_on_the_whole_project_count_toward_their_category_total()
    {
        var inputs = Inputs([new(WbsA, Senior, 100), new(WbsB, Senior, 100)], [], [new(Ann, "Ann", null, Senior, ThisWeek, 150)]);

        var (categories, slots) = StaffingCalculator.Calculate(inputs, 10m);

        var whole = slots.Single(s => s.WbsElementId is null);
        Assert.Equal(("Whole project", StaffingGapStatus.Balanced), (whole.WbsLabel, whole.Status));
        var senior = Assert.Single(categories);
        Assert.Equal((200m, 150m, 50m, StaffingGapStatus.Understaffed), (senior.LeftHours, senior.StaffedHours, senior.Gap, senior.Status));
    }

    [Theory]
    [InlineData(1000, 1000, StaffingGapStatus.Balanced)]
    [InlineData(1000, 950, StaffingGapStatus.Balanced)] // within 10%
    [InlineData(1000, 850, StaffingGapStatus.Understaffed)]
    [InlineData(1000, 1150, StaffingGapStatus.Overstaffed)]
    [InlineData(0, 0, StaffingGapStatus.Balanced)]
    public void Small_gaps_within_the_tolerance_are_balanced(int left, int staffed, StaffingGapStatus expected) =>
        Assert.Equal(expected, StaffingCalculator.StatusOf(left, left, staffed, 10m));

    [Fact]
    public void A_person_qualifies_for_their_own_category_and_categories_they_are_assigned_to_charge_in()
    {
        var qualifications = new Dictionary<Guid, HashSet<Guid>> { [Ann] = [Senior, Analyst2] };
        Assert.True(StaffingCalculator.Qualifies(qualifications, Ann, Analyst2));
        Assert.False(StaffingCalculator.Qualifies(qualifications, Ann, Admin1));
        Assert.False(StaffingCalculator.Qualifies(qualifications, Bo, Senior));
        Assert.False(StaffingCalculator.Qualifies(qualifications, Ann, null));
    }

    private static async Task BudgetAsync(ResourcingHost host, params (Guid Wbs, Guid Category, decimal Hours)[] lines)
    {
        await using var db = await host.Db.CreateAsync();
        var current = new BudgetVersion { ProjectId = host.Project1, Name = "Working", Kind = BudgetKind.Current, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow };
        foreach (var (wbs, category, hours) in lines)
        {
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = wbs, CostType = BudgetCostType.Labor, LaborCategoryId = category, Hours = hours, Amount = hours * 100 });
        }

        db.Set<BudgetVersion>().Add(current);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Plan_compares_the_budget_with_people_staffed_from_this_week_on()
    {
        await using var host = await CreateAsync();
        await BudgetAsync(host, (host.Build, host.Se2, 400), (host.Design, host.Ba2, 80));
        await host.AddApprovedTimeAsync(host.DevId, (host.Build, host.Se2, new DateOnly(2026, 10, 6), 40m));
        var allocations = host.As(Pm).Get<IResourceAllocationService>();
        await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 40, WbsElementId: host.Build, Weeks: 5)); // 200 of 360 left
        await allocations.CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project1, ThisWeek, 20, WbsElementId: host.Design, Weeks: 4)); // 80 of 80

        var plan = await host.Get<IProjectStaffingService>().GetPlanAsync(host.Project1);

        var build = plan.Slots.Single(s => s.WbsElementId == host.Build);
        Assert.Equal((400m, 40m, 360m, 200m, 160m, StaffingGapStatus.Understaffed), (build.BudgetHours, build.ChargedHours, build.LeftHours, build.StaffedHours, build.Gap, build.Status));
        Assert.Equal("SE2 Systems Engineer II", build.LaborCategory);
        Assert.Equal(StaffingGapStatus.Balanced, plan.Slots.Single(s => s.WbsElementId == host.Design).Status);
        Assert.Equal((160m, 0m, true), (plan.UnstaffedHours, plan.OverstaffedHours, plan.HasLaborBudget));
        Assert.Empty(plan.Overbooked); // 40 h a week is exactly dev@'s standard week

        var todo = Assert.Single(StaffingTodoProvider.For(plan));
        Assert.Equal("160 budgeted hours on 1001 have nobody staffed", todo.Title);
        Assert.Equal("160 h of SE2 Systems Engineer II on 1001.01.02.", todo.Reason);
        Assert.Equal($"projects/{host.Project1}/staffing", todo.Link);
    }

    [Fact]
    public async Task People_booked_past_their_hours_across_projects_are_flagged()
    {
        await using var host = await CreateAsync();
        await BudgetAsync(host, (host.Build, host.Se2, 400));
        await host.As(Pm).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 30, WbsElementId: host.Build));
        await host.As(Pm2).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project2, ThisWeek, 20));

        var plan = await host.As(Pm).Get<IProjectStaffingService>().GetPlanAsync(host.Project1);

        var dev = Assert.Single(plan.Overbooked);
        Assert.Equal(("Dana Lee", 1, 50m, 40m), (dev.Name, dev.OverWeeks, dev.WorstAllocated, dev.WorstCapacity));
        Assert.Contains(StaffingTodoProvider.For(plan), t => t.Title == "Dana Lee is booked past their hours");
    }

    [Fact]
    public async Task Staffing_someone_outside_their_labor_category_or_past_the_budget_warns()
    {
        await using var host = await CreateAsync();
        await BudgetAsync(host, (host.Build, host.Se2, 100));
        var staffing = host.As(Pm).Get<IProjectStaffingService>();

        // The analyst bills as BA2.
        var wrongCategory = await staffing.CheckAsync(new StaffingCheckRequest(host.Project1, host.AnalystId, host.Build, host.Se2, ThisWeek, 10, 2));
        Assert.False(wrongCategory.Qualifies);
        var warning = Assert.Single(wrongCategory.Warnings);
        Assert.Equal(StaffingWarningKind.NotQualified, warning.Kind);
        Assert.Equal("Alex Rivera bills as BA2 Business Analyst II, not SE2 Systems Engineer II. Staff them in their own category, or check with Finance that they qualify for this one.", warning.Message);

        // No category given: dev@'s own (SE2), which they qualify for; 3 × 40 = 120 is 20 over the 100 left.
        var over = await staffing.CheckAsync(new StaffingCheckRequest(host.Project1, host.DevId, host.Build, null, ThisWeek, 40, 3));
        Assert.Equal((host.Se2, true, 100m, 120m), (over.LaborCategoryId, over.Qualifies, over.LeftHours, over.StaffedHoursAfter));
        Assert.Equal("This staffs 120 SE2 Systems Engineer II hours on 1001.01.02, 20 more than the 100 left in the budget.", Assert.Single(over.Warnings).Message);

        var fits = await staffing.CheckAsync(new StaffingCheckRequest(host.Project1, host.DevId, host.Build, null, ThisWeek, 40, 2));
        Assert.Empty(fits.Warnings);

        var unbudgeted = await staffing.CheckAsync(new StaffingCheckRequest(host.Project1, host.AnalystId, host.Design, null, ThisWeek, 10));
        Assert.Equal(StaffingWarningKind.NotBudgeted, Assert.Single(unbudgeted.Warnings).Kind);
    }

    [Fact]
    public async Task Re_entering_the_same_weeks_replaces_them_so_the_check_does_not_count_them_twice()
    {
        await using var host = await CreateAsync();
        await BudgetAsync(host, (host.Build, host.Se2, 100));
        await host.As(Pm).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 40, WbsElementId: host.Build, LaborCategoryId: host.Se2, Weeks: 2));

        var check = await host.Get<IProjectStaffingService>().CheckAsync(new StaffingCheckRequest(host.Project1, host.DevId, host.Build, host.Se2, ThisWeek, 45, 2));

        Assert.Equal(90m, check.StaffedHoursAfter);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public async Task Candidates_who_qualify_come_first()
    {
        await using var host = await CreateAsync();
        var candidates = await host.As(Pm).Get<IProjectStaffingService>().ListCandidatesAsync(host.Project1, host.Ba2);

        Assert.Equal("Alex Rivera", candidates[0].Name);
        Assert.True(candidates[0].Qualifies);
        Assert.All(candidates.Skip(1), c => Assert.False(c.Qualifies));
        Assert.Equal("SE2 Systems Engineer II", candidates.Single(c => c.Name == "Dana Lee").LaborCategory);
    }

    [Fact]
    public async Task Setup_progress_walks_wbs_then_budget_then_staffing()
    {
        await using var host = await CreateAsync();
        var staffing = host.As(Pm).Get<IProjectStaffingService>();

        var start = await staffing.GetSetupProgressAsync(host.Project1);
        Assert.Equal(["wbs", "budget", "staff"], start.Steps.Select(s => s.Key));
        Assert.Equal((true, false, false), (start.Steps[0].IsDone, start.Steps[1].IsDone, start.Steps[2].IsDone));
        Assert.Equal("Contract C1 · 2 charge codes", start.Steps[0].Status);
        Assert.Equal("budget", start.Next?.Key);

        await BudgetAsync(host, (host.Build, host.Se2, 100));
        var budgeted = await staffing.GetSetupProgressAsync(host.Project1);
        Assert.Equal(("staff", "0 of 100 hours left are staffed; 100 have nobody yet"), (budgeted.Next?.Key, budgeted.Steps[2].Status));

        await host.Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 25, WbsElementId: host.Build, Weeks: 4));
        var done = await staffing.GetSetupProgressAsync(host.Project1);
        Assert.True(done.IsComplete);
        Assert.Equal(3, done.DoneCount);
    }

    [Theory]
    [InlineData(Pm, true)]
    [InlineData(Finance, true)]
    [InlineData(ResourceManager, true)]
    [InlineData(Admin, true)]
    [InlineData(Pm2, false)]
    [InlineData(Analyst, false)]
    public async Task Staffing_is_for_finance_resource_managers_and_the_projects_manager(string email, bool allowed)
    {
        await using var host = await CreateAsync();
        var staffing = host.As(email).Get<IProjectStaffingService>();

        async Task<bool> CanAsync()
        {
            try
            {
                await staffing.GetPlanAsync(host.Project1);
                await staffing.CheckAsync(new StaffingCheckRequest(host.Project1, host.DevId, null, null, ThisWeek, 8));
                return true;
            }
            catch (Exception ex) when (ex is WorkbenchForbiddenException or WorkbenchNotFoundException) // Not found: they aren't on the project.
            {
                return false;
            }
        }

        Assert.Equal(allowed, await CanAsync());
    }

    [Fact]
    public async Task The_project_overview_and_the_pms_to_dos_show_unstaffed_budget()
    {
        await using var host = await CreateAsync();
        await BudgetAsync(host, (host.Build, host.Se2, 400));

        var hub = await host.As(Pm).Get<IProjectHubService>().GetProjectAsync(host.Project1);
        var alert = Assert.Single(hub.Alerts, a => a.Kind == "staffing");
        Assert.Equal(("staffing", "400 budgeted hours on 1001 have nobody staffed. 400 h of SE2 Systems Engineer II on 1001.01.02."), (alert.Tab, alert.Message));

        var todos = await host.Get<IMyTodoService>().GetAsync();
        Assert.Contains(todos.Items, t => t.Title == "400 budgeted hours on 1001 have nobody staffed" && t.LinkLabel == "Staff people");
    }

    [Fact]
    public async Task A_project_without_a_labor_budget_asks_the_pm_to_budget_it_first()
    {
        await using var host = await CreateAsync();
        var plan = await host.As(Pm).Get<IProjectStaffingService>().GetPlanAsync(host.Project1);

        Assert.False(plan.HasLaborBudget);
        Assert.Contains(plan.Notes, n => n.StartsWith("This project has no budget yet.", StringComparison.Ordinal));
        var todo = Assert.Single(StaffingTodoProvider.For(plan));
        Assert.Equal(("Budget 1001 by labor category", $"projects/{host.Project1}/budgets"), (todo.Title, todo.Link));
    }

    [Fact]
    public async Task Several_projects_without_a_budget_make_one_to_do()
    {
        await using var host = await CreateAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            var p2 = await db.Set<Project>().FindAsync(host.Project2);
            p2!.ProjectManagerId = host.PmId;
            await db.SaveChangesAsync();
        }

        var todos = await host.As(Pm).Get<IMyTodoService>().GetAsync();

        var item = Assert.Single(todos.Items, t => t.Key.StartsWith("staffing:", StringComparison.Ordinal));
        Assert.Equal(("Budget 2 projects by labor category", "Start with 1001"), (item.Title, item.LinkLabel));
        Assert.StartsWith("1001 and 2002 have no labor budget yet.", item.Reason, StringComparison.Ordinal);
    }
}
