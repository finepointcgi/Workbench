using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

// Automatic posting from the subledgers. These wrap the approval services (registered in AddWorkbenchAccounting, above
// AddWorkbenchIdentity, so the permission decorators wrap these and a refused call never reaches the ledger):
//   1. before the inner call, refuse to approve or reopen anything dated in a closed fiscal period;
//   2. run the approval or reopen;
//   3. post the labor/ODC entry, or reverse it, through PostingEngine (idempotent per source revision).
// A posting failure never undoes the approval: it's logged and the item shows on the unposted list (period close
// and "Post backlog").

public sealed class PostingTimesheetApprovalService(ITimesheetApprovalService inner, PostingEngine posting) : ITimesheetApprovalService
{
    public Task<bool> IsApproverAsync(CancellationToken cancellationToken = default) => inner.IsApproverAsync(cancellationToken);

    public Task<IReadOnlyList<TimesheetDto>> ListPendingAsync(CancellationToken cancellationToken = default) => inner.ListPendingAsync(cancellationToken);

    public async Task<TimesheetDto> ApproveAsync(Guid timesheetId, CancellationToken cancellationToken = default)
    {
        await posting.EnsureTimesheetPeriodsOpenAsync(timesheetId, "approve", cancellationToken);
        var result = await inner.ApproveAsync(timesheetId, cancellationToken);
        if (result.Status == TimesheetState.Approved) // Not yet when a later workflow step still has to approve.
        {
            await posting.TryPostAsync(JournalSource.Timesheet, timesheetId, cancellationToken);
        }

        return result;
    }

    public Task<TimesheetDto> RejectAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) =>
        inner.RejectAsync(timesheetId, reason, cancellationToken);

    public Task<TeamStatusDto> GetTeamStatusAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        inner.GetTeamStatusAsync(date, cancellationToken);
}

/// <summary>Reopening an approved timesheet reverses its labor entry.</summary>
public sealed class PostingTimesheetService(ITimesheetService inner, PostingEngine posting, IWorkbenchDbContextFactory dbFactory) : ITimesheetService
{
    public Task<TimesheetDetailDto> GetOrCreateMyTimesheetAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        inner.GetOrCreateMyTimesheetAsync(date, cancellationToken);

    public Task<IReadOnlyList<TimesheetDto>> ListMyTimesheetsAsync(CancellationToken cancellationToken = default) => inner.ListMyTimesheetsAsync(cancellationToken);

    public Task<TimesheetDetailDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) => inner.GetAsync(timesheetId, cancellationToken);

    public Task<TimesheetDetailDto> SaveEntriesAsync(Guid timesheetId, SaveTimeEntriesRequest request, CancellationToken cancellationToken = default) =>
        inner.SaveEntriesAsync(timesheetId, request, cancellationToken);

    public Task<SubmitTimesheetResult> SubmitAsync(Guid timesheetId, SubmitTimesheetRequest request, CancellationToken cancellationToken = default) =>
        inner.SubmitAsync(timesheetId, request, cancellationToken);

    public async Task<TimesheetDetailDto> ReopenAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default)
    {
        bool wasApproved;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            wasApproved = await db.Set<Timesheet>().AnyAsync(t => t.Id == timesheetId && t.Status == TimesheetStatus.Approved, cancellationToken);
        }

        if (wasApproved)
        {
            await posting.EnsureTimesheetPeriodsOpenAsync(timesheetId, "reopen", cancellationToken);
        }

        var result = await inner.ReopenAsync(timesheetId, reason, cancellationToken);
        if (wasApproved)
        {
            await posting.TryReverseAsync(JournalSource.Timesheet, timesheetId, $"Timesheet reopened: {reason}", cancellationToken);
        }

        return result;
    }
}

/// <summary>Approving a vendor ODC or an expense report posts it; reopening an approved one reverses it.</summary>
public sealed class PostingOdcApprovalService(IOdcApprovalService inner, PostingEngine posting, IWorkbenchDbContextFactory dbFactory) : IOdcApprovalService
{
    public Task<OdcApprovalQueueDto> GetMyQueueAsync(CancellationToken cancellationToken = default) => inner.GetMyQueueAsync(cancellationToken);

    public Task<OdcApprovalQueueDto> ListMyApprovedAsync(CancellationToken cancellationToken = default) => inner.ListMyApprovedAsync(cancellationToken);

    public async Task<ExpenseReportDto> ApproveReportAsync(Guid reportId, string? comment = null, CancellationToken cancellationToken = default)
    {
        var lines = await LinesAsync(reportId, null, cancellationToken);
        await posting.EnsureOdcPeriodsOpenAsync(lines, "approve this expense report", cancellationToken);
        var result = await inner.ApproveReportAsync(reportId, comment, cancellationToken);
        foreach (var id in result.Status == OdcApprovalState.Approved ? lines : [])
        {
            await posting.TryPostAsync(JournalSource.Odc, id, cancellationToken);
        }

        return result;
    }

    public Task<ExpenseReportDto> RejectReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default) =>
        inner.RejectReportAsync(reportId, reason, cancellationToken);

    public async Task<ExpenseReportDto> ReopenReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default)
    {
        var approved = await LinesAsync(reportId, OdcStatus.Approved, cancellationToken);
        await posting.EnsureOdcPeriodsOpenAsync(approved, "reopen this expense report", cancellationToken);
        var result = await inner.ReopenReportAsync(reportId, reason, cancellationToken);
        foreach (var id in approved)
        {
            await posting.TryReverseAsync(JournalSource.Odc, id, $"Expense report reopened: {reason}", cancellationToken);
        }

        return result;
    }

    public async Task<OdcEntryDto> ApproveOdcAsync(Guid odcId, string? comment = null, CancellationToken cancellationToken = default)
    {
        await posting.EnsureOdcPeriodsOpenAsync([odcId], "approve this ODC", cancellationToken);
        var result = await inner.ApproveOdcAsync(odcId, comment, cancellationToken);
        if (result.Status == OdcApprovalState.Approved)
        {
            await posting.TryPostAsync(JournalSource.Odc, odcId, cancellationToken);
        }

        return result;
    }

    public Task<OdcEntryDto> RejectOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default) =>
        inner.RejectOdcAsync(odcId, reason, cancellationToken);

    public async Task<OdcEntryDto> ReopenOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default)
    {
        bool wasApproved;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            wasApproved = await db.Set<OdcEntry>().AnyAsync(o => o.Id == odcId && o.Status == OdcStatus.Approved, cancellationToken);
        }

        if (wasApproved)
        {
            await posting.EnsureOdcPeriodsOpenAsync([odcId], "reopen this ODC", cancellationToken);
        }

        var result = await inner.ReopenOdcAsync(odcId, reason, cancellationToken);
        if (wasApproved)
        {
            await posting.TryReverseAsync(JournalSource.Odc, odcId, $"ODC reopened: {reason}", cancellationToken);
        }

        return result;
    }

    public Task<OdcEntryDto> SetUnallowableAsync(Guid odcId, bool isUnallowable, string? reason, CancellationToken cancellationToken = default) =>
        inner.SetUnallowableAsync(odcId, isUnallowable, reason, cancellationToken);

    private async Task<IReadOnlyList<Guid>> LinesAsync(Guid reportId, OdcStatus? status, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.ExpenseReportId == reportId && (status == null || o.Status == status))
            .Select(o => o.Id).ToListAsync(ct);
    }
}
