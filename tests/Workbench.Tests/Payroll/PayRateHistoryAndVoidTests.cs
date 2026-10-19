using Microsoft.EntityFrameworkCore;
using Workbench.Application.Leave;
using Workbench.Application.Payroll;
using Workbench.Contracts;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Leave;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Payroll;

/// <summary>Effective-dated pay rates, and voiding a posted payroll run taking back the period's leave accrual.</summary>
public sealed class PayRateHistoryAndVoidTests
{
    private static readonly DateOnly NextPeriodStart = PayrollKit.PeriodStart.AddDays(14);

    // ---- Pay rate history ----

    [Fact]
    public async Task A_rate_change_is_a_new_dated_row_and_each_run_uses_the_rate_in_effect_for_its_period()
    {
        await using var kit = await PayrollKit.CreateAsync();
        await kit.Profiles.SaveAsync(kit.Fx.AnalystId, new SavePayProfileRequest(kit.Group.Id, "2002", PayTypeKind.Hourly, HourlyRate: 60m,
            EffectiveFrom: NextPeriodStart, Reason: "Annual raise"));

        var masked = await kit.Profiles.GetAsync(kit.Fx.AnalystId);
        Assert.Equal(2, masked.Rates!.Count);
        Assert.All(masked.Rates, r => Assert.Null(r.HourlyRate));
        var revealed = await kit.Profiles.RevealAsync(kit.Fx.AnalystId);
        Assert.Equal([(NextPeriodStart, 60m, "Annual raise"), (new DateOnly(2024, 1, 1), 50m, "Initial pay rate")],
            revealed.Rates!.Select(r => (r.EffectiveFrom, r.HourlyRate!.Value, r.Reason!)));

        // The period ending Oct 18 keeps the old rate; the next one gets the raise.
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        Assert.Equal(50m, (await kit.Runs.GetReconciliationAsync(run.Id)).Rows.Single(r => r.EmployeeId == kit.Fx.AnalystId).PayRate);
        var nextPeriod = (await kit.Groups.ListPeriodsAsync(kit.Group.Id, 2026)).Single(p => p.Start == NextPeriodStart);
        var next = await kit.Runs.StartAsync(kit.Group.Id, nextPeriod.Id);
        Assert.Equal(60m, (await kit.Runs.GetReconciliationAsync(next.Id)).Rows.Single(r => r.EmployeeId == kit.Fx.AnalystId).PayRate);

        // Audited without the amounts.
        await using var db = await kit.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().SingleAsync(a => a.EntityType == PayrollProfileService.AuditEntityType && a.Action == "Pay rate changed");
        Assert.Equal("Annual raise", audit.Reason);
        Assert.DoesNotContain("60", audit.AfterJson);
        Assert.Contains(NextPeriodStart.ToString("yyyy-MM-dd"), audit.AfterJson);
    }

    [Fact]
    public async Task A_rate_change_inside_a_period_is_noted_and_the_export_uses_the_pay_type_in_effect()
    {
        await using var kit = await PayrollKit.CreateAsync();
        // viewer@ becomes salaried from the next period: this period still exports their hourly REG hours.
        await kit.Profiles.SaveAsync(kit.Fx.ViewerId, new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Salaried, AnnualSalary: 70_000m, EffectiveFrom: NextPeriodStart));
        // analyst@ gets a raise in the middle of this period.
        await kit.Profiles.SaveAsync(kit.Fx.AnalystId, new SavePayProfileRequest(kit.Group.Id, "2002", PayTypeKind.Hourly, HourlyRate: 55m, EffectiveFrom: new DateOnly(2026, 10, 12)));

        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        var preview = await kit.Runs.PreviewHoursAsync(run.Id);
        Assert.Equal(PayTypeKind.Hourly, preview.Employees.Single(e => e.EmployeeId == kit.Fx.ViewerId).PayType);
        var file = await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        Assert.Contains("3003", file.Content);

        var analyst = (await kit.Runs.GetReconciliationAsync(run.Id)).Rows.Single(r => r.EmployeeId == kit.Fx.AnalystId);
        Assert.Equal(55m, analyst.PayRate);
        Assert.Contains(analyst.Notes, n => n.Contains("Pay rate changed Oct 12"));

        var nextPeriod = (await kit.Groups.ListPeriodsAsync(kit.Group.Id, 2026)).Single(p => p.Start == NextPeriodStart);
        var next = await kit.Runs.StartAsync(kit.Group.Id, nextPeriod.Id);
        Assert.Equal(PayTypeKind.Salaried, (await kit.Runs.PreviewHoursAsync(next.Id)).Employees.Single(e => e.EmployeeId == kit.Fx.ViewerId).PayType);
    }

    [Fact]
    public async Task Profiles_from_before_rate_history_read_as_one_rate_from_the_hire_date_and_are_backfilled_once()
    {
        await using var kit = await PayrollKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<EmployeePayRate>().RemoveRange(await db.Set<EmployeePayRate>().ToListAsync()); // as an older database has them
            await db.SaveChangesAsync();
        }

        var legacy = await kit.Profiles.RevealAsync(kit.Fx.AnalystId);
        var only = Assert.Single(legacy.Rates!);
        Assert.Equal((new DateOnly(2024, 1, 1), 50m, PayRates.BackfillReason), (only.EffectiveFrom, only.HourlyRate!.Value, only.Reason!));
        Assert.Equal(50m, legacy.HourlyRate);
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        Assert.Equal(50m, (await kit.Runs.GetReconciliationAsync(run.Id)).Rows.Single(r => r.EmployeeId == kit.Fx.AnalystId).PayRate);

        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(3, await PayRates.BackfillAsync(db, "system", DateTime.UtcNow, CancellationToken.None));
            Assert.Equal(0, await PayRates.BackfillAsync(db, "system", DateTime.UtcNow, CancellationToken.None));
        }

        // The first change on a profile that wasn't backfilled keeps its old rate as history.
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<EmployeePayRate>().RemoveRange(await db.Set<EmployeePayRate>().Where(r => r.EmployeeId == kit.Fx.ViewerId).ToListAsync());
            await db.SaveChangesAsync();
        }

        await kit.Profiles.SaveAsync(kit.Fx.ViewerId, new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, HourlyRate: 32m, EffectiveFrom: NextPeriodStart));
        Assert.Equal([32m, 30m], (await kit.Profiles.RevealAsync(kit.Fx.ViewerId)).Rates!.Select(r => r.HourlyRate!.Value));
    }

    [Fact]
    public async Task Rate_changes_are_checked_and_rate_history_is_finance_only()
    {
        await using var kit = await PayrollKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Profiles.SaveAsync(kit.Fx.AnalystId,
            new SavePayProfileRequest(kit.Group.Id, "2002", PayTypeKind.Hourly, HourlyRate: 60m, EffectiveFrom: new DateOnly(2023, 12, 31))));

        // Saving without a new rate adds no row.
        await kit.Profiles.SaveAsync(kit.Fx.AnalystId, new SavePayProfileRequest(kit.Group.Id, "2002-B", PayTypeKind.Hourly));
        Assert.Single((await kit.Profiles.GetAsync(kit.Fx.AnalystId)).Rates!);

        kit.As(Owner);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => kit.Profiles.GetAsync(kit.Fx.AnalystId));
        kit.As(Editor);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => kit.Profiles.RevealAsync(kit.Fx.AnalystId));
    }

    // ---- Voiding a posted run ----

    [Fact]
    public async Task Voiding_a_posted_run_reverses_its_leave_accrual_and_posting_again_accrues_again()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        kit.As(Admin);
        var sick = await kit.Policies.CreateAsync(new SaveLeavePolicyRequest("SICK", "Sick", LeaveTypeKind.Sick, kit.PtoWbs, AccrualMethodKind.AnnualGrant, [new(0, 40m)]));
        kit.As(Finance);

        var run = await PostedRunAsync(kit);
        Assert.Equal((4m, 40m), (await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id), await kit.LedgerHoursAsync(kit.Fx.AnalystId, sick.Id)));

        await kit.Runs.VoidAsync(run.Id, new VoidPayrollRequest("Wrong register"));
        Assert.Equal((0m, 0m), (await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id), await kit.LedgerHoursAsync(kit.Fx.AnalystId, sick.Id)));
        await using (var db = await kit.Factory.CreateAsync())
        {
            var reversals = await db.Set<LeaveLedgerEntry>().Where(l => l.Hours < 0).ToListAsync();
            Assert.Equal(6, reversals.Count); // PTO and Sick for pm@, analyst@ and viewer@
            Assert.All(reversals, r => Assert.Contains("voided (Wrong register)", r.Reason));
            var audit = await db.Set<AuditEvent>().SingleAsync(a => a.EntityType == "LeaveBalance" && a.Action == "Accrual reversed");
            Assert.Equal((kit.Period.Id, "Wrong register"), (audit.EntityId, audit.Reason));

            // Idempotent: nothing more to reverse.
            var period = await db.Set<PayPeriod>().SingleAsync(p => p.Id == kit.Period.Id);
            Assert.Equal(0, (await LeaveEngine.ReverseAccrualAsync(db, period, "again", Finance, DateTime.UtcNow, CancellationToken.None)).Entries);
        }

        // A corrected run accrues the period again, and voiding that one reverses it again.
        var corrected = await PostedRunAsync(kit);
        Assert.Equal((4m, 40m), (await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id), await kit.LedgerHoursAsync(kit.Fx.AnalystId, sick.Id)));
        Assert.Equal(0, (await kit.Balances.RunAccrualAsync(kit.Period.Id)).EntriesAdded);
        await kit.Runs.VoidAsync(corrected.Id, new VoidPayrollRequest("Still wrong"));
        Assert.Equal(0m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));
    }

    [Fact]
    public async Task Voiding_a_run_that_was_never_posted_leaves_leave_alone_and_other_periods_are_untouched()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        var nextPeriod = (await kit.Groups.ListPeriodsAsync(kit.Group.Id, 2026)).Single(p => p.Start == NextPeriodStart);
        await kit.Balances.RunAccrualAsync(kit.Period.Id);
        await kit.Balances.RunAccrualAsync(nextPeriod.Id);

        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("r.csv", PayrollKit.Register));
        await kit.Runs.VoidAsync(run.Id, new VoidPayrollRequest("Not posted"));
        Assert.Equal(8m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));

        var posted = await PostedRunAsync(kit);
        await kit.Runs.VoidAsync(posted.Id, new VoidPayrollRequest("Posted"));
        Assert.Equal(4m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id)); // the next period's accrual stays
    }

    private static async Task<PayrollRunDto> PostedRunAsync(PayrollKit kit)
    {
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("register.csv", PayrollKit.Register));
        return await kit.Runs.PostAsync(run.Id);
    }
}
