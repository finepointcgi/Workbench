using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Odcs;

namespace Workbench.Tests.Accounting;

public sealed class ReportTests
{
    [Fact]
    public async Task Trial_balance_balances_and_carries_balances_into_the_next_period()
    {
        var host = await LaborPostingTests.CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(host);
        var gl = host.Gl;
        await gl.Journal.CreateAndPostAsync(gl.Entry(new DateOnly(2026, 10, 31), ("1120", 2000m, 0), ("4010", 0, 2000m)));
        await gl.Journal.CreateAndPostAsync(gl.Entry(new DateOnly(2026, 11, 5), ("2100", 1360m, 0), ("1010", 0, 1360m)));
        var october = host.Time.Periods.Single(p => p.Name == "2026-10").Id;
        var november = host.Time.Periods.Single(p => p.Name == "2026-11").Id;

        var oct = await gl.Reports.GetTrialBalanceAsync(october);
        Assert.True(oct.IsBalanced);
        Assert.Equal(3360m, oct.TotalPeriodDebit);
        Assert.Equal(1360m, oct.Rows.Single(r => r.AccountNumber == "5010").EndingDebit);
        Assert.Equal(1360m, oct.Rows.Single(r => r.AccountNumber == "2100").EndingCredit);

        var nov = await gl.Reports.GetTrialBalanceAsync(november);
        Assert.True(nov.IsBalanced);
        var payroll = nov.Rows.Single(r => r.AccountNumber == "2100");
        Assert.Equal(-1360m, payroll.Opening);
        Assert.Equal(0m, payroll.Ending);
        Assert.Equal(1360m, nov.Rows.Single(r => r.AccountNumber == "5010").Opening); // same fiscal year: P&L carries
        Assert.Equal(-1360m, nov.Rows.Single(r => r.AccountNumber == "1010").Ending);

        var income = await gl.Reports.GetIncomeStatementAsync(november);
        var net = income.Lines.Single(l => l.Label == "Net income");
        Assert.Equal(0m, net.Period);
        Assert.Equal(640m, net.YearToDate);

        var balance = await gl.Reports.GetBalanceSheetAsync(november);
        Assert.Equal(balance.Lines.Single(l => l.Label == "Total assets").YearToDate, balance.Lines.Single(l => l.Label == "Total liabilities and equity").YearToDate);
        Assert.Equal(640m, balance.Lines.Single(l => l.Label == "Total assets").YearToDate);

        var csv = await gl.Reports.ExportCsvAsync(new AccountingExportRequest(AccountingReport.TrialBalance, october));
        Assert.Equal("trial-balance-2026-10.csv", csv.FileName);
        Assert.Contains("5010,Direct labor,Expense,0.00,1360.00,0.00,1360.00,0.00", csv.Content);
        foreach (var report in Enum.GetValues<AccountingReport>())
        {
            Assert.NotEmpty((await gl.Reports.ExportCsvAsync(new AccountingExportRequest(report, october))).Content);
        }
    }

    [Fact]
    public async Task Prior_year_income_closes_into_retained_earnings()
    {
        var host = await OdcTestHost.CreateAsync();
        host.As("finance@example.com");
        var gl = await LedgerKit.CreateAsync(host.Factory, host.User, TimeProvider.System);
        await SetPeriodAsync(host, "2026-09", PeriodStatus.Open);
        await gl.Journal.CreateAndPostAsync(gl.Entry(OdcTestHost.ClosedDay, ("1110", 1000m, 0), ("4020", 0, 1000m)));
        await SetPeriodAsync(host, "2026-09", PeriodStatus.Closed);
        var october = await PeriodIdAsync(host, "2026-10");

        var tb = await gl.Reports.GetTrialBalanceAsync(october);
        Assert.True(tb.IsBalanced);
        Assert.DoesNotContain(tb.Rows, r => r.AccountNumber == "4020");
        Assert.Equal(-1000m, tb.Rows.Single(r => r.AccountNumber == "3200").Opening);

        var balance = await gl.Reports.GetBalanceSheetAsync(october);
        Assert.Equal(1000m, balance.Lines.Single(l => l.Label == "Total assets").YearToDate);
        Assert.Equal(1000m, balance.Lines.Single(l => l.Label == "Total equity").YearToDate);
    }

    [Fact]
    public async Task Ledger_detail_filters_by_account_and_segments_with_a_running_balance()
    {
        var host = await OdcTestHost.CreateAsync();
        host.As("finance@example.com");
        var gl = await LedgerKit.CreateAsync(host.Factory, host.User, TimeProvider.System);
        await gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(OdcTestHost.Day, "Supplies",
            [new(gl["5190"], 30m, 0, WbsElementId: host.Leaf.Id), new(gl["5190"], 20m, 0, WbsElementId: host.UnassignedLeaf.Id), new(gl["2200"], 0, 50m)]));
        await gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(OdcTestHost.Day.AddDays(1), "More supplies",
            [new(gl["5190"], 5m, 0, WbsElementId: host.Leaf.Id), new(gl["2200"], 0, 5m)]));

        var byWbs = await gl.Reports.GetLedgerDetailAsync(new LedgerQuery(gl["5100"], WbsElementId: host.Leaf.Id)); // summary account includes 5190
        Assert.Equal([30m, 35m], byWbs.Lines.Select(l => l.RunningBalance));
        var fromSecondDay = await gl.Reports.GetLedgerDetailAsync(new LedgerQuery(gl["5190"], From: OdcTestHost.Day.AddDays(1)));
        Assert.Equal(50m, fromSecondDay.OpeningBalance);
        Assert.Equal(55m, fromSecondDay.ClosingBalance);
    }

    [Fact]
    public async Task Project_cost_ledger_reconciles_with_project_financials_and_flags_differences()
    {
        var host = await LaborPostingTests.CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(host);
        var gl = host.Gl;
        await using (var db = await host.Time.Factory.CreateAsync())
        {
            db.Set<OdcEntry>().Add(new OdcEntry
            {
                ProjectId = host.Time.Direct.ProjectId, WbsElementId = host.Time.Direct.Id, Type = OdcType.Travel, IncurredOn = new DateOnly(2026, 10, 14),
                Amount = 315.40m, Description = "Site visit", Vendor = "Airline", SubmittedBy = "pat@example.com", Status = OdcStatus.Approved,
            });
            await db.SaveChangesAsync();
        }

        await gl.Posting.PostBacklogAsync();
        var projectId = host.Time.Direct.ProjectId;
        var financials = await new ProjectFinancialsService(host.Time.Factory, host.Time.Clock).GetProjectSummaryAsync(projectId);
        var cost = Assert.Single(await gl.Reports.GetProjectCostLedgerAsync(projectId));
        Assert.Equal(1360m, cost.DirectLabor);
        Assert.Equal(315.40m, cost.DirectOdc);
        Assert.Equal(financials.Totals.ActualCost, cost.Allowable);

        var reconciled = await gl.Reports.GetReconciliationAsync();
        Assert.True(reconciled.IsReconciled);
        Assert.Equal(1675.40m, reconciled.Rows.Single().GlTotal);

        // A manual entry with project segments isn't in the financials: flagged.
        await gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(new DateOnly(2026, 10, 20), "Accrued travel",
            [new(gl["5110"], 12.50m, 0, WbsElementId: host.Time.Direct.Id), new(gl["2200"], 0, 12.50m)]));
        var flagged = await gl.Reports.GetReconciliationAsync();
        Assert.False(flagged.IsReconciled);
        Assert.Equal(12.50m, flagged.Rows.Single().Difference);
        Assert.NotEmpty(flagged.Notes);
    }

    private static async Task SetPeriodAsync(OdcTestHost host, string name, PeriodStatus status)
    {
        await using var db = await host.Factory.CreateAsync();
        (await db.Set<FiscalPeriod>().SingleAsync(p => p.Name == name)).Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> PeriodIdAsync(OdcTestHost host, string name)
    {
        await using var db = await host.Factory.CreateAsync();
        return await db.Set<FiscalPeriod>().Where(p => p.Name == name).Select(p => p.Id).SingleAsync();
    }
}
