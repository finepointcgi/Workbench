using Microsoft.EntityFrameworkCore;
using Workbench.Application.Leave;
using Workbench.Contracts;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Time;
using Workbench.Domain.Leave;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Payroll;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Leave;

public sealed class LeaveTests
{
    private static readonly DateOnly Week1 = PayrollKit.PeriodStart;

    [Fact]
    public async Task Accrual_uses_the_tier_for_years_of_service_and_is_idempotent_per_pay_period()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(tiers: [new(0, 4m), new(5, 6m), new(10, 8m)]);
        await kit.UpdateEmployeeAsync(kit.Fx.PmId, e => e.HireDate = new DateOnly(2020, 3, 1)); // 6 years on 2026-10-18

        var first = await kit.Balances.RunAccrualAsync(kit.Period.Id);
        Assert.Equal(3, first.EntriesAdded);
        Assert.Equal(6m, await kit.LedgerHoursAsync(kit.Fx.PmId, pto.Id));
        Assert.Equal(4m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));
        Assert.Equal(0m, await kit.LedgerHoursAsync(kit.Fx.OutsiderId, pto.Id)); // not in the pay group

        var again = await kit.Balances.RunAccrualAsync(kit.Period.Id);
        Assert.Equal((0, 3), (again.EntriesAdded, again.AlreadyAccrued));
        Assert.Equal(6m, await kit.LedgerHoursAsync(kit.Fx.PmId, pto.Id));
    }

    [Fact]
    public async Task Accrual_stops_at_the_cap_and_waits_out_the_waiting_period()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(tiers: [new(0, 4m), new(2, 4m, MaxBalance: 50m)], cap: 30m, waiting: 90);
        await kit.UpdateEmployeeAsync(kit.Fx.ViewerId, e => e.HireDate = new DateOnly(2026, 9, 1)); // inside the waiting period
        await kit.UpdateEmployeeAsync(kit.Fx.PmId, e => e.HireDate = new DateOnly(2025, 6, 1));     // 1 year: policy cap 30
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.PmId, pto.Id, 28.5m, "Opening balance", new DateOnly(2026, 1, 1)));
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 48m, "Opening balance", new DateOnly(2026, 1, 1))); // 2 years: tier cap 50

        var result = await kit.Balances.RunAccrualAsync(kit.Period.Id);

        Assert.Equal(30m, await kit.LedgerHoursAsync(kit.Fx.PmId, pto.Id));
        Assert.Equal(50m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));
        Assert.Equal(0m, await kit.LedgerHoursAsync(kit.Fx.ViewerId, pto.Id));
        Assert.Contains(result.Notes, n => n.Contains("waiting period"));
        Assert.Equal(2, result.Notes.Count(n => n.Contains("capped")));
    }

    [Fact]
    public async Task Per_hour_worked_accrual_counts_approved_non_leave_hours_and_annual_grants_come_once_a_year()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var perHour = await kit.PtoPolicyAsync(AccrualMethodKind.PerHourWorked, [new(0, 0.05m)]);
        await kit.AddApprovedHoursAsync(kit.Fx.AnalystId, kit.PtoWbs, (Week1, 8)); // leave doesn't earn leave
        kit.As(Admin);
        var sick = await kit.Policies.CreateAsync(new SaveLeavePolicyRequest("SICK", "Sick", LeaveTypeKind.Sick, kit.PtoWbs, AccrualMethodKind.AnnualGrant, [new(0, 40m)]));
        kit.As(Finance);

        await kit.Balances.RunAccrualAsync(kit.Period.Id);
        Assert.Equal(0.4m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, perHour.Id) + 8m); // 8 h worked × 0.05; the 8 h of PTO is usage
        Assert.Equal(40m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, sick.Id));

        var next = (await kit.Groups.ListPeriodsAsync(kit.Group.Id, 2026)).Single(p => p.Start == Week1.AddDays(14));
        await kit.Balances.RunAccrualAsync(next.Id);
        Assert.Equal(40m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, sick.Id));
    }

    [Fact]
    public async Task Year_end_forfeits_hours_above_the_carryover_limit_once()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(carryover: 80m);
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 120m, "Opening", new DateOnly(2026, 1, 1)));
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.ViewerId, pto.Id, 50m, "Opening", new DateOnly(2026, 1, 1)));

        var result = await kit.Balances.RunYearEndAsync(2026);
        Assert.Equal((1, 40m), (result.EmployeesForfeited, result.HoursForfeited));
        Assert.Equal(80m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));
        Assert.Equal(50m, await kit.LedgerHoursAsync(kit.Fx.ViewerId, pto.Id));
        Assert.Equal(0, (await kit.Balances.RunYearEndAsync(2026)).EmployeesForfeited);
    }

    [Fact]
    public async Task Usage_comes_from_approved_timesheets_and_a_reopened_timesheet_gives_it_back()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 40m, "Opening", new DateOnly(2026, 1, 1)));
        await kit.AddApprovedHoursAsync(kit.Fx.AnalystId, kit.PtoWbs, (Week1, 8), (Week1.AddDays(1), 4));

        kit.As(Editor);
        var mine = (await kit.Balances.GetMyBalancesAsync()).Single(b => b.PolicyId == pto.Id);
        Assert.Equal((28m, 12m), (mine.Balance, mine.UsedThisYear));

        await kit.SetTimesheetStatusAsync(kit.Fx.AnalystId, Week1, TimesheetStatus.Draft);
        Assert.Equal(40m, (await kit.Balances.GetMyBalancesAsync()).Single(b => b.PolicyId == pto.Id).Balance);

        // The ledger is append-only: the reversal is a new row, not a deletion.
        var history = await kit.Balances.GetHistoryAsync(kit.Fx.AnalystId, pto.Id);
        Assert.Equal([LeaveLedgerEntryKind.Usage, LeaveLedgerEntryKind.Usage, LeaveLedgerEntryKind.Adjustment], history.Select(h => h.Kind));
        Assert.Equal([40m, 28m, 40m], history.Select(h => h.BalanceAfter));
    }

    [Fact]
    public async Task An_approved_request_pre_fills_the_timesheet_and_holidays_pre_fill_the_holiday_code()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 40m, "Opening"));
        kit.As(Admin);
        await kit.Holidays.AddAsync(new SaveHolidayRequest(Week1.AddDays(4), "Company holiday"));

        kit.As(Editor);
        var request = await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1.AddDays(2), Week1.AddDays(4), Comment: "Trip"));
        Assert.Equal(LeaveRequestState.Pending, request.Status);
        Assert.Equal([Week1.AddDays(2), Week1.AddDays(3)], request.Days.Select(d => d.Date)); // the holiday is skipped
        Assert.Equal(16m, request.Hours);

        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.True(await db.Set<Notification>().AnyAsync(n => n.RecipientEmail == Owner && n.Kind == LeaveNotificationKinds.Requested));
        }

        kit.As(Owner);
        Assert.Single(await kit.Requests.ListPendingApprovalsAsync());
        await kit.Requests.ApproveAsync(request.Id, new DecideLeaveRequest());

        kit.As(Editor);
        Assert.Equal((40m, 16m, 24m), (await kit.Balances.GetMyBalancesAsync()).Select(b => (b.Balance, b.Planned, b.Available)).Single());
        var sheet = await kit.Timesheets.GetOrCreateMyTimesheetAsync(Week1);
        var leave = await kit.LeaveTimesheets.GetForTimesheetAsync(sheet.Timesheet.Id);
        Assert.Equal(
            [("LV.PTO", Week1.AddDays(2), 8m), ("LV.PTO", Week1.AddDays(3), 8m), ("LV.HOL", Week1.AddDays(4), 8m)],
            leave.Prefill.Select(p => (p.WbsCode, p.Date, p.Hours)));

        // On the team calendar for the analyst's supervisor.
        kit.As(Owner);
        var calendar = await kit.Requests.GetTeamCalendarAsync(Week1, Week1.AddDays(6));
        Assert.Equal(2, calendar.Count(e => e.Kind == TeamCalendarEntryKind.Leave && e.EmployeeId == kit.Fx.AnalystId));
        Assert.Contains(calendar, e => e.Kind == TeamCalendarEntryKind.Holiday);

        // Once saved the pre-fill is gone; once approved the hours are usage, no longer planned.
        kit.As(Editor);
        await kit.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(leave.Prefill.Select(p => new TimeEntryInput(p.WbsElementId, p.Date, p.Hours)).ToList()));
        Assert.Empty((await kit.LeaveTimesheets.GetForTimesheetAsync(sheet.Timesheet.Id)).Prefill);
        await kit.SetTimesheetStatusAsync(kit.Fx.AnalystId, Week1, TimesheetStatus.Approved);
        Assert.Equal((24m, 0m, 24m), (await kit.Balances.GetMyBalancesAsync()).Select(b => (b.Balance, b.Planned, b.Available)).Single());
    }

    [Fact]
    public async Task Leave_beyond_the_balance_is_blocked_when_the_policy_disallows_negative_balances()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(allowNegative: false);
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 4m, "Opening"));

        kit.As(Editor);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1)));
        var sheet = await kit.Timesheets.GetOrCreateMyTimesheetAsync(Week1);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new TimeEntryInput(kit.PtoWbs, Week1, 8m)])));
        Assert.Contains("negative balance", refused.Message);

        // Within the balance it saves.
        await kit.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new TimeEntryInput(kit.PtoWbs, Week1, 4m)]));
    }

    [Fact]
    public async Task Leave_beyond_the_balance_only_warns_when_negative_balances_are_allowed()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(allowNegative: true);

        kit.As(Editor);
        var request = await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1.AddDays(1), Week1.AddDays(1), 4m));
        Assert.NotEmpty(request.Warnings);
        var sheet = await kit.Timesheets.GetOrCreateMyTimesheetAsync(Week1);
        await kit.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new TimeEntryInput(kit.PtoWbs, Week1, 8m)]));
        var check = (await kit.LeaveTimesheets.GetForTimesheetAsync(sheet.Timesheet.Id)).Checks.Single();
        Assert.True(check.Exceeds);
        Assert.False(check.Blocks);
    }

    [Fact]
    public async Task Only_the_supervisor_decides_and_admins_own_policies_and_holidays()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(allowNegative: true);
        kit.As(Editor);
        var request = await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requests.ApproveAsync(request.Id, new DecideLeaveRequest()));
        kit.As(Viewer); // a peer, not the supervisor
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requests.ApproveAsync(request.Id, new DecideLeaveRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Balances.GetHistoryAsync(kit.Fx.AnalystId, pto.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.ViewerId, pto.Id, 8m, "Gift")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Policies.CreateAsync(new SaveLeavePolicyRequest("X", "X", LeaveTypeKind.Other, kit.PtoWbs, AccrualMethodKind.None, [])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Holidays.ApplyFederalTemplateAsync(2027));
        Assert.Empty(await kit.Requests.ListPendingApprovalsAsync());

        kit.As(Owner);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Requests.RejectAsync(request.Id, new DecideLeaveRequest()));
        var rejected = await kit.Requests.RejectAsync(request.Id, new DecideLeaveRequest("Coverage"));
        Assert.Equal(LeaveRequestState.Rejected, rejected.Status);
        Assert.Single(await kit.Balances.ListBalancesAsync(), b => b.EmployeeId == kit.Fx.AnalystId);

        await using var db = await kit.Factory.CreateAsync();
        Assert.True(await db.Set<Notification>().AnyAsync(n => n.RecipientEmail == Editor && n.Kind == LeaveNotificationKinds.Rejected));
        Assert.True(await db.Set<AuditEvent>().AnyAsync(a => a.EntityType == LeaveRequestService.AuditEntityType && a.Action == "Rejected"));
    }

    [Fact]
    public async Task Adjustments_need_a_reason_and_are_audited()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 8m, " ")));
        var entry = await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 8m, "Donated leave"));
        Assert.Equal(8m, entry.BalanceAfter);

        await using var db = await kit.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().SingleAsync(a => a.EntityType == "LeaveBalance");
        Assert.Equal(("Adjusted", "Donated leave", Finance), (audit.Action, audit.Reason, audit.Actor));
    }

    [Fact]
    public async Task Posting_payroll_runs_the_periods_leave_accrual()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("r.csv", PayrollKit.Register));
        await kit.Runs.PostAsync(run.Id);

        Assert.Equal(4m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));
        Assert.Equal(0, (await kit.Balances.RunAccrualAsync(kit.Period.Id)).EntriesAdded);
    }

    [Theory]
    [InlineData(2026, "2026-01-01,2026-01-19,2026-02-16,2026-05-25,2026-06-19,2026-07-03,2026-09-07,2026-10-12,2026-11-11,2026-11-26,2026-12-25")]
    [InlineData(2027, "2027-01-01,2027-01-18,2027-02-15,2027-05-31,2027-06-18,2027-07-05,2027-09-06,2027-10-11,2027-11-11,2027-11-25,2027-12-24,2027-12-31")]
    public void Federal_holidays_use_observed_dates(int year, string expected)
    {
        Assert.Equal(expected, string.Join(',', FederalHolidays.For(year).Select(h => h.Date.ToString("yyyy-MM-dd"))));
    }

    [Fact]
    public async Task The_federal_template_is_idempotent()
    {
        await using var kit = await PayrollKit.CreateAsync();
        kit.As(Admin);
        Assert.Equal(11, await kit.Holidays.ApplyFederalTemplateAsync(2026));
        Assert.Equal(0, await kit.Holidays.ApplyFederalTemplateAsync(2026));
        kit.As(Viewer);
        Assert.Equal(11, (await kit.Holidays.ListAsync(2026)).Count);
    }
}
