using Microsoft.EntityFrameworkCore;
using Workbench.Application.IndirectRates;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;

namespace Workbench.Tests.Planning;

public sealed class GlBudgetTests
{
    private static async Task<(PlanningKit Kit, BudgetVersionDto Version)> VersionAsync(int year = 2027)
    {
        var kit = await PlanningKit.CreateAsync();
        return (kit, await kit.Budgets.CreateVersionAsync(new CreateBudgetVersionRequest(year, BudgetVersionKind.Original, $"FY{year} Original")));
    }

    [Fact]
    public async Task Spreading_evenly_adds_up_to_the_annual_amount_to_the_cent()
    {
        var (kit, version) = await VersionAsync();
        var grid = await kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["6120"], kit.Ops, 1000m, SpreadMethod.Evenly));
        var row = Assert.Single(grid.Rows);
        Assert.Equal(12, row.Amounts.Count);
        Assert.Equal(1000m, row.Total);
        Assert.Equal([83.34m, 83.34m, 83.34m, 83.34m, 83.33m, 83.33m, 83.33m, 83.33m, 83.33m, 83.33m, 83.33m, 83.33m], row.Amounts);
        Assert.Equal("2026-10", grid.Periods[0].Name);
        Assert.Equal(1000m, grid.Version.Expense);
    }

    [Fact]
    public async Task Profiles_and_the_prior_years_pattern_add_up_exactly()
    {
        var (kit, version) = await VersionAsync();
        var profile = new decimal[] { 10, 10, 10, 5, 5, 5, 5, 10, 10, 10, 10, 10 };
        var grid = await kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["4010"], kit.Eng, 100_000.01m, SpreadMethod.Profile, profile));
        Assert.Equal(100_000.01m, grid.Rows.Single().Total);
        Assert.Equal(10_000.01m, grid.Rows.Single().Amounts[0]);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["4010"], kit.Eng, 1000m, SpreadMethod.Profile, [50, 40])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["6120"], kit.Ops, 1200m, SpreadMethod.PriorYearPattern)));

        // FY2026 actuals: $100 in October, $300 in November (and another unit's cost, which doesn't count).
        await kit.PostAsync(new DateOnly(2025, 10, 10), ("6120", kit.Ops, 100m, 0), ("1010", null, 0, 100m));
        await kit.PostAsync(new DateOnly(2025, 11, 10), ("6120", kit.Ops, 300m, 0), ("6120", kit.Eng, 999m, 0), ("1010", null, 0, 1299m));
        grid = await kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["6120"], kit.Ops, 1201m, SpreadMethod.PriorYearPattern));
        var rent = grid.Rows.Single(r => r.AccountNumber == "6120");
        Assert.Equal(1201m, rent.Total);
        Assert.Equal((300.25m, 900.75m, 0m), (rent.Amounts[0], rent.Amounts[1], rent.Amounts[2]));
    }

    [Fact]
    public async Task An_adopted_version_refuses_every_edit()
    {
        var (kit, version) = await VersionAsync();
        await kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["6120"], kit.Ops, 1200m, SpreadMethod.Evenly));
        var adopted = await kit.Budgets.AdoptAsync(version.Id);
        Assert.True(adopted.IsLocked);
        var period = kit.Periods["2026-10"].Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Budgets.SaveCellsAsync(version.Id, new SaveBudgetCellsRequest([new BudgetCellInput(kit["6120"], kit.Ops, period, 5m)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit["6120"], kit.Ops, 1m, SpreadMethod.Evenly)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Budgets.ImportCsvAsync(version.Id, new ImportBudgetCsvRequest("account,org,annual\n6120,OPS,10")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Budgets.AdoptAsync(version.Id));
        Assert.Equal(1200m, (await kit.Budgets.GetGridAsync(version.Id)).Rows.Single().Total);

        // A revision starts from the adopted lines and can change.
        var revised = await kit.Budgets.CreateVersionAsync(new CreateBudgetVersionRequest(2027, BudgetVersionKind.Revised, "FY2027 Revised", CopyFromVersionId: version.Id));
        Assert.Equal((1200m, "FY2027 Original"), (revised.Expense, revised.SourceVersionName));
        await kit.Budgets.SaveCellsAsync(revised.Id, new SaveBudgetCellsRequest([new BudgetCellInput(kit["6120"], kit.Ops, period, 0m)]));
        Assert.Equal(1100m, (await kit.Budgets.GetGridAsync(revised.Id)).Rows.Single().Total);
    }

    [Fact]
    public async Task Csv_imports_all_or_nothing()
    {
        var (kit, version) = await VersionAsync();
        var bad = await kit.Budgets.ImportCsvAsync(version.Id, new ImportBudgetCsvRequest("account,org,period,amount\n6120,OPS,2026-10,100\n9999,OPS,2026-11,5\n6120,XYZ,1,5\n6120,OPS,2030-01,5\n6120,OPS,2,1.234"));
        Assert.False(bad.Succeeded);
        Assert.Equal(4, bad.Errors.Count);
        Assert.Contains("Row 3", bad.Errors[0]);
        Assert.Empty((await kit.Budgets.GetGridAsync(version.Id)).Rows);

        var good = await kit.Budgets.ImportCsvAsync(version.Id, new ImportBudgetCsvRequest("﻿Account,Org,Period,Amount\r\n6120,ops,2026-10,\"1,000.50\"\r\n6120,OPS,2,(25.00)\r\n4010,ENG,12,500\r\n"));
        Assert.True(good.Succeeded);
        var grid = await kit.Budgets.GetGridAsync(version.Id);
        var rent = grid.Rows.Single(r => r.AccountNumber == "6120");
        Assert.Equal((1000.50m, -25m), (rent.Amounts[0], rent.Amounts[1]));
        Assert.Equal(500m, grid.Rows.Single(r => r.AccountNumber == "4010").Amounts[11]);

        var annual = await kit.Budgets.ImportCsvAsync(version.Id, new ImportBudgetCsvRequest("account,org,annual\n6230,OPS,1200", Replace: true));
        Assert.True(annual.Succeeded);
        Assert.Equal(12, annual.CellsSet);
        Assert.Equal("6230", Assert.Single((await kit.Budgets.GetGridAsync(version.Id)).Rows).AccountNumber);

        // Balance sheet accounts aren't budgeted.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Budgets.SaveCellsAsync(version.Id,
            new SaveBudgetCellsRequest([new BudgetCellInput(kit["1010"], kit.Ops, kit.Periods["2026-10"].Id, 5m)])));
    }

    [Fact]
    public async Task Budget_vs_actual_shows_variances_for_the_period_and_the_year_to_date()
    {
        var (kit, version) = await VersionAsync();
        var (oct, nov) = (kit.Periods["2026-10"].Id, kit.Periods["2026-11"].Id);
        await kit.Budgets.SaveCellsAsync(version.Id, new SaveBudgetCellsRequest([
            new BudgetCellInput(kit["6120"], kit.Ops, oct, 1000m), new BudgetCellInput(kit["6120"], kit.Ops, nov, 1000m),
            new BudgetCellInput(kit["4010"], kit.Eng, oct, 5000m),
        ]));
        await kit.PostAsync(new DateOnly(2026, 10, 5), ("6120", kit.Ops, 1200m, 0), ("1010", null, 0, 1200m));
        await kit.PostAsync(new DateOnly(2026, 10, 30), ("1110", null, 5500m, 0), ("4010", kit.Eng, 0, 5500m));

        var october = await kit.Reports.GetIncomeStatementAsync(new BudgetVsActualQuery(version.Id, oct));
        var rent = october.Lines.Single(l => l.Code == "6120").Figures;
        Assert.Equal((1000m, 1200m, -200m, -20.0m), (rent.PeriodBudget, rent.PeriodActual, rent.PeriodVariance, rent.PeriodVariancePercent));
        Assert.Equal(2000m, rent.AnnualBudget);
        var revenue = october.Lines.Single(l => l.Label == "Total revenue").Figures;
        Assert.Equal((500m, 10.0m), (revenue.PeriodVariance, revenue.PeriodVariancePercent));
        var net = october.Lines.Single(l => l.Label == "Net income").Figures;
        Assert.Equal((4000m, 4300m, 300m), (net.PeriodBudget, net.PeriodActual, net.PeriodVariance));

        var november = await kit.Reports.GetIncomeStatementAsync(new BudgetVsActualQuery(version.Id, nov));
        var ytd = november.Lines.Single(l => l.Code == "6120").Figures;
        Assert.Equal((1000m, 0m, 2000m, 1200m, 800m), (ytd.PeriodBudget, ytd.PeriodActual, ytd.YtdBudget, ytd.YtdActual, ytd.YtdVariance));

        var byOrg = await kit.Reports.GetByOrgUnitAsync(new BudgetVsActualQuery(version.Id, oct));
        Assert.Equal(1200m, byOrg.Lines.Single(l => l.Code == "OPS" && l.Label == "Expenses").Figures.PeriodActual);
        Assert.Equal(5500m, byOrg.Lines.Single(l => l.Code == "ENG" && l.Label == "Revenue").Figures.PeriodActual);

        var opsOnly = await kit.Reports.GetIncomeStatementAsync(new BudgetVsActualQuery(version.Id, oct, kit.Ops));
        Assert.Equal(0m, opsOnly.Lines.Single(l => l.Label == "Total revenue").Figures.PeriodActual);

        var csv = await kit.Reports.ExportCsvAsync(BudgetReport.IncomeStatement, new BudgetVsActualQuery(version.Id, oct));
        Assert.Contains("6120,Facilities and rent,1000.00,1200.00,-200.00,-20.0,1000.00,1200.00,-200.00,-20.0,2000.00", csv.Content);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Reports.GetIncomeStatementAsync(new BudgetVsActualQuery(version.Id, kit.Periods["2025-10"].Id)));
    }

    [Fact]
    public async Task Pool_budgets_give_budgeted_rates_next_to_provisional_and_actual()
    {
        var (kit, version) = await VersionAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            await DefaultPools.EnsureAsync(db, "test", DateTime.UtcNow, CancellationToken.None);
            db.Set<IndirectRate>().AddRange(
                new IndirectRate { Pool = IndirectPool.Fringe, FiscalYear = 2027, Basis = IndirectRateBasis.Provisional, Rate = 0.32m, EffectiveFrom = new DateOnly(2026, 10, 1), CreatedBy = "t" },
                new IndirectRate { Pool = IndirectPool.Overhead, FiscalYear = 2027, Basis = IndirectRateBasis.Provisional, Rate = 0.18m, EffectiveFrom = new DateOnly(2026, 10, 1), CreatedBy = "t" });
            await db.SaveChangesAsync();
        }

        foreach (var (account, annual) in new[] { ("5010", 10_000m), ("6020", 3_000m), ("6120", 2_600m), ("6220", 1_560m) })
        {
            await kit.Budgets.SpreadAsync(version.Id, new SpreadBudgetRequest(kit[account], kit.Ops, annual, SpreadMethod.Evenly));
        }

        // Actual so far: $1,000 direct labor, $400 fringe.
        await kit.PostAsync(new DateOnly(2026, 10, 31), ("5010", kit.Eng, 1000m, 0), ("6020", kit.Eng, 400m, 0), ("2100", null, 0, 1400m));

        var rates = await kit.Reports.GetBudgetedRatesAsync(2027);
        Assert.Equal(version.Id, rates.VersionId);
        var fringe = rates.Pools.Single(p => p.Pool == CostPool.Fringe);
        Assert.Equal((0.3m, 0.32m, 0.4m), (fringe.BudgetedRate, fringe.ProvisionalRate, fringe.ActualRate));
        Assert.Equal(-0.02m, fringe.BudgetedVsProvisional);
        Assert.Equal(0.2m, rates.Pools.Single(p => p.Pool == CostPool.Overhead).BudgetedRate);
        var ga = rates.Pools.Single(p => p.Pool == CostPool.GeneralAndAdministrative);
        Assert.Equal((15_600m, 0.1m, (decimal?)null), (ga.BudgetedBase, ga.BudgetedRate, ga.ProvisionalRate));

        var pools = await kit.Reports.GetByPoolAsync(new BudgetVsActualQuery(version.Id, kit.Periods["2026-10"].Id));
        Assert.Equal(250m, pools.Lines.Single(l => l.Label == "Total Fringe pool").Figures.PeriodBudget);
        Assert.Equal(400m, pools.Lines.Single(l => l.Label == "Total Fringe pool").Figures.PeriodActual);
        Assert.Equal(1000m, pools.Lines.Single(l => l.Label == "Direct labor").Figures.PeriodActual);
    }
}
