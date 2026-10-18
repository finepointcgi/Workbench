using Microsoft.EntityFrameworkCore;
using Workbench.Application.IndirectRates;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Domain.Accounting;

namespace Workbench.Tests.IndirectRates;

public sealed class CascadeTests
{
    private static readonly PoolStep[] Standard =
    [
        new(IndirectPool.Fringe, AllocationBase.DirectLabor),
        new(IndirectPool.Overhead, AllocationBase.DirectLaborPlusFringe),
        new(IndirectPool.GeneralAndAdministrative, AllocationBase.TotalCostInput),
    ];

    [Fact]
    public void Cascade_matches_the_hand_calculation()
    {
        var rates = new Dictionary<IndirectPool, decimal> { [IndirectPool.Fringe] = 0.30m, [IndirectPool.Overhead] = 0.50m, [IndirectPool.GeneralAndAdministrative] = 0.10m };
        var burden = BurdenCascade.Apply(Standard, 1000m, 500m, p => rates[p]);

        Assert.Equal(300m, burden.Fringe);                    // 1,000 × 30%
        Assert.Equal(650m, burden.Overhead);                  // (1,000 + 300) × 50%
        Assert.Equal(245m, burden.GeneralAndAdministrative);  // (1,000 + 300 + 650 + 500) × 10%
        Assert.Equal(1195m, burden.Total);
    }

    [Fact]
    public void Each_pool_rounds_to_the_cent_before_the_next_uses_it()
    {
        var burden = BurdenCascade.Apply(Standard, 333.33m, 0m, p => p == IndirectPool.Fringe ? 0.3333m : 0.1m);
        Assert.Equal(111.10m, burden.Fringe);       // 111.0989 → 111.10
        Assert.Equal(44.44m, burden.Overhead);      // 444.43 × 10% = 44.443
        Assert.Equal(48.89m, burden.GeneralAndAdministrative); // 488.87 × 10%
    }

    [Fact]
    public void Value_added_base_leaves_out_odcs()
    {
        PoolStep[] steps = [new(IndirectPool.Fringe, AllocationBase.DirectLabor), new(IndirectPool.GeneralAndAdministrative, AllocationBase.ValueAdded)];
        var burden = BurdenCascade.Apply(steps, 1000m, 5000m, p => 0.10m);
        Assert.Equal(100m, burden.Fringe);
        Assert.Equal(110m, burden.GeneralAndAdministrative); // (1,000 + 100) × 10%; the $5,000 of ODCs carries none
    }

    [Fact]
    public void Actual_rates_divide_each_pool_by_its_cascaded_base()
    {
        var pools = new Dictionary<IndirectPool, decimal> { [IndirectPool.Fringe] = 330m, [IndirectPool.Overhead] = 900m, [IndirectPool.GeneralAndAdministrative] = 300m };
        var rates = BurdenCascade.ActualRates(Standard, 1000m, 500m, p => pools[p]).ToDictionary(r => r.Pool);
        Assert.Equal((1000m, 0.33m), (rates[IndirectPool.Fringe].Base, rates[IndirectPool.Fringe].Rate!.Value));
        Assert.Equal(1330m, rates[IndirectPool.Overhead].Base);
        Assert.Equal(0.676692m, rates[IndirectPool.Overhead].Rate);
        Assert.Equal(2730m, rates[IndirectPool.GeneralAndAdministrative].Base);   // 1,000 + 330 + 900 + 500
        Assert.Equal(0.10989m, rates[IndirectPool.GeneralAndAdministrative].Rate);
        Assert.Null(BurdenCascade.ActualRates(Standard, 0, 0, _ => 1m)[0].Rate);
    }
}

public sealed class AllocationTests
{
    [Fact]
    public async Task Allocation_posts_a_balanced_entry_per_pool_with_project_segments()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();

        var preview = await kit.Rates.PreviewAllocationAsync(kit.Oct.Id);
        var line = Assert.Single(preview.Lines);
        Assert.Equal((1000m, 500m, 300m, 650m, 245m), (line.DirectLabor, line.DirectOdc, line.Fringe, line.Overhead, line.GeneralAndAdministrative));
        Assert.False(preview.IsCurrent);

        var posted = await kit.Rates.PostAllocationAsync(kit.Oct.Id);
        Assert.True(posted.IsCurrent);
        var entry = Assert.Single(await kit.Gl.EntriesAsync(JournalSource.Allocation));
        Assert.Equal(JournalStatus.Posted, entry.Status);
        Assert.Equal(kit.Oct.Id, entry.SourceId);
        Assert.Equal(kit.Oct.End, entry.Date);
        Assert.True(entry.IsBalanced);
        Assert.Equal(1195m, entry.TotalDebit);
        Assert.All(entry.Lines.Where(l => l.Debit > 0), l => Assert.Equal((kit.Project.Id, kit.Task1.Id), (l.ProjectId!.Value, l.WbsElementId!.Value)));
        Assert.Equal(300m, await kit.Gl.BalanceAsync("5910"));
        Assert.Equal(650m, await kit.Gl.BalanceAsync("5920"));
        Assert.Equal(245m, await kit.Gl.BalanceAsync("5930"));
        Assert.Equal(-300m, await kit.Gl.BalanceAsync("6090"));
        Assert.Equal(-650m, await kit.Gl.BalanceAsync("6190"));
        Assert.Equal(-245m, await kit.Gl.BalanceAsync("6290"));
        Assert.True((await kit.Gl.Reports.GetTrialBalanceAsync(kit.Oct.Id)).IsBalanced);
    }

    [Fact]
    public async Task Re_running_reverses_and_reposts_without_doubling()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.Rates.PostAllocationAsync(kit.Oct.Id);

        var rerun = await kit.Rates.PostAllocationAsync(kit.Oct.Id);

        var entries = await kit.Gl.EntriesAsync();
        var allocations = entries.Where(e => e.Source == JournalSource.Allocation).ToList();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(JournalStatus.Reversed, allocations[0].Status);
        Assert.Equal(JournalStatus.Posted, allocations[1].Status);
        Assert.Equal(2, allocations[1].SourceRevision);
        Assert.Single(entries, e => e.Source == JournalSource.Reversal);
        Assert.Equal(2, rerun.Posted!.Revision);
        Assert.Equal(300m, await kit.Gl.BalanceAsync("5910"));
        Assert.Equal(1195m, rerun.Posted.Total);
        Assert.True((await kit.Gl.Reports.GetTrialBalanceAsync(kit.Oct.Id)).IsBalanced);

        // New cost makes the posted run out of date; re-running picks it up.
        await kit.AddTimeAsync((kit.Task2, IndirectRatesKit.October.AddDays(12), 2m));
        Assert.False((await kit.Rates.PreviewAllocationAsync(kit.Oct.Id)).IsCurrent);
        var third = await kit.Rates.PostAllocationAsync(kit.Oct.Id);
        Assert.True(third.IsCurrent);
        Assert.Equal(300m + 60m, await kit.Gl.BalanceAsync("5910"));
    }

    [Fact]
    public async Task Reverse_takes_the_allocation_out()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.Rates.PostAllocationAsync(kit.Oct.Id);

        var after = await kit.Rates.ReverseAllocationAsync(kit.Oct.Id, new ReverseAllocationRequest("Wrong rates"));
        Assert.Null(after.Posted);
        Assert.Equal(0m, await kit.Gl.BalanceAsync("5910"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Rates.ReverseAllocationAsync(kit.Oct.Id, new ReverseAllocationRequest("Again")));
    }

    [Fact]
    public async Task Closed_periods_are_blocked()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.Rates.PostAllocationAsync(kit.Oct.Id);
        await kit.CloseAsync(kit.Oct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Rates.PostAllocationAsync(kit.Oct.Id));
        Assert.Contains("closed", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Rates.ReverseAllocationAsync(kit.Oct.Id, new ReverseAllocationRequest("x")));
        Assert.True((await kit.Rates.PreviewAllocationAsync(kit.Oct.Id)).PeriodClosed);
        Assert.Single(await kit.Gl.EntriesAsync(JournalSource.Allocation));
    }

    [Fact]
    public async Task Unallowable_cost_is_never_in_the_base_or_burdened()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.AddOdcAsync(kit.Task2, IndirectRatesKit.October.AddDays(8), 1000m, unallowable: true);

        var preview = await kit.Rates.PreviewAllocationAsync(kit.Oct.Id);
        var line = Assert.Single(preview.Lines); // 2001.02 has only unallowable cost: nothing to burden
        Assert.Equal(500m, line.DirectOdc);
        Assert.Equal(1195m, preview.Total);
        Assert.Contains(preview.Notes, n => n.Contains("unallowable"));

        var cost = await kit.Burdened.GetProjectCostAsync(kit.Project.Id, kit.Oct.Start, kit.Oct.End, RateBasis.Provisional);
        Assert.Equal(200m, cost.Lines.Single(l => l.WbsElementId == kit.Task1.Id).UnallowableExcluded);
        var task2 = cost.Lines.Single(l => l.WbsElementId == kit.Task2.Id);
        Assert.Equal((1000m, 0m), (task2.UnallowableExcluded, task2.TotalCost));
    }

    [Fact]
    public async Task Period_close_checklist_requires_the_allocation()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.Gl.Posting.PostBacklogAsync();

        var before = await kit.Close.GetChecklistAsync(kit.Oct.Id);
        var check = Assert.Single(before.Checks, c => c.Kind == CloseCheckKind.AllocationPosted);
        Assert.False(check.Passed);
        Assert.False(before.CanClose);

        await kit.Rates.PostAllocationAsync(kit.Oct.Id);
        var after = await kit.Close.GetChecklistAsync(kit.Oct.Id);
        Assert.True(after.Checks.Single(c => c.Kind == CloseCheckKind.AllocationPosted).Passed);
        Assert.True(after.CanClose);

        // A period with nothing to allocate passes.
        Assert.True((await kit.Close.GetChecklistAsync(kit.Periods["2026-12"].Id)).Checks.Single(c => c.Kind == CloseCheckKind.AllocationPosted).Passed);
    }

    [Fact]
    public async Task Reconciliation_keeps_applied_burden_out_of_direct_cost()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.Gl.Posting.PostBacklogAsync();
        await kit.Rates.PostAllocationAsync(kit.Oct.Id);

        var report = await new BurdenedReconciliationReportService(kit.Gl.Reports, kit.Factory).GetReconciliationAsync(kit.Project.Id);
        Assert.True(report.IsReconciled);
        var row = Assert.Single(report.Rows);
        Assert.Equal((1000m, 500m), (row.GlLabor, row.GlOdc));
        Assert.Equal((1195m, 1195m, 0m), (row.GlAppliedBurden, row.ExpectedBurden, row.BurdenDifference));
        Assert.Equal(2695m, row.BurdenedTotal);
    }
}

public sealed class RateCalculationTests
{
    [Fact]
    public async Task Actual_rates_come_from_gl_pool_balances_without_unallowables()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.AddTimeAsync((kit.Overhead, IndirectRatesKit.October.AddDays(5), 2m)); // $200 overhead labor (6110 via the posting rule)
        await kit.Gl.Posting.PostBacklogAsync();
        await kit.Gl.Journal.CreateAndPostAsync(kit.Gl.Entry(IndirectRatesKit.October.AddDays(20),
            ("6020", 330m, 0), ("6120", 700m, 0), ("6230", 300m, 0), ("7090", 999m, 0), ("1010", 0, 2329m)));
        await kit.Rates.PostAllocationAsync(kit.Oct.Id);

        var report = await kit.Rates.GetRateCalculationAsync(kit.Oct.Id, kit.Oct.Id);
        var pools = report.Pools.ToDictionary(p => p.Pool);
        Assert.Equal((330m, 1000m, 0.33m), (pools[CostPool.Fringe].PoolCost, pools[CostPool.Fringe].BaseAmount, pools[CostPool.Fringe].ActualRate!.Value));
        Assert.Equal((900m, 1330m), (pools[CostPool.Overhead].PoolCost, pools[CostPool.Overhead].BaseAmount));
        Assert.Equal(0.676692m, pools[CostPool.Overhead].ActualRate);
        Assert.Equal((300m, 2730m), (pools[CostPool.GeneralAndAdministrative].PoolCost, pools[CostPool.GeneralAndAdministrative].BaseAmount));
        Assert.Equal(0.30m, pools[CostPool.Fringe].ProvisionalRate);
        Assert.Equal(0.03m, pools[CostPool.Fringe].Variance);
        Assert.Equal((300m, 30m), (pools[CostPool.Fringe].Applied, pools[CostPool.Fringe].UnderApplied));

        var trend = Assert.Single(report.Trend, t => t.Pool == CostPool.Fringe);
        Assert.Equal((0.33m, 0.33m), (trend.MonthRate!.Value, trend.YtdRate!.Value));
    }

    [Fact]
    public async Task Year_to_date_accumulates_through_each_month()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        await kit.AddTimeAsync((kit.Task1, IndirectRatesKit.October.AddDays(5), 10m));
        await kit.AddTimeAsync((kit.Task1, new DateOnly(2026, 11, 9), 10m));
        await kit.Gl.Posting.PostBacklogAsync();
        await kit.Gl.Journal.CreateAndPostAsync(kit.Gl.Entry(IndirectRatesKit.October.AddDays(20), ("6020", 200m, 0), ("1010", 0, 200m)));
        await kit.Gl.Journal.CreateAndPostAsync(kit.Gl.Entry(new DateOnly(2026, 11, 20), ("6020", 400m, 0), ("1010", 0, 400m)));

        var report = await kit.Rates.GetRateCalculationAsync(kit.Periods["2026-11"].Id, kit.Periods["2026-11"].Id);
        var nov = Assert.Single(report.Trend, t => t.Pool == CostPool.Fringe);
        Assert.Equal(0.40m, nov.MonthRate);   // 400 / 1,000
        Assert.Equal(0.30m, nov.YtdRate);     // (200 + 400) / 2,000
        Assert.Equal(0.40m, report.Pools.Single(p => p.Pool == CostPool.Fringe).ActualRate);
    }
}

public sealed class RateBasisAndTrueUpTests
{
    [Fact]
    public async Task Actual_falls_back_to_provisional_until_every_pool_has_an_actual_rate()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        var oct = kit.Oct;

        var fallback = await kit.Burdened.GetProjectCostAsync(kit.Project.Id, oct.Start, oct.End, RateBasis.Actual);
        Assert.Equal(new AppliedRates(RateBasis.Provisional, 2027, 0.30m, 0.50m, 0.10m), Assert.Single(fallback.Rates));

        await kit.SetRateAsync(CostPool.Fringe, 2027, RateBasis.Actual, 0.33m);
        Assert.Equal(RateBasis.Provisional, Assert.Single((await kit.Burdened.GetProjectCostAsync(kit.Project.Id, oct.Start, oct.End, RateBasis.Actual)).Rates).Basis);

        await kit.SetRateAsync(CostPool.Overhead, 2027, RateBasis.Actual, 0.68m);
        await kit.SetRateAsync(CostPool.GeneralAndAdministrative, 2027, RateBasis.Actual, 0.11m);
        var actual = await kit.Burdened.GetProjectCostAsync(kit.Project.Id, oct.Start, oct.End, RateBasis.Actual);
        Assert.Equal(new AppliedRates(RateBasis.Actual, 2027, 0.33m, 0.68m, 0.11m), Assert.Single(actual.Rates));
        Assert.Equal(RateBasis.Provisional, Assert.Single((await kit.Burdened.GetProjectCostAsync(kit.Project.Id, oct.Start, oct.End, RateBasis.Provisional)).Rates).Basis);
    }

    [Fact]
    public async Task Changing_a_rate_supersedes_it_and_needs_a_reason()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.SetRateAsync(CostPool.Fringe, 2027, RateBasis.Provisional, 0.30m);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.SetRateAsync(CostPool.Fringe, 2027, RateBasis.Provisional, 0.31m));
        await kit.SetRateAsync(CostPool.Fringe, 2027, RateBasis.Provisional, 0.31m, "Revised forecast");

        Assert.Equal(0.31m, Assert.Single(await kit.Rates.ListRatesAsync(2027)).Rate);
        Assert.Equal(2, (await kit.Rates.ListRatesAsync(2027, includeHistory: true)).Count);
        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(3, await db.Set<Workbench.Domain.ProjectAccounting.AuditEvent>().CountAsync(a => a.EntityType == IndirectRateService.RateEntityType));
    }

    [Fact]
    public async Task True_up_reports_the_difference_and_posts_the_adjustment()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.Rates.PostAllocationAsync(kit.Oct.Id);

        var none = await kit.Rates.GetTrueUpAsync(2027);
        Assert.False(none.HasActualRates);
        Assert.Equal(0m, none.Difference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Rates.PostTrueUpAsync(2027, new PostTrueUpRequest()));

        await kit.SetRatesAsync(2027, RateBasis.Actual, 0.33m, 0.68m, 0.11m);
        var report = await kit.Rates.GetTrueUpAsync(2027);
        var line = Assert.Single(report.Lines);
        // Actual: fringe 330; overhead 1,330 × 68% = 904.40; G&A (1,000 + 330 + 904.40 + 500) × 11% = 300.78.
        Assert.Equal(1195m, line.ProvisionalBurden);
        Assert.Equal(1535.18m, line.ActualBurden);
        Assert.Equal((30m, 254.40m, 55.78m), (line.FringeDifference, line.OverheadDifference, line.GeneralAndAdministrativeDifference));
        Assert.Equal(340.18m, report.Difference);
        Assert.Equal(kit.ClinId, line.ClinId);
        Assert.Equal(340.18m, Assert.Single(report.Contracts).Difference);
        Assert.Equal(kit.Contract.ContractNumber, report.Contracts[0].Code);
        Assert.Equal(340.18m, Assert.Single(report.Projects).Difference);

        var posted = await kit.Rates.PostTrueUpAsync(2027, new PostTrueUpRequest());
        Assert.Equal(340.18m, posted.Posted!.Total);
        Assert.Equal(new DateOnly(2027, 9, 30), posted.Posted.Date);
        Assert.Equal(330m, await kit.Gl.BalanceAsync("5910"));
        Assert.Equal(904.40m, await kit.Gl.BalanceAsync("5920"));

        // Re-posting reverses the first true-up.
        await kit.Rates.PostTrueUpAsync(2027, new PostTrueUpRequest());
        Assert.Equal(330m, await kit.Gl.BalanceAsync("5910"));
        Assert.True((await kit.Gl.Reports.GetTrialBalanceAsync(kit.Periods["2027-09"].Id)).IsBalanced);
    }
}

public sealed class BurdenedCostServiceTests
{
    [Fact]
    public async Task Burdened_cost_per_wbs_with_clin_and_each_years_rates()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();
        await kit.SetRatesAsync(2026, RateBasis.Provisional, 0.10m, 0.10m, 0.10m);
        await kit.AddTimeAsync((kit.Task1, new DateOnly(2026, 9, 14), 10m)); // FY2026: fringe 100, overhead 110, G&A 121

        var result = await kit.Burdened.GetProjectCostAsync(kit.Project.Id, new DateOnly(2026, 9, 1), kit.Oct.End, RateBasis.Provisional);

        var line = Assert.Single(result.Lines);
        Assert.Equal((kit.Project.Id, kit.Task1.Id, "2001.01", (Guid?)kit.ClinId), (line.ProjectId, line.WbsElementId, line.WbsCode, line.ClinId));
        Assert.Equal((20m, 2000m, 500m, 200m), (line.DirectLaborHours, line.DirectLabor, line.DirectOdc, line.UnallowableExcluded));
        Assert.Equal((400m, 760m, 366m), (line.Fringe, line.Overhead, line.GeneralAndAdministrative));
        Assert.Equal(2000m + 400m + 760m + 366m + 500m, line.TotalCost);
        Assert.Equal(
            [new AppliedRates(RateBasis.Provisional, 2026, 0.10m, 0.10m, 0.10m), new AppliedRates(RateBasis.Provisional, 2027, 0.30m, 0.50m, 0.10m)],
            result.Rates);
    }

    [Fact]
    public async Task Indirect_work_and_unapproved_time_are_not_direct_cost()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        await kit.AddTimeAsync((kit.Overhead, IndirectRatesKit.October.AddDays(5), 8m));
        await using (var db = await kit.Factory.CreateAsync())
        {
            var draft = new Workbench.Domain.ProjectAccounting.Timesheet { EmployeeId = kit.Employee.Id, PeriodStart = IndirectRatesKit.October, PeriodEnd = IndirectRatesKit.October.AddDays(6) };
            draft.Entries.Add(new() { TimesheetId = draft.Id, WbsElementId = kit.Task1.Id, WorkDate = IndirectRatesKit.October, Hours = 8, LaborCategoryId = kit.Category.Id });
            db.Set<Workbench.Domain.ProjectAccounting.Timesheet>().Add(draft);
            await db.SaveChangesAsync();
        }

        Assert.Empty((await kit.Burdened.GetProjectCostAsync(kit.Project.Id, kit.Oct.Start, kit.Oct.End, RateBasis.Provisional)).Lines);
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchNotFoundException>(() => kit.Burdened.GetProjectCostAsync(Guid.NewGuid(), kit.Oct.Start, kit.Oct.End, RateBasis.Provisional));
    }

    [Fact]
    public async Task Burdened_financials_add_burden_to_actuals_and_eac()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync();

        var result = await new BurdenedFinancialsService(kit.Factory, kit.Clock).GetProjectAsync(kit.Project.Id);
        Assert.True(result.HasRates);
        Assert.Equal(1500m, result.Totals.DirectActual);
        Assert.Equal(1195m, result.Totals.Burden);
        Assert.Equal(2695m, result.Totals.BurdenedActual);
        Assert.Equal(1195m, result.Rows.Single(r => r.WbsElementId == kit.Task1.Id).Figures.Burden);
    }
}
