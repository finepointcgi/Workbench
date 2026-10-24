using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// People staffed week by week before assigning by total hours existed: the Plan tab shows them as the assignment they'd
/// be and changes them in place. The first change makes them a real assignment over the same weeks, so hours, cost and
/// charging are as they were; then the change applies like any other. Quinn (no charge assignment on 1001, $80/h) is
/// staffed on Build: 8 h the week of Oct 5 (past, SE2), 16 h Oct 12 and 20 h Oct 19 (no labor category: their own, SE2),
/// and 30 h Oct 26 (SE2, generated from the plan). Today is Wed Oct 14 2026.
/// </summary>
public sealed class WeekByWeekStaffingTests
{
    private static readonly DateOnly Oct5 = new(2026, 10, 5);
    private static readonly DateOnly Oct12 = new(2026, 10, 12);
    private static readonly DateOnly Oct19 = new(2026, 10, 19);
    private static readonly DateOnly Oct26 = new(2026, 10, 26);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);

    private static readonly (DateOnly, decimal)[] Weeks = [(Oct5, 8m), (Oct12, 16m), (Oct19, 20m), (Oct26, 30m)];

    [Fact]
    public async Task The_first_change_makes_it_an_assignment_with_the_same_weeks_cost_and_charging()
    {
        await using var host = await SeedAsync();
        var plans = host.As(Pm).Get<IResourcePlanService>();
        var build = await BuildItemAsync(host);

        // Before: one row for Quinn on Build (the uncategorized weeks count in their own SE2), with an id to change it by.
        var before = await plans.GetAsync(host.Project1);
        var quinn = Assert.Single(before.Task(build)!.People);
        Assert.NotNull(quinn.AssignmentId);
        Assert.Equal((74m, 5920m, Oct5, Nov1, (Guid?)host.Se2), (quinn.Hours, quinn.DirectCost!.Value, quinn.From, quinn.To, quinn.LaborCategoryId));
        Assert.Equal(quinn.AssignmentId, (await plans.GetAsync(host.Project1)).Task(build)!.People.Single().AssignmentId); // the same on every read
        var chargingBefore = await ChargingAsync(host);
        Assert.Contains(chargingBefore, c => c.Date == new DateOnly(2026, 10, 21) && c.Wbs == host.Build && c.Category == host.Se2);
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.False(await db.Set<StaffingAssignment>().AnyAsync());
        }

        // Re-saving the same hours only converts.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: quinn.AssignmentId, Hours: 74m)]);

        await using (var db = await host.Db.CreateAsync())
        {
            var assignment = await db.Set<StaffingAssignment>().SingleAsync();
            Assert.Equal((quinn.AssignmentId!.Value, host.Pm2Id, (Guid?)host.Build, (Guid?)host.Se2, Oct5, Nov1, 74m),
                (assignment.Id, assignment.EmployeeId, assignment.WbsElementId, assignment.LaborCategoryId, assignment.From, assignment.To, assignment.TotalHours));
            var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(r => r.EmployeeId == host.Pm2Id).OrderBy(r => r.WeekStart).ToListAsync();
            Assert.Equal(Weeks, rows.Select(r => (r.WeekStart, r.Hours)));
            Assert.All(rows, r => Assert.Equal((assignment.Id, AllocationSource.Manual), (r.AssignmentId!.Value, r.Source)));
        }

        var after = await plans.GetAsync(host.Project1);
        var converted = Assert.Single(after.Task(build)!.People);
        Assert.Equal((quinn.AssignmentId, quinn.Hours, quinn.DirectCost, quinn.From, quinn.To, quinn.LaborCategoryId),
            (converted.AssignmentId, converted.Hours, converted.DirectCost, converted.From, converted.To, converted.LaborCategoryId));
        Assert.Equal((before.Totals.Hours, before.Totals.Cost), (after.Totals.Hours, after.Totals.Cost));
        Assert.Equal(chargingBefore, await ChargingAsync(host));

        // Budget & staffing lists it with the assignments now, not as week-by-week hours.
        var listed = await host.As(Pm).Get<IStaffingAssignmentService>().ListAsync(host.Project1);
        Assert.Equal((1, 0), (listed.Assignments.Count, listed.WeekByWeek.Count));
    }

    [Fact]
    public async Task Changing_the_hours_spreads_the_new_total_keeps_past_weeks_and_undo_puts_the_weeks_back()
    {
        await using var host = await SeedAsync();
        var plans = host.As(Pm).Get<IResourcePlanService>();
        var build = await BuildItemAsync(host);
        var id = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single().AssignmentId!.Value;

        var applied = await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: id, Hours: 100m)]);

        await using (var db = await host.Db.CreateAsync())
        {
            Assert.Equal(100m, (await db.Set<StaffingAssignment>().SingleAsync(a => a.Id == id)).TotalHours);
            var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(r => r.EmployeeId == host.Pm2Id).ToListAsync();
            Assert.Equal(100m, rows.Sum(r => r.Hours));
            Assert.Equal(8m, rows.Single(r => r.WeekStart == Oct5).Hours); // the week already past keeps its hours
            Assert.All(rows, r => Assert.Equal(id, r.AssignmentId));
        }

        var plan = await plans.GetAsync(host.Project1);
        Assert.Equal((100m, 8000m), (plan.Task(build)!.People.Single().Hours, plan.Task(build)!.People.Single().DirectCost!.Value));
        Assert.Contains(await ChargingAsync(host), c => c.Date == new DateOnly(2026, 10, 21) && c.Wbs == host.Build && c.Category == host.Se2);

        // Undo puts back the weeks exactly as they were staffed.
        await plans.ApplyAsync(host.Project1, applied.Undo);
        await using (var db = await host.Db.CreateAsync())
        {
            var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(r => r.EmployeeId == host.Pm2Id).OrderBy(r => r.WeekStart).ToListAsync();
            Assert.Equal(Weeks, rows.Select(r => (r.WeekStart, r.Hours)));
        }
    }

    [Fact]
    public async Task Trying_a_change_saves_nothing_and_taking_them_off_removes_the_weeks_with_an_undo()
    {
        await using var host = await SeedAsync();
        var plans = host.As(Pm).Get<IResourcePlanService>();
        var build = await BuildItemAsync(host);
        var id = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single().AssignmentId!.Value;

        var tried = await plans.TryAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: id, Hours: 100m)]);
        Assert.True(tried.CanApply);
        Assert.Equal((74m, 100m), (tried.Before.Hours, tried.After.Hours));
        Assert.Equal((100m, true), (tried.Tasks.Single(t => t.ItemId == build).People.Single().Hours, tried.Tasks.Single(t => t.ItemId == build).People.Single().Changed));
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.False(await db.Set<StaffingAssignment>().AnyAsync());
            Assert.All(await db.Set<ResourceAllocation>().AsNoTracking().ToListAsync(), r => Assert.Null(r.AssignmentId));
        }

        var applied = await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.RemovePerson, AssignmentId: id)]);
        Assert.Empty((await plans.GetAsync(host.Project1)).Task(build)!.People);
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.False(await db.Set<ResourceAllocation>().AnyAsync(r => r.EmployeeId == host.Pm2Id));
            Assert.False(await db.Set<StaffingAssignment>().AnyAsync());
        }

        Assert.DoesNotContain(await ChargingAsync(host), c => c.Wbs == host.Build);

        await plans.ApplyAsync(host.Project1, applied.Undo);
        var back = Assert.Single((await plans.GetAsync(host.Project1)).Task(build)!.People);
        Assert.Equal((74m, Oct5, Nov1), (back.Hours, back.From, back.To));
        await using (var db = await host.Db.CreateAsync())
        {
            var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(r => r.EmployeeId == host.Pm2Id).OrderBy(r => r.WeekStart).ToListAsync();
            Assert.Equal(Weeks, rows.Select(r => (r.WeekStart, r.Hours)));
        }
    }

    [Fact]
    public async Task Weeks_changed_by_someone_else_since_the_plan_was_read_are_not_converted()
    {
        await using var host = await SeedAsync();
        var plans = host.As(Pm).Get<IResourcePlanService>();
        var build = await BuildItemAsync(host);
        var id = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single().AssignmentId!.Value;

        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<ResourceAllocation>().Add(Row(host, new DateOnly(2026, 11, 2), 10m, host.Se2));
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: id, Hours: 50m)]));
        Assert.Contains("Reload", ex.Message, StringComparison.Ordinal);
        await using (var check = await host.Db.CreateAsync())
        {
            Assert.False(await check.Set<StaffingAssignment>().AnyAsync());
        }

        // Read again, it's one row of 84 h with a new id, and changes.
        var fresh = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single();
        Assert.Equal(84m, fresh.Hours);
        Assert.NotEqual(id, fresh.AssignmentId);
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: fresh.AssignmentId, Hours: 50m)]);
        Assert.Equal(50m, (await plans.GetAsync(host.Project1)).Task(build)!.People.Single().Hours);
    }

    // ---- Helpers ----

    private static async Task<ResourcingHost> SeedAsync()
    {
        var host = await CreateAsync();
        await using var db = await host.Db.CreateAsync();
        db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = host.Pm2Id, EffectiveFrom = new DateOnly(2024, 1, 1), HourlyCost = 80m });
        db.Set<ResourceAllocation>().AddRange(
            Row(host, Oct5, 8m, host.Se2),
            Row(host, Oct12, 16m, null),
            Row(host, Oct19, 20m, null),
            Row(host, Oct26, 30m, host.Se2, AllocationSource.Plan));
        await db.SaveChangesAsync();
        return host;
    }

    private static ResourceAllocation Row(ResourcingHost host, DateOnly week, decimal hours, Guid? category, AllocationSource source = AllocationSource.Manual) => new()
    {
        EmployeeId = host.Pm2Id, ProjectId = host.Project1, WbsElementId = host.Build, LaborCategoryId = category, WeekStart = week, Hours = hours,
        Source = source, CreatedBy = "rm@example.com", CreatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<Guid> BuildItemAsync(ResourcingHost host)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Items.Where(i => i.SheetId == host.SheetId && i.Title == "Build").Select(i => i.Id).SingleAsync();
    }

    /// <summary>What Quinn may charge on project 1001, day by day from Sep 28 to Nov 15, with the labor category.</summary>
    private static async Task<List<(DateOnly Date, Guid Wbs, Guid Category)>> ChargingAsync(ResourcingHost host)
    {
        var days = Enumerable.Range(0, 49).Select(i => new DateOnly(2026, 9, 28).AddDays(i)).ToList();
        var codes = await host.As(Admin).Get<IProjectService>().ListChargeCodesForDatesAsync(host.Pm2Id, days);
        host.As(Pm);
        return codes.SelectMany(d => d.Value.Where(c => c.ProjectId == host.Project1).Select(c => (d.Key, c.WbsElementId, c.LaborCategoryId)))
            .OrderBy(c => c.Key).ThenBy(c => c.WbsElementId).ToList();
    }
}
