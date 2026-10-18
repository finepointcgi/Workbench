using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>
/// The month-end close: every timesheet with hours in the period approved, every ODC approved or rejected, nothing
/// left to post and a balanced trial balance; then the period is closed, which stops postings and any time or ODC
/// change dated in it (the timesheet and ODC services check the period, and the posting decorators refuse approvals
/// and reopens there).
/// </summary>
public sealed class PeriodCloseService(IWorkbenchDbContextFactory dbFactory, PostingEngine engine, AuditRecorder audit, IEnumerable<IPeriodCloseCheck>? moduleChecks = null)
    : IPeriodCloseService
{
    private const int MaxDetails = 25;

    public async Task<PeriodCloseChecklistDto> GetChecklistAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindAsync(db, fiscalPeriodId, cancellationToken);
        return await BuildAsync(db, period, cancellationToken);
    }

    public async Task<PeriodCloseChecklistDto> CloseAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default)
    {
        var reason = RequireReason(request.Reason);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindAsync(db, fiscalPeriodId, cancellationToken);
        if (period.Status == PeriodStatus.Closed)
        {
            throw new InvalidOperationException($"{period.Name} is already closed.");
        }

        var checklist = await BuildAsync(db, period, cancellationToken);
        if (!checklist.CanClose)
        {
            throw new InvalidOperationException($"{period.Name} can't close yet: " +
                string.Join("; ", checklist.Checks.Where(c => !c.Passed).Select(c => c.Summary)) + ".");
        }

        period.Status = PeriodStatus.Closed;
        audit.Record(db, nameof(FiscalPeriod), period.Id, "Closed",
            new { Status = nameof(PeriodStatus.Open) }, new { Status = nameof(PeriodStatus.Closed), Checklist = checklist.Checks.Select(c => c.Summary) }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return checklist with { IsClosed = true };
    }

    public async Task<PeriodCloseChecklistDto> ReopenAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default)
    {
        var reason = RequireReason(request.Reason);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindAsync(db, fiscalPeriodId, cancellationToken);
        if (period.Status == PeriodStatus.Open)
        {
            throw new InvalidOperationException($"{period.Name} is already open.");
        }

        period.Status = PeriodStatus.Open;
        audit.Record(db, nameof(FiscalPeriod), period.Id, "Reopened", new { Status = nameof(PeriodStatus.Closed) }, new { Status = nameof(PeriodStatus.Open) }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await BuildAsync(db, period, cancellationToken);
    }

    private async Task<PeriodCloseChecklistDto> BuildAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var (start, end) = (period.Start, period.End);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);

        // 1. Timesheets: any timesheet with hours dated in the period must be approved.
        var sheetIds = await db.Set<TimeEntry>().AsNoTracking()
            .Where(e => e.WorkDate >= start && e.WorkDate <= end && e.Hours != 0)
            .Select(e => e.TimesheetId).Distinct().ToListAsync(ct);
        var openSheets = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => sheetIds.Contains(t.Id) && t.Status != TimesheetStatus.Approved)
            .OrderBy(t => t.PeriodStart).ToListAsync(ct);
        var timesheets = Check(CloseCheckKind.TimesheetsApproved, "All timesheets approved", openSheets.Count,
            openSheets.Count == 0 ? $"All {sheetIds.Count} timesheets with hours in {period.Name} are approved" : $"{openSheets.Count} timesheet(s) not approved",
            openSheets.Select(t => $"{employees.GetValueOrDefault(t.EmployeeId, "Unknown")}, week of {t.PeriodStart:yyyy-MM-dd}: {t.Status}"));

        // 2. ODCs and expenses: every one dated in the period is approved or rejected.
        var undecided = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.IncurredOn >= start && o.IncurredOn <= end && (o.Status == OdcStatus.Draft || o.Status == OdcStatus.Submitted))
            .OrderBy(o => o.IncurredOn).ToListAsync(ct);
        var odcs = Check(CloseCheckKind.OdcsDecided, "All ODCs and expenses approved or rejected", undecided.Count,
            undecided.Count == 0 ? "No ODCs or expenses are waiting" : $"{undecided.Count} ODC(s) or expense line(s) still draft or submitted",
            undecided.Select(o => $"{o.IncurredOn:yyyy-MM-dd} {o.Description} ({o.Amount:C}): {o.Status}"));

        // 3. Nothing unposted: approved items all have entries, no stale entries, no draft journal entries.
        var unposted = await engine.ListUnpostedAsync(period.Id, ct);
        var drafts = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.FiscalPeriodId == period.Id && e.Status == JournalStatus.Draft).ToListAsync(ct);
        var unpostedCount = unposted.Count + drafts.Count;
        var posting = Check(CloseCheckKind.NothingUnposted, "Unposted items = 0", unpostedCount,
            unpostedCount == 0 ? "Every approved item is posted" : $"{unposted.Count} unposted item(s), {drafts.Count} draft journal entr{(drafts.Count == 1 ? "y" : "ies")}",
            unposted.Select(u => (u.IsStale ? "Needs reversal: " : "Not posted: ") + u.Description + (u.Problem is null ? "" : $" ({u.Problem})"))
                .Concat(drafts.Select(d => $"Draft entry: {d.Description}")));

        // 4. Trial balance: the period's postings balance.
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.FiscalPeriodId == period.Id && e.Status != JournalStatus.Draft
                select new { l.Debit, l.Credit })
            .ToListAsync(ct);
        var (debits, credits) = (lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        var trial = Check(CloseCheckKind.TrialBalanceBalanced, "Trial balance balanced", debits == credits ? 0 : 1,
            debits == credits ? $"Debits and credits both {debits:N2}" : $"Debits {debits:N2} vs credits {credits:N2}", []);

        List<CloseCheckDto> checks = [timesheets, odcs, posting, trial];
        foreach (var check in moduleChecks ?? [])
        {
            checks.Add(await check.CheckAsync(db, period, ct));
        }

        return new PeriodCloseChecklistDto(period.Id, period.Name, start, end, period.Status == PeriodStatus.Closed, checks);
    }

    private static CloseCheckDto Check(CloseCheckKind kind, string title, int count, string summary, IEnumerable<string> details) =>
        new(kind, title, count == 0, count, summary, details.Take(MaxDetails).ToList());

    private static async Task<FiscalPeriod> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Fiscal period", id);

    private static string RequireReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? throw new ArgumentException("A reason is required.") : reason.Trim();
}

/// <summary>A checklist item another module adds to the period close (for example billing's unbilled accrual).</summary>
public interface IPeriodCloseCheck
{
    Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct);
}
