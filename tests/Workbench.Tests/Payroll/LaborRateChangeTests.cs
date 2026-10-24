using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Financials;
using Workbench.Application.Payroll;
using Workbench.Application.TotalTime;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Hr;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Payroll;

/// <summary>
/// Labor cost follows pay rates by work date: a pay change writes the matching cost rate from its date, posting costs each
/// day at the rate in effect on it, and a back-dated change re-costs labor already posted (into the open period when the
/// work period is closed). viewer@ (Val) is hourly 30 with 4 approved hours on Wed 2026-10-14 against 2002.01 (direct).
/// </summary>
public sealed class LaborRateChangeTests
{
    private static readonly DateOnly October1 = new(2026, 10, 1);
    private static readonly DateOnly WeekOf = new(2026, 10, 12);

    [Fact]
    public async Task A_pay_rate_sets_the_cost_rate_by_date_and_lookups_use_the_rate_on_the_work_date()
    {
        await using var kit = await PayrollKit.CreateAsync();

        // A first profile fills the gap (Val had no cost rate); it doesn't replace analyst@'s cost rate on file (87.50).
        var rates = await CostRatesAsync(kit, kit.Fx.ViewerId);
        Assert.Equal([(new DateOnly(2024, 1, 1), 30m)], rates);
        Assert.Equal([(new DateOnly(2024, 1, 1), 87.5m)], await CostRatesAsync(kit, kit.Fx.AnalystId));
        Assert.Equal([(new DateOnly(2024, 1, 1), 50m)], await CostRatesAsync(kit, kit.Fx.PmId)); // 104,000 / 2,080

        // A raise from a later date adds a row; a correction for the same date replaces it.
        await RaiseAsync(kit, kit.Fx.ViewerId, 33m, new DateOnly(2026, 11, 1));
        await RaiseAsync(kit, kit.Fx.ViewerId, 34m, new DateOnly(2026, 11, 1));
        Assert.Equal([(new DateOnly(2024, 1, 1), 30m), (new DateOnly(2026, 11, 1), 34m)], await CostRatesAsync(kit, kit.Fx.ViewerId));

        await using var db = await kit.Factory.CreateAsync();
        var table = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => r.EmployeeId == kit.Fx.ViewerId).ToListAsync())
        {
            table.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        Assert.Equal(30m, table.Find(kit.Fx.ViewerId, new DateOnly(2026, 10, 31)));
        Assert.Equal(34m, table.Find(kit.Fx.ViewerId, new DateOnly(2026, 11, 1)));
        Assert.Null(table.Find(kit.Fx.ViewerId, new DateOnly(2023, 12, 31)));

        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstAsync(p => p.EmployeeId == kit.Fx.ViewerId);
        var history = await PayRates.HistoryAsync(db, profile, CancellationToken.None);
        Assert.Equal(30m, PayRates.InEffect(history, new DateOnly(2026, 10, 31))!.HourlyRate);
        Assert.Equal(34m, PayRates.InEffect(history, new DateOnly(2026, 11, 1))!.HourlyRate);
    }

    [Fact]
    public async Task Posting_a_week_with_a_mid_week_raise_costs_each_day_at_its_rate()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var monday = new DateOnly(2026, 10, 19);
        await kit.AddApprovedHoursAsync(kit.Fx.ViewerId, kit.Fx.Wbs1, (monday, 4m), (monday.AddDays(3), 4m));
        await RaiseAsync(kit, kit.Fx.ViewerId, 40m, monday.AddDays(2)); // from Wednesday: nothing posted yet, so nothing to adjust

        var sheet = await SheetAsync(kit, kit.Fx.ViewerId, monday);
        var outcome = await kit.Get<PostingEngine>().PostTimesheetAsync(sheet);
        Assert.Empty(outcome.Problems);
        var entry = Assert.Single(await EntriesAsync(kit, JournalSource.Timesheet, sheet));
        Assert.Equal(4m * 30m + 4m * 40m, entry.Lines.Sum(l => l.Debit));

        var book = await LaborCostBook.LoadAsync(await kit.Factory.CreateAsync(), [sheet], CancellationToken.None);
        Assert.Equal(280m, book.Sheets[sheet].Cost.LaborCost);
    }

    [Fact]
    public async Task A_back_dated_raise_adjusts_posted_labor_in_its_open_period()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var sheet = await PostViewerWeekAsync(kit);

        var saved = await kit.Profiles.SaveAsync(kit.Fx.ViewerId,
            new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, HourlyRate: 33m, EffectiveFrom: October1, Reason: "Merit, back-dated"));

        var adjustment = Assert.Single(saved.LaborAdjustments!);
        Assert.Equal(12m, adjustment.Amount); // 4 h × (33 − 30)
        Assert.False(adjustment.Redirected);
        Assert.Equal(["2026-10"], adjustment.PostedPeriods);
        Assert.Null(adjustment.Problem);

        var entry = Assert.Single(await EntriesAsync(kit, JournalSource.LaborAdjustment, adjustment.Id!.Value));
        Assert.Equal(JournalStatus.Posted, entry.Status);
        Assert.Equal(new DateOnly(2026, 10, 18), entry.Date);
        var accounts = await kit.AccountNumbersAsync();
        Assert.Equal(12m, entry.Lines.Where(l => accounts[l.AccountId] == "5010").Sum(l => l.Debit - l.Credit));
        Assert.Equal(12m, entry.Lines.Where(l => l.WbsElementId != null).Sum(l => l.Debit));
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));

        // GL labor for the sheet now equals what the hours cost at today's rates.
        Assert.Equal(132m, await PostedLaborAsync(kit, sheet));

        // Adjusting again finds nothing more to do.
        var again = await kit.Get<LaborRateChanges>().AdjustPostedLaborAsync(kit.Fx.ViewerId, October1, "again");
        Assert.Empty(again);

        // Listed on the profile, and audited.
        Assert.Equal(adjustment.Id, Assert.Single(await kit.Profiles.ListLaborAdjustmentsAsync(kit.Fx.ViewerId)).Id);
        await using var db = await kit.Factory.CreateAsync();
        var audit = Assert.Single(await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == PostingEngine.LaborAdjustmentAuditType).ToListAsync());
        Assert.Equal("Labor cost adjusted", audit.Action);
        Assert.Contains("Merit, back-dated", audit.Reason);
    }

    [Fact]
    public async Task A_back_dated_raise_into_a_closed_period_posts_to_the_open_period_and_says_so()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var sheet = await PostViewerWeekAsync(kit);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var october = await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == "2026-10");
            october.Status = PeriodStatus.Closed;
            db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = october.FiscalYearId, Name = "2026-11", Number = 2, Start = new DateOnly(2026, 11, 1), End = new DateOnly(2026, 11, 30) });
            await db.SaveChangesAsync();
        }

        var saved = await kit.Profiles.SaveAsync(kit.Fx.ViewerId,
            new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, HourlyRate: 35m, EffectiveFrom: October1, Reason: "Retro raise"));
        var adjustment = Assert.Single(saved.LaborAdjustments!);
        Assert.Equal(20m, adjustment.Amount);
        Assert.True(adjustment.Redirected);
        Assert.Equal("2026-10", adjustment.WorkPeriods);
        Assert.Equal(["2026-11"], adjustment.PostedPeriods);

        var entry = Assert.Single(await EntriesAsync(kit, JournalSource.LaborAdjustment, adjustment.Id!.Value));
        Assert.Equal(new DateOnly(2026, 11, 1), entry.Date);
        Assert.Contains("closed period 2026-10", entry.Description);

        await using var read = await kit.Factory.CreateAsync();
        var audit = Assert.Single(await read.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == PostingEngine.LaborAdjustmentAuditType).ToListAsync());
        Assert.Contains("\"redirected\":true", audit.AfterJson);
        Assert.Contains("closed period", audit.Reason);

        // A second back-dated change counts the first adjustment as posted: only the new difference goes.
        var second = await kit.Profiles.SaveAsync(kit.Fx.ViewerId,
            new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, HourlyRate: 36m, EffectiveFrom: October1, Reason: "Correction"));
        Assert.Equal(4m, Assert.Single(second.LaborAdjustments!).Amount);
        Assert.Equal(144m, await PostedLaborAsync(kit, sheet));
    }

    [Fact]
    public async Task Reopening_the_timesheet_reverses_its_adjustments_with_it()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var sheet = await PostViewerWeekAsync(kit);
        var saved = await kit.Profiles.SaveAsync(kit.Fx.ViewerId,
            new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, HourlyRate: 33m, EffectiveFrom: October1, Reason: "Merit"));
        var adjustmentId = Assert.Single(saved.LaborAdjustments!).Id!.Value;

        // An adjustment follows its timesheet: the journal won't reverse it by hand.
        var adjustmentEntry = Assert.Single(await EntriesAsync(kit, JournalSource.LaborAdjustment, adjustmentId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Get<Contracts.Accounting.IJournalService>()
            .ReverseAsync(adjustmentEntry.Id, new Contracts.Accounting.ReverseJournalEntryRequest("Undo")));

        var reversed = await kit.Get<PostingEngine>().ReverseSourceAsync(JournalSource.Timesheet, sheet, "Timesheet reopened");
        Assert.Equal(2, reversed.Reversed);
        Assert.Equal(JournalStatus.Reversed, Assert.Single(await EntriesAsync(kit, JournalSource.LaborAdjustment, adjustmentId)).Status);
        Assert.Equal(0m, await PostedLaborAsync(kit, sheet));
    }

    [Fact]
    public async Task An_approved_back_dated_compensation_change_writes_the_rate_and_adjusts_labor()
    {
        await using var kit = await HrKit.CreateAsync();
        var sheet = await PostViewerWeekAsync(kit.Payroll);

        var change = await kit.As(HrKit.Hr).Compensation.ProposeAsync(kit.Fx.ViewerId,
            new ProposeCompensationRequest(October1, PayTypeKind.Hourly, null, 31.5m, CompensationReason.Merit, "Merit cycle"));
        var applied = await kit.As(Admin).Compensation.ApproveAsync(change.Id, new CompensationDecisionRequest(null));
        Assert.Equal(CompensationChangeStatus.Applied, applied.Status);

        await using var db = await kit.Factory.CreateAsync();
        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstAsync(p => p.EmployeeId == kit.Fx.ViewerId);
        Assert.Equal(31.5m, PayRates.InEffect(await PayRates.HistoryAsync(db, profile, CancellationToken.None), October1)!.HourlyRate);
        Assert.Contains((October1, 31.5m), await CostRatesAsync(kit.Payroll, kit.Fx.ViewerId));

        var adjustment = Assert.Single(await db.Set<LaborCostAdjustment>().AsNoTracking().Where(a => a.EmployeeId == kit.Fx.ViewerId).ToListAsync());
        Assert.Equal(6m, adjustment.Amount); // 4 h × 1.50
        Assert.Contains("Merit", adjustment.Reason);
        Assert.Equal(126m, await PostedLaborAsync(kit.Payroll, sheet));
    }

    [Fact]
    public async Task A_back_dated_cost_rate_from_setup_adjusts_posted_labor_too()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var sheet = await PostViewerWeekAsync(kit);
        await kit.Get<Contracts.ProjectAccounting.IPeopleService>().AddCostRateAsync(kit.Fx.ViewerId,
            new Contracts.ProjectAccounting.AddCostRateRequest(October1, 32m, "Rate audit correction"));
        Assert.Equal(128m, await PostedLaborAsync(kit, sheet));
    }

    [Fact]
    public async Task Reconciliation_prorates_a_raise_inside_the_pay_period()
    {
        await using var kit = await PayrollKit.CreateAsync();

        // Val: 4 h on Wed 10-14 (fixture) plus 4 h on Thu 10-08; the raise to 40 starts Mon 10-12, inside the period.
        await kit.AddApprovedHoursAsync(kit.Fx.ViewerId, kit.Fx.Wbs1, (new DateOnly(2026, 10, 8), 4m));
        await RaiseAsync(kit, kit.Fx.ViewerId, 40m, new DateOnly(2026, 10, 12));

        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("r.csv", """
            Employee Id,Employee Name,Check Date,Gross Pay,EE Taxes,ER Taxes,Deductions,ER Benefits,Net Pay
            3003,Val Chen,10/23/2026,280.00,40.00,21.42,0.00,0.00,240.00
            """));
        var recon = await kit.Runs.GetReconciliationAsync(run.Id);
        var val = recon.Rows.Single(r => r.EmployeeId == kit.Fx.ViewerId);
        Assert.Equal(4m * 30m + 4m * 40m, val.WorkedAtPayRate);
        Assert.Equal(0m, val.Unexplained);
        Assert.Equal(280m, val.AccruedLaborCost); // the cost book uses the same dated rates
        Assert.Contains(val.Notes, n => n.Contains("rate in effect on their work date"));
    }

    [Fact]
    public void A_salaried_raise_inside_the_period_prorates_the_salary_by_working_days()
    {
        // Biweekly Mon 10-05 to Sun 10-18: 10 working days, the raise from Mon 10-12 covers 5 of them.
        var hours = new HoursSplit(0, 0, new Dictionary<string, decimal>());
        var line = PayrollReconciliation.Explain(
        [
            (new PayBasis(PayType.Salaried, 104_000m, null, 26, 0.5m), hours),
            (new PayBasis(PayType.Salaried, 114_400m, null, 26, 0.5m), hours),
        ], 4_200m);
        Assert.Equal(2_000m + 2_200m, line.ExpectedGross);
        Assert.Equal(0m, line.Unexplained);
        Assert.Equal(5, PayrollReconciliation.WorkingDays(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18)));
    }

    // ---- helpers ----

    private static async Task<Guid> PostViewerWeekAsync(PayrollKit kit)
    {
        var sheet = await SheetAsync(kit, kit.Fx.ViewerId, WeekOf);
        var outcome = await kit.Get<PostingEngine>().PostTimesheetAsync(sheet);
        Assert.Empty(outcome.Problems);
        Assert.Equal(120m, await PostedLaborAsync(kit, sheet)); // 4 h × 30
        return sheet;
    }

    private static Task<PayProfileDto> RaiseAsync(PayrollKit kit, Guid employeeId, decimal hourly, DateOnly from) =>
        kit.Profiles.SaveAsync(employeeId, new SavePayProfileRequest(kit.Group.Id, employeeId == kit.Fx.ViewerId ? "3003" : "2002", PayTypeKind.Hourly,
            HourlyRate: hourly, EffectiveFrom: from, Reason: "Raise"));

    private static async Task<Guid> SheetAsync(PayrollKit kit, Guid employeeId, DateOnly weekStart)
    {
        await using var db = await kit.Factory.CreateAsync();
        return await db.Set<Timesheet>().Where(t => t.EmployeeId == employeeId && t.PeriodStart == weekStart).Select(t => t.Id).SingleAsync();
    }

    private static async Task<List<(DateOnly, decimal)>> CostRatesAsync(PayrollKit kit, Guid employeeId)
    {
        await using var db = await kit.Factory.CreateAsync();
        return (await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => r.EmployeeId == employeeId).ToListAsync())
            .OrderBy(r => r.EffectiveFrom).Select(r => (r.EffectiveFrom, r.HourlyCost)).ToList();
    }

    private static async Task<List<JournalEntry>> EntriesAsync(PayrollKit kit, JournalSource source, Guid sourceId)
    {
        await using var db = await kit.Factory.CreateAsync();
        return await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).Where(e => e.Source == source && e.SourceId == sourceId).ToListAsync();
    }

    /// <summary>Labor cost posted for a timesheet: its entries and its adjustments, net of reversals.</summary>
    private static async Task<decimal> PostedLaborAsync(PayrollKit kit, Guid sheetId)
    {
        await using var db = await kit.Factory.CreateAsync();
        var adjustments = await db.Set<LaborCostAdjustment>().AsNoTracking().Where(a => a.TimesheetId == sheetId).Select(a => a.Id).ToListAsync();
        var entries = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => e.Status == JournalStatus.Posted
                && ((e.Source == JournalSource.Timesheet && e.SourceId == sheetId) || (e.Source == JournalSource.LaborAdjustment && adjustments.Contains(e.SourceId!.Value))))
            .ToListAsync();
        return entries.SelectMany(e => e.Lines).Where(l => l.WbsElementId != null).Sum(l => l.Debit - l.Credit);
    }
}
