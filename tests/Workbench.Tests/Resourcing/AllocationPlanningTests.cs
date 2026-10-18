using Microsoft.EntityFrameworkCore;
using Workbench.Application.Resourcing;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Resourcing;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

public sealed class AllocationPlanningTests
{
    private static async Task SetPlanAsync(ResourcingHost host, string title, string? owner, decimal? hours)
    {
        await using var db = await host.Db.CreateAsync();
        var item = await db.Items.SingleAsync(i => i.SheetId == host.SheetId && i.Title == title);
        var values = new Dictionary<string, string?>(item.Values) { [host.OwnerColumn.ToString()] = owner, [host.HoursColumn.ToString()] = hours?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        item.Values = values;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Generate_from_plan_spreads_planned_hours_over_working_days_and_replaces_only_plan_allocations()
    {
        await using var host = await CreateAsync();
        // Design: Oct 12–23 (10 working days), dev@, 60 h → 6 h/day. Build: Oct 19–30, analyst@ and dev@ sharing 40 h → 2 h/day each.
        await SetPlanAsync(host, "Design", Dev, 60);
        await SetPlanAsync(host, "Build", $"{Analyst}, {Dev}", 40);
        await SetPlanAsync(host, "Phase 1", Pm, 500); // a summary task: ignored
        var allocations = host.As(Pm).Get<IResourceAllocationService>();
        var manual = await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 5, WbsElementId: host.Build));

        var result = await allocations.GenerateFromPlanAsync(host.Project1);

        var rows = await allocations.ListAsync(new AllocationQuery(ThisWeek, ThisWeek.AddDays(27), host.Project1));
        var plan = rows.Where(r => r.Source == AllocationOrigin.Plan).ToList();
        Assert.Equal(100m, result.TotalHours);
        Assert.Equal(30m, plan.Single(r => r.EmployeeId == host.DevId && r.WbsElementId == host.Design && r.WeekStart == ThisWeek).Hours);
        Assert.Equal(30m, plan.Single(r => r.EmployeeId == host.DevId && r.WbsElementId == host.Design && r.WeekStart == ThisWeek.AddDays(7)).Hours);
        Assert.Equal(10m, plan.Single(r => r.EmployeeId == host.AnalystId && r.WbsElementId == host.Build && r.WeekStart == ThisWeek.AddDays(14)).Hours);
        Assert.Equal("BA2", plan.First(r => r.EmployeeId == host.AnalystId).LaborCategoryCode);
        Assert.DoesNotContain(plan, r => r.EmployeeId == host.PmId);
        Assert.Contains(rows, r => r.Id == manual[0].Id);

        // Regenerating replaces the plan rows; the manual one stays.
        await SetPlanAsync(host, "Design", Dev, 20);
        var again = await allocations.GenerateFromPlanAsync(host.Project1);
        Assert.Equal((6, 60m), (again.Replaced, again.TotalHours));
        rows = await allocations.ListAsync(new AllocationQuery(ThisWeek, ThisWeek.AddDays(27), host.Project1));
        Assert.Equal(10m, rows.Single(r => r.Source == AllocationOrigin.Plan && r.WbsElementId == host.Design && r.WeekStart == ThisWeek).Hours);
        Assert.Contains(rows, r => r.Id == manual[0].Id);
    }

    [Fact]
    public async Task Generate_from_plan_books_a_standard_day_without_planned_hours_and_reports_what_it_skipped()
    {
        await using var host = await CreateAsync();
        await SetPlanAsync(host, "Design", Analyst, null);
        await SetPlanAsync(host, "Build", "stranger@example.com", 10);

        var result = await host.As(Pm).Get<IResourceAllocationService>().GenerateFromPlanAsync(host.Project1, new GenerateAllocationsRequest(new DateOnly(2026, 10, 19)));

        Assert.Equal(40m, result.TotalHours); // Oct 19–23 only (from the 19th): 5 days × 8 h
        Assert.Contains(result.Skipped, s => s.Contains("stranger@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Staffing_compares_remaining_budget_hours_with_allocations_by_labor_category()
    {
        await using var host = await CreateAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            var current = new BudgetVersion { ProjectId = host.Project1, Name = "Working", Kind = BudgetKind.Current, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow };
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = host.Design, CostType = BudgetCostType.Labor, LaborCategoryId = host.Se2, Hours = 200, Amount = 12_000 });
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = host.Build, CostType = BudgetCostType.Labor, LaborCategoryId = host.Ba2, Hours = 50, Amount = 2_500 });
            db.Set<BudgetVersion>().Add(current);
            await db.SaveChangesAsync();
        }

        await host.AddApprovedTimeAsync(host.DevId, (host.Design, host.Se2, new DateOnly(2026, 10, 6), 20m));
        var allocations = host.As(Pm).Get<IResourceAllocationService>();
        await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 30, Weeks: 2)); // SE2: 60
        await allocations.CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project1, ThisWeek, 40, Weeks: 2)); // BA2: 80
        await allocations.CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project1, ThisWeek.AddDays(-7), 40)); // past: ignored

        var staffing = await host.Get<IResourcePlanningService>().GetProjectStaffingAsync(host.Project1);

        var se2 = staffing.Rows.Single(r => r.LaborCategoryId == host.Se2);
        Assert.Equal((200m, 20m, 180m, 60m, 120m, StaffingGapStatus.Understaffed), (se2.BudgetHours, se2.ActualHours, se2.RemainingBudgetHours, se2.AllocatedHours, se2.Gap, se2.Status));
        var ba2 = staffing.Rows.Single(r => r.LaborCategoryId == host.Ba2);
        Assert.Equal((50m, 80m, -30m, StaffingGapStatus.Overstaffed), (ba2.RemainingBudgetHours, ba2.AllocatedHours, ba2.Gap, ba2.Status));
        Assert.Equal("Working", staffing.BudgetVersionName);

        var demand = await host.Get<IResourcePlanningService>().ListOpenDemandAsync(host.Project1);
        Assert.Empty(demand); // budget lines aren't phased into periods here
    }

    [Fact]
    public async Task Open_demand_lists_phased_budget_hours_without_allocations()
    {
        await using var host = await CreateAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            var current = new BudgetVersion { ProjectId = host.Project1, Name = "Working", Kind = BudgetKind.Current, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow };
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = host.Design, CostType = BudgetCostType.Labor, LaborCategoryId = host.Se2, PeriodStart = new DateOnly(2026, 11, 1), Hours = 100, Amount = 6_000 });
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = host.Design, CostType = BudgetCostType.Labor, LaborCategoryId = host.Se2, PeriodStart = new DateOnly(2026, 9, 1), Hours = 100, Amount = 6_000 });
            db.Set<BudgetVersion>().Add(current);
            await db.SaveChangesAsync();
        }

        // Week of Oct 26 has 5 days in October → none of it counts for November; Nov 2 and Nov 9 weeks count fully.
        await host.As(Pm).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, new DateOnly(2026, 10, 26), 20, Weeks: 3));

        var demand = Assert.Single(await host.Get<IResourcePlanningService>().ListOpenDemandAsync());
        Assert.Equal(("2026-11", 100m, 40m, 60m), (demand.PeriodName, demand.BudgetHours, demand.AllocatedHours, demand.OpenHours));
    }

    [Fact]
    public async Task Utilization_is_approved_hours_over_available_hours()
    {
        await using var host = await CreateAsync();
        host.Leave.Holidays.Add(new HolidayDay(new DateOnly(2026, 10, 12), "Columbus Day"));
        host.Leave.Leave.Add(new ApprovedLeaveDay(host.DevId, new DateOnly(2026, 10, 16), 8));
        // dev@, Oct 12–16: 24 direct + 4 indirect + 8 leave (leave isn't worked time).
        await host.AddApprovedTimeAsync(host.DevId,
            (host.Design, host.Se2, new DateOnly(2026, 10, 13), 8m),
            (host.Design, host.Se2, new DateOnly(2026, 10, 14), 8m),
            (host.Build, host.Se2, new DateOnly(2026, 10, 15), 8m),
            (host.IndirectWbs, host.Se2, new DateOnly(2026, 10, 15), 4m),
            (host.LeaveWbs, host.Se2, new DateOnly(2026, 10, 16), 8m));

        var data = await host.As(Admin).Get<IResourcePlanningService>().GetUtilizationAsync(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 16));

        var dev = data.ByPerson.Single(r => r.Key == host.DevId.ToString());
        // Available: 40 − 8 holiday − 8 leave = 24. Direct 24/24 = 100%; total 28/24.
        Assert.Equal((24m, 28m, 24m, 1m, 1.1667m), (dev.AvailableHours, dev.ActualHours, dev.DirectHours, dev.DirectUtilization, dev.TotalUtilization));
        Assert.Equal(0.9m, data.TargetUtilization);
        Assert.Equal("Oct 2026", Assert.Single(data.ByMonth).Name);
        // Four people × 32 available (dev 24) = 120; only dev worked.
        Assert.Equal((120m, 24m), (data.Total.AvailableHours, data.Total.DirectHours));
        Assert.Equal("Engineering", Assert.Single(data.ByOrgUnit).Name);
    }

    [Fact]
    public async Task Allocation_based_etc_prices_future_allocations_at_cost_plus_remaining_odc_budget()
    {
        await using var host = await CreateAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            var current = new BudgetVersion { ProjectId = host.Project1, Name = "Working", Kind = BudgetKind.Current, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow };
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = host.Design, CostType = BudgetCostType.Labor, LaborCategoryId = host.Se2, Hours = 100, Amount = 6_000 });
            current.Lines.Add(new BudgetLine { BudgetVersionId = current.Id, WbsElementId = host.Build, CostType = BudgetCostType.Odc, OdcType = OdcType.Travel, Amount = 1_000 });
            db.Set<BudgetVersion>().Add(current);
            db.Set<OdcEntry>().Add(new OdcEntry { ProjectId = host.Project1, WbsElementId = host.Build, Type = OdcType.Travel, IncurredOn = new DateOnly(2026, 10, 5), Amount = 400, Description = "Trip", SubmittedBy = Finance, Status = OdcStatus.Approved, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await host.AddApprovedTimeAsync(host.DevId, (host.Design, host.Se2, new DateOnly(2026, 10, 6), 10m));
        var allocations = host.As(Pm).Get<IResourceAllocationService>();
        // dev@ costs $60 until Nov 2, then $70: weeks of Oct 26 (60) and Nov 2 (70), 20 h each, on Design.
        await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, new DateOnly(2026, 10, 26), 20, WbsElementId: host.Design, Weeks: 2));
        // analyst@ ($50), no WBS → the project's root element; a past week is ignored.
        await allocations.CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project1, ThisWeek, 10));
        await allocations.CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project1, ThisWeek.AddDays(-7), 10));

        var summary = await host.Get<IAllocationForecastService>().GetProjectSummaryAsync(host.Project1);
        var remaining = await host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(host.Project1);

        // Labor ETC: 20 × 60 + 20 × 70 + 10 × 50 = 3,100; ODC ETC: 1,000 − 400 = 600.
        Assert.Equal((50m, 3_700m), (summary.Totals.EtcHours, summary.Totals.EtcAmount));
        Assert.Equal(1_000m, summary.Totals.ActualCost); // 10 h × $60 + $400 ODC
        Assert.Equal(4_700m, summary.Totals.EacAmount);
        Assert.Equal("Allocation-based ETC", summary.ForecastVersionName);
        Assert.Equal(remaining.Totals.ActualCost, summary.Totals.ActualCost);
        Assert.NotEqual(remaining.Totals.EtcAmount, summary.Totals.EtcAmount);

        var rows = await host.Get<IAllocationForecastService>().GetWbsFinancialsAsync(host.Project1);
        Assert.Equal(2_600m, rows.Single(r => r.WbsElementId == host.Design).Figures.EtcAmount);
        Assert.Equal(3_700m, rows.Single(r => r.WbsElementId == host.Root1).Figures.EtcAmount);
    }
}
