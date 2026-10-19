using Workbench.Contracts.Identity;
using Workbench.Application.Time;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Time;

public sealed class TimesheetRuleTests
{
    private static readonly DateOnly Monday = TimeTestHost.Monday;

    private static SaveTimeEntriesRequest Cell(Guid wbs, DateOnly date, decimal hours, string? reason = null) =>
        new([new TimeEntryInput(wbs, date, hours)], date, reason);

    [Fact]
    public async Task Weekly_period_runs_monday_to_sunday_and_semimonthly_splits_on_the_fifteenth()
    {
        Assert.Equal((Monday, Monday.AddDays(6)), TimekeepingClock.PeriodFor(new DateOnly(2026, 10, 25), TimesheetCadence.Weekly));
        Assert.Equal((new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 31)), TimekeepingClock.PeriodFor(new DateOnly(2026, 10, 21), TimesheetCadence.SemiMonthly));
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 15)), TimekeepingClock.PeriodFor(new DateOnly(2026, 2, 15), TimesheetCadence.SemiMonthly));

        var host = await TimeTestHost.CreateAsync(new TimekeepingOptions { Cadence = TimesheetCadence.SemiMonthly });
        host.SignInAs(host.Dana);
        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(Monday);
        Assert.Equal(new DateOnly(2026, 10, 16), sheet.Timesheet.PeriodStart);
        Assert.Equal(16, sheet.Days.Count);
    }

    [Fact]
    public async Task Employees_enter_their_own_time_and_drafts_change_freely()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync();
        var entered = detail.Entries[0].EnteredAtUtc;

        host.Clock.UtcNow = host.Clock.UtcNow.AddHours(1);
        detail = await host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id, Cell(host.Direct.Id, Monday, 6m));

        Assert.Equal(22m, detail.Timesheet.TotalHours);
        var monday = detail.Entries.Single(e => e.WorkDate == Monday);
        Assert.Equal(entered, monday.EnteredAtUtc);
        Assert.True(monday.UpdatedAtUtc > entered);
        Assert.Empty(detail.Revisions);
    }

    [Fact]
    public async Task Future_dates_are_rejected()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id, Cell(host.Direct.Id, Monday.AddDays(3), 8m)));
        Assert.Contains("future", ex.Message);
        Assert.False(detail.Days.Single(d => d.Date == Monday.AddDays(3)).CanEnter);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.GetOrCreateMyTimesheetAsync(Monday.AddDays(7)));
    }

    [Fact]
    public async Task Closed_fiscal_periods_reject_time()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        await host.Calendar.SetPeriodStatusAsync(host.Periods[0].Id, FiscalPeriodState.Closed, "October closed");
        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(Monday);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, Cell(host.Direct.Id, Monday, 8m)));
        Assert.Contains("open fiscal period", ex.Message);
    }

    [Fact]
    public async Task Dates_outside_the_fiscal_calendar_or_employment_are_rejected()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        // The week of 2026-09-28 starts before the fiscal calendar (FY27 begins 2026-10-01).
        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 9, 30));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, Cell(host.Overhead.Id, new DateOnly(2026, 9, 30), 8m)));

        host.SignInAs(host.Pat);
        await host.People.UpdateEmployeeAsync(host.Dana.Id, new SaveEmployeeRequest("E2", "dana@example.com", "Dana Lee", null, host.Pat.Id,
            host.Dana.DefaultLaborCategoryId, Monday.AddDays(1)));
        host.SignInAs(host.Dana);
        var week = await host.Timesheets.GetOrCreateMyTimesheetAsync(Monday);
        Assert.False(week.Days[0].IsEmployed);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(week.Timesheet.Id, Cell(host.Overhead.Id, Monday, 8m)));
    }

    [Fact]
    public async Task Only_authorized_charge_codes_are_accepted()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(Monday);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, Cell(host.Unassigned.Id, Monday, 8m)));
        Assert.Contains("charge code", ex.Message);
        Assert.DoesNotContain(sheet.ChargeCodes, c => c.ChargeCode.WbsElementId == host.Unassigned.Id);
        Assert.Contains(sheet.ChargeCodes, c => c.ChargeCode.WbsElementId == host.Overhead.Id);
    }

    [Fact]
    public async Task A_day_cannot_exceed_24_hours_across_charge_codes()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync(16m);

        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id,
            new SaveTimeEntriesRequest([new TimeEntryInput(host.Direct.Id, Monday, 16m), new TimeEntryInput(host.Overhead.Id, Monday, 9m)], Monday)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id, Cell(host.Direct.Id, Monday, 25m)));

        detail = await host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id,
            new SaveTimeEntriesRequest([new TimeEntryInput(host.Direct.Id, Monday, 16m), new TimeEntryInput(host.Overhead.Id, Monday, 8m)], Monday));
        Assert.Equal(24m, detail.Entries.Where(e => e.WorkDate == Monday).Sum(e => e.Hours));
    }

    [Fact]
    public async Task Submitting_requires_certification_and_stores_it()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SubmitAsync(detail.Timesheet.Id, new SubmitTimesheetRequest(false)));
        var result = await host.Timesheets.SubmitAsync(detail.Timesheet.Id, new SubmitTimesheetRequest(true));

        Assert.Equal(TimesheetState.Submitted, result.Detail.Timesheet.Status);
        Assert.Equal(host.Clock.UtcNow, result.Detail.Timesheet.CertifiedAtUtc);
        Assert.Contains(result.Detail.Audit, a => a.Action == "Submitted" && a.Actor == "dana@example.com");
        var sent = Assert.Single(host.Notifier.Sent);
        Assert.Equal(TimesheetEvent.Submitted, sent.Event);
        Assert.Equal(["pat@example.com"], sent.Recipients);
    }

    [Fact]
    public async Task Submitting_fewer_than_standard_hours_warns_but_submits()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync();

        var result = await host.Timesheets.SubmitAsync(detail.Timesheet.Id, new SubmitTimesheetRequest(true));

        Assert.Equal(TimesheetState.Submitted, result.Detail.Timesheet.Status);
        Assert.Equal(40m, result.Detail.Timesheet.StandardHours);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("below your standard 40", warning);
    }

    [Fact]
    public async Task Full_week_submits_without_warnings()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync(13.34m);

        var result = await host.Timesheets.SubmitAsync(detail.Timesheet.Id, new SubmitTimesheetRequest(true));

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Saving_one_day_replaces_only_that_days_entries()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync();

        detail = await host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id, Cell(host.Overhead.Id, Monday.AddDays(1), 8m));

        Assert.Equal(["1001.01", "IND.OH", "1001.01"], detail.Entries.OrderBy(e => e.WorkDate).Select(e => e.WbsCode));
    }

    [Fact]
    public async Task Nobody_approves_their_own_timesheet()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveAsync(id));

        // An unrelated employee isn't the approver either.
        host.SignInAs(host.Sam);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveAsync(id));
    }

    [Fact]
    public async Task Admin_approves_when_there_is_no_supervisor_but_not_their_own()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.GrantAsync("sam@example.com", OrgRoles.Admin);
        await host.GrantAsync("pat@example.com", OrgRoles.Admin);
        host.SignInAs(host.Sam);
        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(Monday);
        await host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, Cell(host.Overhead.Id, Monday, 8m));
        await host.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveAsync(sheet.Timesheet.Id));
        host.SignInAs(host.Pat);
        Assert.Contains(await host.Approvals.ListPendingAsync(), t => t.Id == sheet.Timesheet.Id);
        var approved = await host.Approvals.ApproveAsync(sheet.Timesheet.Id);
        Assert.Equal(TimesheetState.Approved, approved.Status);
        Assert.Equal("pat@example.com", approved.ReviewedBy);
    }

    [Fact]
    public async Task Supervisor_can_approve_or_reject_but_never_edit_hours()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync();
        host.SignInAs(host.Pat);

        Assert.True(await host.Approvals.IsApproverAsync());
        Assert.Single(await host.Approvals.ListPendingAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 4m, "fix")));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Approvals.RejectAsync(id, " "));

        var rejected = await host.Approvals.RejectAsync(id, "Monday should be on overhead");

        Assert.Equal(TimesheetState.Rejected, rejected.Status);
        Assert.Equal("Monday should be on overhead", rejected.RejectionReason);
        Assert.Equal(TimesheetEvent.Rejected, host.Notifier.Sent[^1].Event);
        host.SignInAs(host.Dana);
        var detail = await host.Timesheets.GetAsync(id);
        Assert.Contains(detail.Audit, a => a.Action == "Rejected" && a.Reason == "Monday should be on overhead");
        Assert.All(detail.Entries, e => Assert.Equal(8m, e.Hours));
    }

    [Fact]
    public async Task Approved_timesheets_are_locked()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.ApprovedDanaWeekAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 4m, "Correction")));
        Assert.Contains("locked", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SubmitAsync(id, new SubmitTimesheetRequest(true)));
        Assert.True((await host.Timesheets.GetAsync(id)).Timesheet.IsLocked);
    }

    [Fact]
    public async Task Submitted_timesheets_are_locked_until_recalled()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 4m, "Typo")));
        var recalled = await host.Timesheets.ReopenAsync(id, "Typo on Monday");

        Assert.Equal(TimesheetState.Draft, recalled.Timesheet.Status);
        Assert.Equal(0, recalled.Timesheet.Revision);
        Assert.Contains(recalled.Audit, a => a.Action == "Recalled");
    }

    [Fact]
    public async Task Reopening_needs_a_reason_increments_the_revision_and_requires_recertification_and_reapproval()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.ApprovedDanaWeekAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.ReopenAsync(id, ""));
        var reopened = await host.Timesheets.ReopenAsync(id, "Monday hours were wrong");

        Assert.Equal(TimesheetState.Draft, reopened.Timesheet.Status);
        Assert.Equal(1, reopened.Timesheet.Revision);
        Assert.Null(reopened.Timesheet.CertifiedAtUtc);
        Assert.Null(reopened.Timesheet.ApprovedAtUtc);
        var audit = Assert.Single(reopened.Audit, a => a.Action == "Reopened");
        Assert.Equal("Monday hours were wrong", audit.Reason);

        await host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 7m, "Monday hours were wrong"));
        await host.Timesheets.SubmitAsync(id, new SubmitTimesheetRequest(true));
        host.SignInAs(host.Pat);
        Assert.Single(await host.Approvals.ListPendingAsync());
        var approved = await host.Approvals.ApproveAsync(id);
        Assert.Equal((TimesheetState.Approved, 1), (approved.Status, approved.Revision));
    }

    [Fact]
    public async Task Changes_after_first_submission_need_a_reason_and_write_revisions()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.ApprovedDanaWeekAsync();
        await host.Timesheets.ReopenAsync(id, "Correction");

        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 7m)));

        var detail = await host.Timesheets.GetAsync(id);
        var tuesday = detail.Entries.Single(e => e.WorkDate == Monday.AddDays(1));
        detail = await host.Timesheets.SaveEntriesAsync(id, new SaveTimeEntriesRequest(
        [
            new TimeEntryInput(host.Direct.Id, Monday, 7m),                                 // hours changed
            new TimeEntryInput(host.Overhead.Id, Monday, 1m),                               // added
            new TimeEntryInput(host.Overhead.Id, tuesday.WorkDate, 8m, EntryId: tuesday.Id), // moved to another charge code
            // Wednesday omitted: deleted
        ], Reason: "Charged the wrong codes"));

        Assert.Equal(4, detail.Revisions.Count);
        Assert.All(detail.Revisions, r => Assert.Equal(("dana@example.com", "Charged the wrong codes", 1), (r.ChangedBy, r.Reason, r.TimesheetRevision)));
        Assert.Contains(detail.Revisions, r => r is { OldHours: 8m, NewHours: 7m, OldWbsCode: "1001.01", NewWbsCode: "1001.01" });
        Assert.Contains(detail.Revisions, r => r is { OldHours: null, NewHours: 1m, NewWbsCode: "IND.OH" });
        Assert.Contains(detail.Revisions, r => r is { OldWbsCode: "1001.01", NewWbsCode: "IND.OH" } && r.TimeEntryId == tuesday.Id);
        Assert.Contains(detail.Revisions, r => r is { OldHours: 8m, NewHours: null, NewWbsCode: null } && r.WorkDate == Monday.AddDays(2));
        Assert.Equal(16m, detail.Timesheet.TotalHours);
    }

    [Fact]
    public async Task Rejected_timesheet_corrections_are_tracked()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync();
        host.SignInAs(host.Pat);
        await host.Approvals.RejectAsync(id, "Wrong code");
        host.SignInAs(host.Dana);

        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 0m)));
        var detail = await host.Timesheets.SaveEntriesAsync(id, Cell(host.Direct.Id, Monday, 0m, "Wrong code"));

        var revision = Assert.Single(detail.Revisions);
        Assert.Equal((8m, (decimal?)null), (revision.OldHours, revision.NewHours));
        var result = await host.Timesheets.SubmitAsync(id, new SubmitTimesheetRequest(true));
        Assert.Equal(TimesheetState.Submitted, result.Detail.Timesheet.Status);
        Assert.Null(result.Detail.Timesheet.RejectionReason);
    }

    [Fact]
    public async Task Late_entries_are_flagged_in_the_daily_entry_report()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        // It's Wednesday 10-21. Last Thursday (10-15) is four business days ago; yesterday is on time.
        var lastWeek = await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 15));
        await host.Timesheets.SaveEntriesAsync(lastWeek.Timesheet.Id, Cell(host.Direct.Id, new DateOnly(2026, 10, 15), 8m));
        var week = await host.Timesheets.GetOrCreateMyTimesheetAsync(Monday);
        var detail = await host.Timesheets.SaveEntriesAsync(week.Timesheet.Id, Cell(host.Direct.Id, Monday.AddDays(1), 8m));

        Assert.False(detail.Days.Single(d => d.Date == Monday.AddDays(1)).EnteredLate);
        var rows = await host.Reports.GetDailyEntryComplianceAsync(new DateOnly(2026, 10, 12), Monday.AddDays(6));
        Assert.Equal(2, rows.Count);
        Assert.Equal((4, true), (rows[0].BusinessDaysAfterWorkDate, rows[0].IsLate));
        Assert.Equal((1, false), (rows[1].BusinessDaysAfterWorkDate, rows[1].IsLate));
    }

    [Fact]
    public async Task First_entry_time_is_kept_when_hours_are_removed_and_reentered()
    {
        var host = await TimeTestHost.CreateAsync();
        var detail = await host.DanaWeekAsync();
        var first = detail.Days[0].FirstEnteredAtUtc;
        await host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id, Cell(host.Direct.Id, Monday, 0m));

        host.Clock.UtcNow = host.Clock.UtcNow.AddDays(1);
        detail = await host.Timesheets.SaveEntriesAsync(detail.Timesheet.Id, Cell(host.Direct.Id, Monday, 8m));

        Assert.Equal(first, detail.Days[0].FirstEnteredAtUtc);
    }

    [Fact]
    public async Task Team_status_shows_each_report_and_missing_timesheets()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.SubmittedDanaWeekAsync();
        host.SignInAs(host.Pat);

        var current = await host.Approvals.GetTeamStatusAsync(Monday);
        var dana = Assert.Single(current.Members);
        Assert.Equal((TimesheetState.Submitted, 24m, false), (dana.Status, dana.Hours, dana.IsMissing));

        var lastWeek = await host.Approvals.GetTeamStatusAsync(Monday.AddDays(-7));
        Assert.True(Assert.Single(lastWeek.Members).IsMissing);
        Assert.Null(lastWeek.Members[0].TimesheetId);
    }

    [Fact]
    public async Task Reports_include_approved_hours_with_cost_at_the_effective_rate()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.ApprovedDanaWeekAsync();

        var hours = await host.Reports.GetHoursByChargeCodeAsync(Monday, Monday.AddDays(6));
        Assert.Equal(24m, Assert.Single(hours).Hours);
        var csv = await host.Reports.ExportLaborDistributionCsvAsync(Monday, Monday.AddDays(6));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("EmployeeNumber,Employee,WorkDate,Project,Wbs,LaborCategory,Hours,HourlyCost,Cost,UncompensatedHours", lines[0]);
        Assert.Equal("E2,Dana Lee,2026-10-19,1001,1001.01,SE2,8.00,50.0000,400.00,0.00", lines[1]);
        Assert.Equal("E2,Dana Lee,2026-10-20,1001,1001.01,SE2,8.00,60.0000,480.00,0.00", lines[2]);

        // Unapproved time is excluded.
        await host.Timesheets.ReopenAsync((await host.Timesheets.ListMyTimesheetsAsync())[0].Id, "Correction");
        Assert.Empty(await host.Reports.GetHoursByChargeCodeAsync(Monday, Monday.AddDays(6)));
    }
}
