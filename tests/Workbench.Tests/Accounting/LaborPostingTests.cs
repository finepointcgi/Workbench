using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Time;

namespace Workbench.Tests.Accounting;

/// <summary>
/// Timesheet approval posts labor through the posting decorators. Dana's cost rate is $50 until 2026-10-19 and $60 from
/// 2026-10-20, so her Monday–Wednesday week straddles a rate change.
/// </summary>
public sealed class LaborPostingTests
{
    internal sealed record Host(TimeTestHost Time, LedgerKit Gl, PostingTimesheetApprovalService Approvals, PostingTimesheetService Timesheets);

    internal static async Task<Host> CreateAsync()
    {
        var time = await TimeTestHost.CreateAsync();
        var gl = await LedgerKit.CreateAsync(time.Factory, time.User, time.Clock);
        return new Host(time, gl, new PostingTimesheetApprovalService(time.Approvals, gl.Engine), new PostingTimesheetService(time.Timesheets, gl.Engine, time.Factory));
    }

    internal static async Task<Guid> ApproveDanaAsync(Host host)
    {
        var id = await host.Time.SubmittedDanaWeekAsync();
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(id);
        return id;
    }

    [Fact]
    public async Task Approval_posts_hours_times_the_rate_effective_each_day()
    {
        var host = await CreateAsync();
        var id = await ApproveDanaAsync(host);

        var entry = Assert.Single(await host.Gl.EntriesAsync(JournalSource.Timesheet));
        Assert.Equal(JournalStatus.Posted, entry.Status);
        Assert.Equal(id, entry.SourceId);
        Assert.Equal(0, entry.SourceRevision);
        Assert.Equal(new DateOnly(2026, 10, 25), entry.Date);

        // 8 h × $50 (Mon) + 16 h × $60 (Tue, Wed)
        var debit = Assert.Single(entry.Lines, l => l.Debit > 0);
        Assert.Equal(1360m, debit.Debit);
        Assert.Equal(host.Gl["5010"], debit.AccountId);
        Assert.Equal(host.Time.Direct.Id, debit.WbsElementId);
        Assert.Equal(host.Time.Direct.ProjectId, debit.ProjectId);
        var credit = Assert.Single(entry.Lines, l => l.Credit > 0);
        Assert.Equal(host.Gl["2100"], credit.AccountId);
        Assert.Equal(1360m, credit.Credit);
        Assert.Null(credit.ProjectId);
    }

    [Fact]
    public async Task Indirect_labor_goes_to_its_pools_account_and_leave_to_fringe()
    {
        var host = await CreateAsync();
        var leave = await host.Time.Projects.CreateWbsElementAsync(host.Time.Overhead.ProjectId,
            new SaveWbsElementRequest(null, "LV.PTO", "Paid time off", ChargeCodeKind.Leave, RequiresAssignment: false));
        await host.Gl.Posting.SetWbsPoolAsync(host.Time.Overhead.Id, new SetWbsPoolRequest(CostPool.GeneralAndAdministrative));

        host.Time.SignInAs(host.Time.Dana);
        var sheet = await host.Time.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday);
        var monday = TimeTestHost.Monday;
        await host.Time.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(
        [
            new TimeEntryInput(host.Time.Direct.Id, monday, 4m),
            new TimeEntryInput(host.Time.Overhead.Id, monday, 4m),
            new TimeEntryInput(leave.Id, monday.AddDays(1), 8m),
        ]));
        await host.Time.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(sheet.Timesheet.Id);

        var lines = Assert.Single(await host.Gl.EntriesAsync(JournalSource.Timesheet)).Lines;
        Assert.Equal(200m, lines.Single(l => l.AccountId == host.Gl["5010"]).Debit);   // 4 h × $50 direct
        Assert.Equal(200m, lines.Single(l => l.AccountId == host.Gl["6210"]).Debit);   // 4 h × $50 to the G&A pool's labor
        Assert.Equal(480m, lines.Single(l => l.AccountId == host.Gl["6010"]).Debit);   // 8 h × $60 leave → fringe
        Assert.Equal(880m, lines.Single(l => l.AccountId == host.Gl["2100"]).Credit);
    }

    [Fact]
    public async Task Retrying_a_posting_never_double_posts()
    {
        var host = await CreateAsync();
        var id = await ApproveDanaAsync(host);

        var retry = await host.Gl.Engine.PostTimesheetAsync(id);
        Assert.Equal(0, retry.Posted);
        Assert.Equal(1, retry.AlreadyPosted);
        var backlog = await host.Gl.Posting.PostBacklogAsync();
        Assert.Equal(0, backlog.EntriesPosted);
        Assert.Empty(await host.Gl.Posting.ListUnpostedAsync());
        Assert.Single(await host.Gl.EntriesAsync(JournalSource.Timesheet));
        Assert.Equal(1360m, await host.Gl.BalanceAsync("5010"));
    }

    [Fact]
    public async Task Reopening_an_approved_timesheet_reverses_it_and_reapproval_posts_the_new_revision()
    {
        var host = await CreateAsync();
        var id = await ApproveDanaAsync(host);

        host.Time.SignInAs(host.Time.Dana);
        await host.Timesheets.ReopenAsync(id, "Wednesday was a half day");
        var entries = await host.Gl.EntriesAsync();
        Assert.Equal(JournalStatus.Reversed, entries.Single(e => e.Source == JournalSource.Timesheet).Status);
        Assert.Single(entries, e => e.Source == JournalSource.Reversal);
        Assert.Equal(0m, await host.Gl.BalanceAsync("5010"));
        Assert.Equal(0m, await host.Gl.BalanceAsync("2100"));

        var monday = TimeTestHost.Monday;
        await host.Time.Timesheets.SaveEntriesAsync(id, new SaveTimeEntriesRequest(
            [new(host.Time.Direct.Id, monday, 8m), new(host.Time.Direct.Id, monday.AddDays(1), 8m), new(host.Time.Direct.Id, monday.AddDays(2), 4m)],
            Reason: "Half day"));
        await host.Time.Timesheets.SubmitAsync(id, new SubmitTimesheetRequest(true));
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(id);

        var current = (await host.Gl.EntriesAsync(JournalSource.Timesheet)).Single(e => e.Status == JournalStatus.Posted);
        Assert.Equal(1, current.SourceRevision);
        Assert.Equal(1120m, await host.Gl.BalanceAsync("5010")); // 400 + 480 + 240
    }

    [Fact]
    public async Task A_missed_reversal_shows_as_stale_and_the_backlog_fixes_it()
    {
        var host = await CreateAsync();
        var id = await ApproveDanaAsync(host);
        host.Time.SignInAs(host.Time.Dana);
        await host.Time.Timesheets.ReopenAsync(id, "Bypassing the decorator"); // undecorated: no reversal

        var stale = Assert.Single(await host.Gl.Posting.ListUnpostedAsync());
        Assert.True(stale.IsStale);

        var result = await host.Gl.Posting.PostBacklogAsync();
        Assert.Equal(1, result.EntriesReversed);
        Assert.Empty(await host.Gl.Posting.ListUnpostedAsync());
        Assert.Equal(0m, await host.Gl.BalanceAsync("5010"));
    }

    [Fact]
    public async Task Approval_without_a_cost_rate_still_approves_and_lists_the_problem()
    {
        var host = await CreateAsync();
        host.Time.SignInAs(host.Time.Sam);
        var sheet = await host.Time.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday);
        await host.Time.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new(host.Time.Overhead.Id, TimeTestHost.Monday, 8m)]));
        await host.Time.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        host.Time.User.Email = "admin@example.com";

        Assert.Equal(TimesheetState.Approved, (await host.Approvals.ApproveAsync(sheet.Timesheet.Id)).Status);
        Assert.Empty(await host.Gl.EntriesAsync(JournalSource.Timesheet));
        var unposted = Assert.Single(await host.Gl.Posting.ListUnpostedAsync());
        Assert.Contains("no cost rate", unposted.Problem);
    }

    [Fact]
    public async Task Approving_or_reopening_time_in_a_closed_period_is_refused()
    {
        var host = await CreateAsync();
        var submitted = await host.Time.SubmittedDanaWeekAsync();
        var october = host.Time.Periods.Single(p => p.Name == "2026-10");
        await host.Time.Calendar.SetPeriodStatusAsync(october.Id, FiscalPeriodState.Closed, "Test");

        host.Time.SignInAs(host.Time.Pat);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Approvals.ApproveAsync(submitted));
        Assert.Contains("closed fiscal period 2026-10", ex.Message);
        Assert.Empty(await host.Gl.EntriesAsync());

        await host.Time.Calendar.SetPeriodStatusAsync(october.Id, FiscalPeriodState.Open, "Test");
        await host.Approvals.ApproveAsync(submitted);
        await host.Time.Calendar.SetPeriodStatusAsync(october.Id, FiscalPeriodState.Closed, "Test");
        host.Time.SignInAs(host.Time.Dana);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.ReopenAsync(submitted, "Too late"));
        await using var db = await host.Time.Factory.CreateAsync();
        Assert.Equal(TimesheetStatus.Approved, (await db.Set<Timesheet>().SingleAsync(t => t.Id == submitted)).Status);
    }

    [Fact]
    public async Task Backlog_posts_items_from_a_closed_period_into_the_next_open_one()
    {
        var host = await CreateAsync();
        var id = await host.Time.ApprovedDanaWeekAsync(); // undecorated: approved, not posted
        var october = host.Time.Periods.Single(p => p.Name == "2026-10");
        await host.Time.Calendar.SetPeriodStatusAsync(october.Id, FiscalPeriodState.Closed, "Test");

        var result = await host.Gl.Posting.PostBacklogAsync();
        Assert.Equal(1, result.EntriesPosted);
        var entry = Assert.Single(await host.Gl.EntriesAsync(JournalSource.Timesheet));
        Assert.Equal(new DateOnly(2026, 11, 1), entry.Date);
        Assert.Contains("late posting", entry.Description);
        Assert.Equal(id, entry.SourceId);
    }
}
