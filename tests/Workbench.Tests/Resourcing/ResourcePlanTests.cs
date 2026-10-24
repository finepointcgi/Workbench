using Microsoft.EntityFrameworkCore;
using Workbench.Application.IndirectRates;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using Workbench.Domain;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// The plan with its people (the Plan tab's people, hours and cost): one store with Budget &amp; staffing, cost at the dated
/// cost rate plus burden for those who may see it, overbooking flags and their one-click fixes, and "try changes" that
/// saves nothing until applied, then applies all at once with an undo. Today is Wed Oct 14 2026; Design runs Oct 12–23 and
/// Build Oct 19–30 under Phase 1. Dana costs $60/h until Nov 2, then $70.
/// </summary>
public sealed class ResourcePlanTests
{
    private static readonly DateOnly Oct19 = new(2026, 10, 19);
    private static readonly DateOnly Oct26 = new(2026, 10, 26);
    private static readonly DateOnly Oct30 = new(2026, 10, 30);
    private static readonly DateOnly Nov6 = new(2026, 11, 6);

    [Fact]
    public async Task People_added_on_the_plan_are_the_staffing_tabs_assignments_and_the_other_way_round()
    {
        await using var host = await CreateAsync();
        var (build, design) = (await ItemAsync(host, "Build"), await ItemAsync(host, "Design"));
        var plans = host.As(Pm).Get<IResourcePlanService>();

        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);

        // The Budget & staffing tab reads the same assignment, on the task's dates with Dana's own labor category.
        var staffing = host.Get<IStaffingAssignmentService>();
        var saved = Assert.Single((await staffing.ListAsync(host.Project1)).Assignments);
        Assert.Equal((host.DevId, (Guid?)host.Build, Oct19, Oct30, 80m, (Guid?)host.Se2), (saved.EmployeeId, saved.WbsElementId, saved.From, saved.To, saved.TotalHours, saved.LaborCategoryId));

        // Assigning on the staffing tab shows on the plan; so do hours changed there.
        await staffing.SaveAsync(new SaveAssignmentRequest(host.AnalystId, host.Project1, 40m, host.Design));
        await staffing.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 60m, host.Build, Oct19, Oct30, Id: saved.Id));
        var plan = await plans.GetAsync(host.Project1);
        var buildRow = plan.Task(build)!;
        var dana = Assert.Single(buildRow.People);
        Assert.Equal((saved.Id, "Dana Lee", "SE2 Systems Engineer II", 60m, 3600m), (dana.AssignmentId, dana.Name, dana.LaborCategory, dana.Hours, dana.DirectCost));
        var alex = Assert.Single(plan.Task(design)!.People);
        Assert.Equal(("Alex Rivera", 40m, 2000m), (alex.Name, alex.Hours, alex.DirectCost));

        // The summary adds up its subtasks.
        var phase = plan.Tasks.Single(t => t.Title == "Phase 1");
        Assert.Equal((true, 100m, 5600m), (phase.IsSummary, phase.Hours, phase.DirectCost));

        // Changing hours on the plan changes the staffing tab's assignment; taking them off removes it.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: saved.Id, Hours: 24m)]);
        Assert.Equal(24m, (await staffing.ListAsync(host.Project1)).Assignments.Single(a => a.Id == saved.Id).TotalHours);
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.RemovePerson, AssignmentId: saved.Id)]);
        Assert.DoesNotContain((await staffing.ListAsync(host.Project1)).Assignments, a => a.Id == saved.Id);
        await using var db = await host.Db.CreateAsync();
        Assert.False(await db.Set<ResourceAllocation>().AnyAsync(a => a.AssignmentId == saved.Id));
    }

    [Fact]
    public async Task Cost_is_hours_at_the_cost_rate_in_each_week_plus_burden()
    {
        await using var host = await CreateAsync();
        await AddFringeAsync(host, 0.25m);
        var build = await ItemAsync(host, "Build");
        var plans = host.As(Pm).Get<IResourcePlanService>();

        // Oct 26 – Nov 6: 40 h at $60, then 40 h at $70 once Dana's raise starts on Nov 2.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m, From: Oct26, To: Nov6)]);

        var plan = await plans.GetAsync(host.Project1);
        var dana = plan.Task(build)!.People.Single();
        Assert.Equal((5200m, 6500m, 6500m), (dana.DirectCost, dana.BurdenedCost, dana.Cost));
        Assert.True(plan.CostIncludesBurden);
        Assert.Equal((80m, 5200m, 6500m), (plan.Totals.Hours, plan.Totals.DirectCost, plan.Totals.BurdenedCost));
    }

    [Fact]
    public async Task Cost_shows_only_to_those_who_may_see_project_financials()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.As(Pm).Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        await AddBudgetAsync(host, (host.Se2, 100m, 6000m));

        // A resource manager plans people but doesn't see labor cost or the budget's dollars.
        var rm = await host.As(ResourceManager).Get<IResourcePlanService>().GetAsync(host.Project1);
        Assert.False(rm.ShowsCost);
        Assert.Null(rm.Task(build)!.People.Single().Cost);
        Assert.Null(rm.Task(build)!.Cost);
        Assert.Equal((80m, (decimal?)null, (decimal?)null, 100m), (rm.Totals.Hours, rm.Totals.Cost, rm.Totals.BudgetAmount, rm.Totals.BudgetHours));
        Assert.All(rm.Totals.Categories, c => Assert.Null(c.Cost));
        var tried = await host.Get<IResourcePlanService>().TryAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.AnalystId, Hours: 10m)]);
        Assert.Null(tried.After.Cost);
        Assert.DoesNotContain(tried.Effects, e => e.Contains('$', StringComparison.Ordinal));

        // Finance and the project's manager see it.
        Assert.Equal(4800m, (await host.As(Finance).Get<IResourcePlanService>().GetAsync(host.Project1)).Totals.Cost);
        Assert.True((await host.As(Pm).Get<IResourcePlanService>().GetAsync(host.Project1)).ShowsCost);

        // Someone who can't see the project's staffing can't see its people either; another PM can't change them.
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Analyst).Get<IResourcePlanService>().GetAsync(host.Project1));
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Pm2).Get<IResourcePlanService>().ApplyAsync(host.Project1,
            [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.Pm2Id, Hours: 8m)]));
    }

    [Fact]
    public async Task Totals_compare_hours_and_cost_by_labor_category_with_the_budget_or_the_plan_is_the_budget()
    {
        await using var host = await CreateAsync();
        var (build, design) = (await ItemAsync(host, "Build"), await ItemAsync(host, "Design"));
        var plans = host.As(Pm).Get<IResourcePlanService>();
        await plans.ApplyAsync(host.Project1,
        [
            new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m),
            new PlanChange(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.AnalystId, Hours: 20m),
        ]);

        var unbudgeted = (await plans.GetAsync(host.Project1)).Totals;
        Assert.True(unbudgeted.PlanIsTheBudget);
        Assert.Null(unbudgeted.CostGap);
        Assert.Equal([("BA2 Business Analyst II", 20m, 1000m), ("SE2 Systems Engineer II", 80m, 4800m)],
            unbudgeted.Categories.Select(c => (c.LaborCategory, c.Hours, c.Cost!.Value)));
        Assert.Equal((Oct30, (DateOnly?)new DateOnly(2026, 10, 12)), (unbudgeted.Finish!.Value, unbudgeted.Start));
        Assert.NotNull(unbudgeted.CriticalFloatDays);

        await AddBudgetAsync(host, (host.Se2, 100m, 6000m));
        var plan = await plans.GetAsync(host.Project1);
        var se2 = plan.Totals.Categories.Single(c => c.LaborCategoryId == host.Se2);
        Assert.Equal((100m, 6000m, 20m), (se2.BudgetHours, se2.BudgetAmount, se2.HoursGap));
        Assert.Equal((false, 200m), (plan.Totals.PlanIsTheBudget, plan.Totals.CostGap)); // $6,000 budget − $5,800 planned

        // The schedule check's summary links to the full DCMA check.
        Assert.NotNull(plan.Health);
        Assert.Equal(14, plan.Health!.Passed + plan.Health.Failed + plan.Health.NotMeasured);
    }

    [Fact]
    public async Task Someone_booked_past_their_hours_on_another_project_is_flagged_with_three_fixes()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.As(ResourceManager).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project2, Oct19, 32m, WbsElementId: host.Wbs2));
        var plans = host.As(Pm).Get<IResourcePlanService>();
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);

        var plan = await plans.GetAsync(host.Project1);
        var row = plan.Task(build)!;
        Assert.True(row.IsOverbooked);
        var week = Assert.Single(row.People.Single().Overbooked);
        Assert.Equal((Oct19, 40m, 32m, 40m), (week.WeekStart, week.ThisAssignment, week.OtherHours, week.Capacity));
        Assert.Equal(["Dana Lee"], plan.Totals.OverbookedPeople);
        Assert.Equal(1, plan.OverbookedTasks);

        var fixes = await plans.ListFixesAsync(host.Project1, row.People.Single().AssignmentId!.Value);
        Assert.Equal([OverbookingFixKind.SpreadOverMoreWeeks, OverbookingFixKind.MoveAfterOtherWork, OverbookingFixKind.SomeoneElse, OverbookingFixKind.SomeoneElse],
            fixes.Select(f => f.Kind));

        // Spread: 8 h a week fits around the 32 h on 2002, so the 80 h run ten weeks, to Dec 25.
        Assert.Equal(new DateOnly(2026, 12, 25), Assert.Single(fixes[0].Changes).To);
        // Move: Build starts the next week, when Dana is free.
        Assert.Equal((PlanChangeKind.MoveTask, (Guid?)build, (DateOnly?)Oct26), (fixes[1].Changes[0].Kind, fixes[1].Changes[0].ItemId, fixes[1].Changes[0].Start));
        Assert.Contains("finishes Nov 6, 2026 (5 working days later)", fixes[1].Explanation, StringComparison.Ordinal);
        // Someone else in SE2 with room: Pat, then Quinn.
        Assert.Equal(["Give it to Pat Morgan", "Give it to Quinn Park"], fixes.Skip(2).Select(f => f.Label));

        // Someone who isn't overbooked needs no fix.
        var design = await ItemAsync(host, "Design");
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.AnalystId, Hours: 8m)]);
        var alex = (await plans.GetAsync(host.Project1)).Task(design)!.People.Single();
        Assert.Empty(await plans.ListFixesAsync(host.Project1, alex.AssignmentId!.Value));
    }

    [Fact]
    public async Task Each_fix_clears_the_overbooking_and_undo_puts_it_back()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.As(ResourceManager).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project2, Oct19, 32m, WbsElementId: host.Wbs2));
        var plans = host.As(Pm).Get<IResourcePlanService>();
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        var assignmentId = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single().AssignmentId!.Value;
        var fixes = await plans.ListFixesAsync(host.Project1, assignmentId);

        foreach (var fix in fixes.Take(3))
        {
            var applied = await plans.ApplyAsync(host.Project1, fix.Changes);
            var after = await plans.GetAsync(host.Project1);
            Assert.Empty(after.Totals.OverbookedPeople);
            switch (fix.Kind)
            {
                case OverbookingFixKind.SpreadOverMoreWeeks:
                    Assert.Equal(new DateOnly(2026, 12, 25), after.Task(build)!.People.Single().To);
                    break;
                case OverbookingFixKind.MoveAfterOtherWork:
                    Assert.Equal((Oct26, Nov6), (after.Task(build)!.Start!.Value, after.Task(build)!.Finish!.Value));
                    Assert.Equal((Oct26, Nov6), (after.Task(build)!.People.Single().From, after.Task(build)!.People.Single().To)); // Dana moved with the task
                    Assert.Equal(Nov6, after.Totals.Finish);
                    break;
                default:
                    Assert.Equal("Pat Morgan", after.Task(build)!.People.Single().Name);
                    break;
            }

            await plans.ApplyAsync(host.Project1, applied.Undo);
            var undone = await plans.GetAsync(host.Project1);
            var dana = undone.Task(build)!.People.Single();
            Assert.Equal(("Dana Lee", Oct19, Oct30, 80m), (dana.Name, dana.From, dana.To, dana.Hours));
            Assert.Equal((Oct19, Oct30), (undone.Task(build)!.Start!.Value, undone.Task(build)!.Finish!.Value));
            Assert.Equal(["Dana Lee"], undone.Totals.OverbookedPeople);
        }
    }

    [Fact]
    public async Task Trying_changes_saves_nothing_then_applying_makes_them_all_and_undo_puts_everything_back()
    {
        await using var host = await CreateAsync();
        var (build, design) = (await ItemAsync(host, "Build"), await ItemAsync(host, "Design"));
        var plans = host.As(Pm).Get<IResourcePlanService>();
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        var dana = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single();
        await host.Get<IStaffingAssignmentService>().SetWeekAsync(dana.AssignmentId!.Value, Oct26, 30m); // adjusted by hand: 40 + 30
        var before = await SnapshotAsync(host);

        PlanChange[] changes =
        [
            new(PlanChangeKind.MoveTask, ItemId: build, Start: Oct26),
            new(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.AnalystId, Hours: 40m),
            new(PlanChangeKind.SetPerson, AssignmentId: dana.AssignmentId, Hours: 60m),
        ];
        var tried = await plans.TryAsync(host.Project1, changes);

        Assert.True(tried.CanApply);
        Assert.Equal((Oct30, Nov6, 5), (tried.Before.Finish!.Value, tried.After.Finish!.Value, tried.FinishMovesDays!.Value));
        Assert.Contains("The plan finishes Nov 6, 2026 instead of Oct 30, 2026 (5 working days later).", tried.Effects);
        Assert.Contains("Planned hours go from 70 to 100.", tried.Effects);
        Assert.Contains("Labor cost goes from $4,200 to $5,900 (+$1,700).", tried.Effects); // Dana 60 h over Oct 26 – Nov 6 at $60 and $70, Alex 40 h at $50
        var buildRow = tried.Tasks.Single(t => t.ItemId == build);
        Assert.Equal((true, Oct26, Nov6), (buildRow.Changed, buildRow.Start!.Value, buildRow.Finish!.Value));
        Assert.Equal((Oct26, Nov6, true), (buildRow.People.Single().From, buildRow.People.Single().To, buildRow.People.Single().Changed));
        Assert.Equal(before, await SnapshotAsync(host)); // nothing saved

        var applied = await plans.ApplyAsync(host.Project1, changes);
        Assert.Equal((1, 2), (applied.TasksMoved, applied.PeopleChanged));
        Assert.Equal("Assigned 1 person, changed hours or dates for 1 person and moved 1 task.", applied.Summary);
        var after = await plans.GetAsync(host.Project1);
        Assert.Equal((Oct26, Nov6), (after.Task(build)!.Start!.Value, after.Task(build)!.Finish!.Value));
        Assert.Equal(5900m, after.Totals.Cost);
        Assert.NotEqual(before, await SnapshotAsync(host));

        // One step back: the task's dates, Dana's adjusted weeks, and Alex gone.
        await plans.ApplyAsync(host.Project1, applied.Undo);
        Assert.Equal(before, await SnapshotAsync(host));
    }

    [Fact]
    public async Task Changes_apply_all_or_nothing()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        var loose = await AddTaskAsync(host, "Not a charge code");
        var plans = host.As(Pm).Get<IResourcePlanService>();
        var before = await SnapshotAsync(host);
        PlanChange[] changes =
        [
            new(PlanChangeKind.MoveTask, ItemId: build, Start: Oct26),
            new(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 40m),
            new(PlanChangeKind.SetPerson, ItemId: loose, EmployeeId: host.AnalystId, Hours: 8m),
        ];

        var tried = await plans.TryAsync(host.Project1, changes);
        Assert.False(tried.CanApply);
        Assert.Contains(tried.Errors, e => e.Contains("Not a charge code isn't a charge code yet", StringComparison.Ordinal));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => plans.ApplyAsync(host.Project1, changes));
        Assert.Contains("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(host));
    }

    [Fact]
    public async Task Moving_tasks_needs_edit_rights_on_the_plan()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");

        // A resource manager changes people on any project but isn't an editor of this plan.
        var rm = host.As(ResourceManager).Get<IResourcePlanService>();
        Assert.False((await rm.GetAsync(host.Project1)).CanMoveTasks);
        await Assert.ThrowsAnyAsync<Exception>(() => rm.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.MoveTask, ItemId: build, Start: Oct26)]));
        await rm.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 8m)]);

        var pm = await host.As(Pm).Get<IResourcePlanService>().GetAsync(host.Project1);
        Assert.True(pm.CanChange && pm.CanMoveTasks);
    }

    // ---- Helpers ----

    private static async Task<Guid> ItemAsync(ResourcingHost host, string title)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Items.Where(i => i.SheetId == host.SheetId && i.Title == title).Select(i => i.Id).SingleAsync();
    }

    private static async Task<Guid> AddTaskAsync(ResourcingHost host, string title)
    {
        await using var db = await host.Db.CreateAsync();
        var item = new Item { SheetId = host.SheetId, Title = title, Order = 5, Start = Oct19, Finish = Oct30 };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task AddBudgetAsync(ResourcingHost host, params (Guid Category, decimal Hours, decimal Amount)[] lines)
    {
        await using var db = await host.Db.CreateAsync();
        var version = new BudgetVersion { ProjectId = host.Project1, Name = "Current", Kind = BudgetKind.Current, CreatedBy = "t", CreatedAtUtc = DateTime.UtcNow };
        db.Set<BudgetVersion>().Add(version);
        foreach (var (category, hours, amount) in lines)
        {
            db.Set<BudgetLine>().Add(new BudgetLine { BudgetVersionId = version.Id, WbsElementId = host.Build, CostType = BudgetCostType.Labor, LaborCategoryId = category, Hours = hours, Amount = amount });
        }

        await db.SaveChangesAsync();
    }

    private static async Task AddFringeAsync(ResourcingHost host, decimal rate)
    {
        await using var db = await host.Db.CreateAsync();
        await DefaultPools.EnsureAsync(db, "t", DateTime.UtcNow, CancellationToken.None);
        db.Set<IndirectRate>().Add(new IndirectRate { Pool = IndirectPool.Fringe, FiscalYear = 2027, Basis = IndirectRateBasis.Provisional, Rate = rate, EffectiveFrom = new DateOnly(2026, 10, 1), CreatedBy = "t" });
        await db.SaveChangesAsync();
    }

    /// <summary>Everything the plan and staffing store: task dates, assignments and their weekly rows, as text to compare.</summary>
    private static async Task<string> SnapshotAsync(ResourcingHost host)
    {
        await using var db = await host.Db.CreateAsync();
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == host.SheetId).OrderBy(i => i.Title).Select(i => $"{i.Title} {i.Start} {i.Finish}").ToListAsync();
        var assignments = (await db.Set<StaffingAssignment>().AsNoTracking().ToListAsync())
            .Select(a => $"{a.EmployeeId} {a.WbsElementId} {a.LaborCategoryId} {a.From} {a.To} {a.TotalHours}").Order();
        var rows = (await db.Set<ResourceAllocation>().AsNoTracking().ToListAsync())
            .Select(a => $"{a.EmployeeId} {a.ProjectId} {a.WbsElementId} {a.WeekStart} {a.Hours} {a.AssignmentId is not null}").Order();
        return string.Join("\n", items.Concat(assignments).Concat(rows));
    }
}
