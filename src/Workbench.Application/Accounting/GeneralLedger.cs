using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>
/// The ledger's invariants in one place, used by manual journal entries and subledger posting alike:
/// an entry balances to the cent, posts only into an open fiscal period, to active detail accounts with consistent
/// segments, is immutable once posted (corrections are reversals), and every post and reversal is audited in the same
/// unit of work. Each company's ledger balances on its own (<see cref="Companies.CompanyLedger"/>). Callers add new entries
/// to the context and save.
/// </summary>
public sealed class GeneralLedger(AuditRecorder audit, TimeProvider clock)
{
    public const string AuditEntityType = nameof(JournalEntry);

    public string Actor => audit.Actor;

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>The fiscal period covering <paramref name="date"/>; <see cref="ArgumentException"/> when none does or it's closed.</summary>
    public static async Task<FiscalPeriod> OpenPeriodForAsync(IWorkbenchDbContext db, DateOnly date, CancellationToken ct)
    {
        var period = await PeriodForAsync(db, date, ct);
        if (period.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed; post into an open period.");
        }

        return period;
    }

    public static async Task<FiscalPeriod> PeriodForAsync(IWorkbenchDbContext db, DateOnly date, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= date && p.End >= date, ct)
            ?? throw new ArgumentException($"No fiscal period covers {date:yyyy-MM-dd}.");

    /// <summary>
    /// Checks the lines' accounts and segments: active detail accounts, a WBS element that belongs to the line's project
    /// (the project is filled in from the WBS when missing), and existing organization units.
    /// </summary>
    public static async Task ValidateLinesAsync(IWorkbenchDbContext db, JournalEntry entry, CancellationToken ct)
    {
        var accountIds = entry.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var parents = (await db.Set<Account>().AsNoTracking().Where(a => a.ParentId != null && accountIds.Contains(a.ParentId.Value))
            .Select(a => a.ParentId!.Value).ToListAsync(ct)).ToHashSet();
        var wbsIds = entry.Lines.Where(l => l.WbsElementId is not null).Select(l => l.WbsElementId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var projectIds = entry.Lines.Where(l => l.ProjectId is not null).Select(l => l.ProjectId!.Value).Distinct().ToList();
        var projects = (await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        var orgIds = entry.Lines.Where(l => l.OrgUnitId is not null).Select(l => l.OrgUnitId!.Value).Distinct().ToList();
        var orgs = (await db.Set<OrgUnit>().AsNoTracking().Where(o => orgIds.Contains(o.Id)).Select(o => o.Id).ToListAsync(ct)).ToHashSet();

        foreach (var line in entry.Lines)
        {
            if (!accounts.TryGetValue(line.AccountId, out var account))
            {
                throw new ArgumentException($"Line {line.LineNumber}: choose an account.");
            }

            if (!account.IsActive)
            {
                throw new ArgumentException($"Line {line.LineNumber}: account {account.Number} {account.Name} is inactive.");
            }

            if (parents.Contains(account.Id))
            {
                throw new ArgumentException($"Line {line.LineNumber}: {account.Number} {account.Name} is a summary account; post to one of its detail accounts.");
            }

            if (line.WbsElementId is { } wbsId)
            {
                if (!wbs.TryGetValue(wbsId, out var element))
                {
                    throw new ArgumentException($"Line {line.LineNumber}: the WBS element doesn't exist.");
                }

                if (line.ProjectId is { } pid && pid != element.ProjectId)
                {
                    throw new ArgumentException($"Line {line.LineNumber}: WBS {element.Code} isn't part of the line's project.");
                }

                line.ProjectId = element.ProjectId;
            }
            else if (line.ProjectId is { } pid && !projects.Contains(pid))
            {
                throw new ArgumentException($"Line {line.LineNumber}: the project doesn't exist.");
            }

            if (line.OrgUnitId is { } oid && !orgs.Contains(oid))
            {
                throw new ArgumentException($"Line {line.LineNumber}: the organization unit doesn't exist.");
            }
        }

        await Companies.CompanyLedger.ResolveAsync(db, entry, accounts, ct);
    }

    /// <summary>
    /// Posts a draft that is already in <paramref name="db"/>: balance, open period, accounts and segments, then the next
    /// number and an audit row. Throws <see cref="ArgumentException"/> (invalid) or <see cref="InvalidOperationException"/>
    /// (already posted).
    /// </summary>
    public async Task PostAsync(IWorkbenchDbContext db, JournalEntry entry, CancellationToken ct)
    {
        entry.EnsurePostable();
        var period = await OpenPeriodForAsync(db, entry.Date, ct);
        entry.FiscalPeriodId = period.Id;
        await ValidateLinesAsync(db, entry, ct);
        await Companies.CompanyLedger.BalanceAsync(db, entry, ct);
        entry.MarkPosted(await NextSequenceAsync(db, ct), Actor, UtcNow);
        audit.Record(db, AuditEntityType, entry.Id, "Posted", after: AuditSnapshot(entry));
    }

    /// <summary>
    /// Adds and posts the reversal of a posted entry (its lines loaded) dated <paramref name="date"/>, and marks the
    /// original reversed. Both rows are audited.
    /// </summary>
    public async Task<JournalEntry> ReverseAsync(IWorkbenchDbContext db, JournalEntry original, DateOnly date, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reverse an entry.");
        }

        var period = await OpenPeriodForAsync(db, date, ct);
        var reversal = original.CreateReversal(date, period.Id, reason.Trim(), Actor, UtcNow);
        db.Set<JournalEntry>().Add(reversal);
        await PostAsync(db, reversal, ct);
        original.MarkReversed(reversal, reason.Trim());
        audit.Record(db, AuditEntityType, original.Id, "Reversed",
            new { Status = nameof(JournalStatus.Posted) }, new { Status = original.Status.ToString(), ReversedBy = reversal.Number }, reason.Trim());
        return reversal;
    }

    /// <summary>The next entry number, counting entries posted earlier in this unit of work.</summary>
    private static async Task<int> NextSequenceAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        // "Sequence > 0" matches the unique index's filter, so the maximum is read from the index instead of a table scan.
        var saved = await db.Set<JournalEntry>().Where(e => e.Sequence > 0).MaxAsync(e => (int?)e.Sequence, ct) ?? 0;
        var local = db.Set<JournalEntry>().Local.Select(e => e.Sequence).DefaultIfEmpty(0).Max();
        return Math.Max(saved, local) + 1;
    }

    private static object AuditSnapshot(JournalEntry e) => new
    {
        e.Number,
        e.Date,
        Source = e.Source.ToString(),
        e.SourceId,
        e.SourceRevision,
        e.Description,
        Debits = e.TotalDebit,
        Credits = e.TotalCredit,
        Lines = e.Lines.Count,
    };
}
