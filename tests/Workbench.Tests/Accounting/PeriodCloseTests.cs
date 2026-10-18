using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Time;

namespace Workbench.Tests.Accounting;

public sealed class PeriodCloseTests
{
    [Fact]
    public async Task Close_checklist_gates_closing_and_a_closed_period_blocks_time_changes()
    {
        var host = await LaborPostingTests.CreateAsync();
        var october = host.Time.Periods.Single(p => p.Name == "2026-10").Id;
        var id = await host.Time.SubmittedDanaWeekAsync();
        await using (var db = await host.Time.Factory.CreateAsync())
        {
            db.Set<OdcEntry>().Add(new OdcEntry
            {
                ProjectId = host.Time.Direct.ProjectId, WbsElementId = host.Time.Direct.Id, Type = OdcType.Materials, IncurredOn = new DateOnly(2026, 10, 15),
                Amount = 99m, Description = "Cables", SubmittedBy = "pat@example.com", Status = OdcStatus.Draft,
            });
            await db.SaveChangesAsync();
        }

        var checklist = await host.Gl.Close.GetChecklistAsync(october);
        Assert.False(checklist.CanClose);
        Assert.Equal(1, Check(checklist, CloseCheckKind.TimesheetsApproved).Count);
        Assert.Equal(1, Check(checklist, CloseCheckKind.OdcsDecided).Count);
        Assert.True(Check(checklist, CloseCheckKind.TrialBalanceBalanced).Passed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Gl.Close.CloseAsync(october, new ClosePeriodRequest("Month end")));

        // Approve without posting, decide the ODC: now only the unposted check fails.
        host.Time.SignInAs(host.Time.Pat);
        await host.Time.Approvals.ApproveAsync(id);
        await using (var db = await host.Time.Factory.CreateAsync())
        {
            (await db.Set<OdcEntry>().SingleAsync()).Status = OdcStatus.Rejected;
            await db.SaveChangesAsync();
        }

        checklist = await host.Gl.Close.GetChecklistAsync(october);
        Assert.True(Check(checklist, CloseCheckKind.TimesheetsApproved).Passed);
        Assert.True(Check(checklist, CloseCheckKind.OdcsDecided).Passed);
        Assert.Equal(1, Check(checklist, CloseCheckKind.NothingUnposted).Count);

        await host.Gl.Posting.PostBacklogAsync();
        checklist = await host.Gl.Close.GetChecklistAsync(october);
        Assert.True(checklist.CanClose);

        var closed = await host.Gl.Close.CloseAsync(october, new ClosePeriodRequest("October close"));
        Assert.True(closed.IsClosed);
        Assert.Contains(await host.Time.Calendar.ListPeriodsAsync(), p => p.Id == october && p.Status == Contracts.ProjectAccounting.FiscalPeriodState.Closed);
        await using (var db = await host.Time.Factory.CreateAsync())
        {
            Assert.True(await db.Set<AuditEvent>().AnyAsync(a => a.EntityType == nameof(FiscalPeriod) && a.EntityId == october && a.Action == "Closed" && a.Reason == "October close"));
        }

        // Closed: no time changes, no reopen (which would reverse into the period), no postings.
        host.Time.SignInAs(host.Time.Dana);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.ReopenAsync(id, "Correction"));
        var sam = await host.Time.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Time.Timesheets.SaveEntriesAsync(sam.Timesheet.Id,
            new SaveTimeEntriesRequest([new(host.Time.Direct.Id, TimeTestHost.Monday, 1m)], Reason: "Late")));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Gl.Journal.CreateAndPostAsync(host.Gl.Entry(new DateOnly(2026, 10, 30), ("1010", 1m, 0), ("3100", 0, 1m))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Gl.Close.CloseAsync(october, new ClosePeriodRequest("Again")));

        var reopened = await host.Gl.Close.ReopenAsync(october, new ClosePeriodRequest("Audit adjustment"));
        Assert.False(reopened.IsClosed);
    }

    private static CloseCheckDto Check(PeriodCloseChecklistDto checklist, CloseCheckKind kind) => checklist.Checks.Single(c => c.Kind == kind);
}
