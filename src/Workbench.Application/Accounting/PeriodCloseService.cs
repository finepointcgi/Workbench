using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Application.Integrations;

namespace Workbench.Application.Accounting;

/// <summary>
/// The month-end close: every timesheet with hours in the period approved, every ODC approved or rejected, nothing
/// left to post and a balanced trial balance; then the period is closed, which stops postings and any time or ODC
/// change dated in it (the timesheet and ODC services check the period, and the posting decorators refuse approvals
/// and reopens there).
/// <para>
/// A period whose checks fail closes only when the closer says "close anyway" with a reason (they hold the close
/// permission, checked by the decorator). The failing checks, their details and the reason are recorded in the audit
/// trail and the security event log, so the exception is visible to an auditor.
/// </para>
/// </summary>
public sealed class PeriodCloseService(
    IWorkbenchDbContextFactory dbFactory, PostingEngine engine, AuditRecorder audit, IEnumerable<IPeriodCloseCheck>? moduleChecks = null,
    Security.SecurityEventLog? securityLog = null)
    : IPeriodCloseService
{
    private const int MaxDetails = 25;

    public async Task<PeriodCloseChecklistDto> GetChecklistAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindAsync(db, fiscalPeriodId, cancellationToken);
        return await BuildAsync(db, period, null, cancellationToken);
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

        var checklist = await BuildAsync(db, period, null, cancellationToken);
        var failed = RequirePassedOrOverride(checklist, period.Name, request.CloseAnyway);

        period.Status = PeriodStatus.Closed;
        var fiscalYear = await db.Set<FiscalYear>().AsNoTracking().Where(y => y.Id == period.FiscalYearId).Select(y => y.Year).FirstOrDefaultAsync(cancellationToken);
        IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.PeriodClosed, IntegrationEventPayloads.Period(period, fiscalYear), audit.Actor);
        audit.Record(db, nameof(FiscalPeriod), period.Id, failed.Count == 0 ? "Closed" : ClosedAnywayAction,
            new { Status = nameof(PeriodStatus.Open) },
            new { Status = nameof(PeriodStatus.Closed), Checklist = checklist.Checks.Select(c => c.Summary), FailedChecks = FailedSnapshot(failed) }, reason);
        await db.SaveChangesAsync(cancellationToken);
        if (failed.Count > 0)
        {
            await LogOverrideAsync($"FY{fiscalYear} {period.Name}", failed, reason, cancellationToken);
        }

        return checklist with { IsClosed = true };
    }

    public async Task<PeriodCloseChecklistDto> GetCompanyChecklistAsync(Guid fiscalPeriodId, Guid companyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindAsync(db, fiscalPeriodId, cancellationToken);
        if (!await db.Set<Domain.Companies.Company>().AsNoTracking().AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Company", companyId);
        }

        return await BuildAsync(db, period, companyId, cancellationToken);
    }

    /// <summary>The audit action of a close whose checks failed.</summary>
    public const string ClosedAnywayAction = "ClosedAnyway";

    /// <summary>
    /// Nothing when every check passes. With failing checks, refuses (<see cref="InvalidOperationException"/>) unless the
    /// closer said close anyway; then returns the failing checks for the audit trail.
    /// </summary>
    internal static IReadOnlyList<CloseCheckDto> RequirePassedOrOverride(PeriodCloseChecklistDto checklist, string what, bool closeAnyway)
    {
        var failed = checklist.Checks.Where(c => !c.Passed).ToList();
        if (failed.Count > 0 && !closeAnyway)
        {
            throw new InvalidOperationException($"{what} can't close yet: " + string.Join("; ", failed.Select(c => c.Summary)) +
                ". Fix these, or close anyway with a reason (it's recorded in the audit trail).");
        }

        return failed;
    }

    internal static object FailedSnapshot(IReadOnlyList<CloseCheckDto> failed) =>
        failed.Select(c => new { Check = c.Title, c.Summary, c.Count, c.Details }).ToList();

    internal async Task LogOverrideAsync(string target, IReadOnlyList<CloseCheckDto> failed, string reason, CancellationToken ct)
    {
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(Contracts.Security.SecurityEventTypes.PeriodClosedAnyway, target,
                detail: $"Closed with {failed.Count} failing check(s): {string.Join("; ", failed.Select(c => c.Summary))}. Reason: {reason}", cancellationToken: ct);
        }
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
        return await BuildAsync(db, period, null, cancellationToken);
    }

    /// <summary>The checklist, for the tenant or (with <paramref name="companyId"/>) one company's records.</summary>
    private async Task<PeriodCloseChecklistDto> BuildAsync(IWorkbenchDbContext db, FiscalPeriod period, Guid? companyId, CancellationToken ct)
    {
        var (start, end) = (period.Start, period.End);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);

        // A company's part: its employees (the employer of record) and what's charged to its projects.
        var companyEmployees = companyId is { } cid
            ? (await db.Set<Employee>().AsNoTracking().Where(e => e.CompanyId == cid).Select(e => e.Id).ToListAsync(ct)).ToHashSet()
            : null;
        var companyProjects = companyId is { } pid
            ? (await db.Set<Project>().AsNoTracking().Where(p => p.CompanyId == pid).Select(p => p.Id).ToListAsync(ct)).ToHashSet()
            : null;

        // 1. Timesheets: any timesheet with hours dated in the period must be approved.
        var hours = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                where e.WorkDate >= start && e.WorkDate <= end && e.Hours != 0
                select new { e.TimesheetId, w.ProjectId })
            .Distinct().ToListAsync(ct);
        var sheetIds = hours.Select(h => h.TimesheetId).Distinct().ToList();
        var sheetsInPeriod = await db.Set<Timesheet>().AsNoTracking().Where(t => sheetIds.Contains(t.Id)).ToListAsync(ct);
        if (companyId is not null)
        {
            var charged = hours.Where(h => companyProjects!.Contains(h.ProjectId)).Select(h => h.TimesheetId).ToHashSet();
            sheetsInPeriod = sheetsInPeriod.Where(t => companyEmployees!.Contains(t.EmployeeId) || charged.Contains(t.Id)).ToList();
        }

        var openSheets = sheetsInPeriod.Where(t => t.Status != TimesheetStatus.Approved).OrderBy(t => t.PeriodStart).ToList();
        var timesheets = Check(CloseCheckKind.TimesheetsApproved, "All timesheets approved", openSheets.Count,
            openSheets.Count == 0 ? $"All {sheetsInPeriod.Count} timesheets with hours in {period.Name} are approved" : $"{openSheets.Count} timesheet(s) not approved",
            openSheets.Select(t => $"{employees.GetValueOrDefault(t.EmployeeId, "Unknown")}, week of {t.PeriodStart:yyyy-MM-dd}: {t.Status}"));

        // 2. ODCs and expenses: every one dated in the period is approved or rejected.
        var undecided = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.IncurredOn >= start && o.IncurredOn <= end && (o.Status == OdcStatus.Draft || o.Status == OdcStatus.Submitted))
            .OrderBy(o => o.IncurredOn).ToListAsync(ct);
        if (companyId is not null)
        {
            undecided = undecided.Where(o => companyProjects!.Contains(o.ProjectId) || (o.EmployeeId is { } e && companyEmployees!.Contains(e))).ToList();
        }

        var odcs = Check(CloseCheckKind.OdcsDecided, "All ODCs and expenses approved or rejected", undecided.Count,
            undecided.Count == 0 ? "No ODCs or expenses are waiting" : $"{undecided.Count} ODC(s) or expense line(s) still draft or submitted",
            undecided.Select(o => $"{o.IncurredOn:yyyy-MM-dd} {o.Description} ({o.Amount:C}): {o.Status}"));

        // 3. Nothing unposted: approved items all have entries, no stale entries, no draft journal entries.
        var unposted = await engine.ListUnpostedAsync(period.Id, ct);
        var draftQuery = db.Set<JournalEntry>().AsNoTracking().Where(e => e.FiscalPeriodId == period.Id && e.Status == JournalStatus.Draft);
        if (companyId is { } draftCompany)
        {
            var mineSheets = sheetsInPeriod.Select(t => t.Id).ToHashSet();
            var odcIds = unposted.Where(u => u.Source == JournalEntrySource.Odc).Select(u => u.SourceId).ToList();
            var mineOdcs = (await db.Set<OdcEntry>().AsNoTracking().Where(o => odcIds.Contains(o.Id)).ToListAsync(ct))
                .Where(o => companyProjects!.Contains(o.ProjectId) || (o.EmployeeId is { } e && companyEmployees!.Contains(e)))
                .Select(o => o.Id).ToHashSet();
            unposted = unposted.Where(u => u.Source == JournalEntrySource.Timesheet ? mineSheets.Contains(u.SourceId) : u.Source != JournalEntrySource.Odc || mineOdcs.Contains(u.SourceId)).ToList();
            draftQuery = draftQuery.Where(e => e.CompanyId == draftCompany);
        }

        var drafts = await draftQuery.ToListAsync(ct);
        var unpostedCount = unposted.Count + drafts.Count;
        var posting = Check(CloseCheckKind.NothingUnposted, "Unposted items = 0", unpostedCount,
            unpostedCount == 0 ? "Every approved item is posted" : $"{unposted.Count} unposted item(s), {drafts.Count} draft journal entr{(drafts.Count == 1 ? "y" : "ies")}",
            unposted.Select(u => (u.IsStale ? "Needs reversal: " : "Not posted: ") + u.Description + (u.Problem is null ? "" : $" ({u.Problem})"))
                .Concat(drafts.Select(d => $"Draft entry: {d.Description}")));

        // 4. Trial balance: the period's postings balance.
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.FiscalPeriodId == period.Id && e.Status != JournalStatus.Draft && (companyId == null || l.CompanyId == companyId)
                select new { l.Debit, l.Credit })
            .ToListAsync(ct);
        var (debits, credits) = (lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        var trial = Check(CloseCheckKind.TrialBalanceBalanced, "Trial balance balanced", debits == credits ? 0 : 1,
            debits == credits ? $"Debits and credits both {debits:N2}" : $"Debits {debits:N2} vs credits {credits:N2}", []);

        // 5+. Checks other accounting modules register (indirect rates: allocation posted).
        var checks = new List<CloseCheckDto> { timesheets, odcs, posting, trial };
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

/// <summary>An extra period-close check contributed by another accounting module (registered as a scoped service).</summary>
public interface IPeriodCloseCheck
{
    Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken cancellationToken);
}
