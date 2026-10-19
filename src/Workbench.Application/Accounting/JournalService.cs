using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;

namespace Workbench.Application.Accounting;

public sealed class JournalService(
    IWorkbenchDbContextFactory dbFactory,
    GeneralLedger ledger,
    Workbench.Application.ProjectAccounting.AuditRecorder audit,
    Approvals.ApprovalEngine? approvals = null) : IJournalService
{
    public async Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).AsQueryable();
        if (query.FiscalPeriodId is { } pid)
        {
            q = q.Where(e => e.FiscalPeriodId == pid);
        }

        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(e => e.Status == s);
        }

        if (query.Source is { } source)
        {
            var s = source.ToDomain();
            q = q.Where(e => e.Source == s);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(e => e.Number.Contains(term) || e.Description.Contains(term));
        }

        var entries = await q.OrderByDescending(e => e.Date).ThenByDescending(e => e.Sequence).ThenByDescending(e => e.CreatedAtUtc)
            .Take(Math.Clamp(query.Take, 1, 2000)).ToListAsync(cancellationToken);
        var lookups = await LedgerLookups.LoadAsync(db, cancellationToken);
        return entries.Select(lookups.Summary).ToList();
    }

    public async Task<JournalEntryDto> GetAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Journal entry", entryId);
        return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
    }

    public async Task<JournalEntryDto> CreateDraftAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await NewEntryAsync(db, request, cancellationToken);
        db.Set<JournalEntry>().Add(entry);
        audit.Record(db, GeneralLedger.AuditEntityType, entry.Id, "Created", after: new { entry.Date, entry.Description, Lines = entry.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
    }

    public async Task<JournalEntryDto> UpdateDraftAsync(Guid entryId, SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await LoadDraftAsync(db, entryId, cancellationToken);
        var period = await GeneralLedger.PeriodForAsync(db, request.Date, cancellationToken);
        entry.Date = request.Date;
        entry.FiscalPeriodId = period.Id;
        entry.Description = RequireDescription(request.Description);
        db.Set<JournalLine>().RemoveRange(entry.Lines);
        entry.Lines.Clear();
        AddLines(entry, request.Lines);

        // The lines have client-generated keys, so add them explicitly rather than relying on change detection.
        db.Set<JournalLine>().AddRange(entry.Lines);
        await GeneralLedger.ValidateLinesAsync(db, entry, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
    }

    public async Task DeleteDraftAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await LoadDraftAsync(db, entryId, cancellationToken);
        db.Set<JournalEntry>().Remove(entry);
        audit.Record(db, GeneralLedger.AuditEntityType, entry.Id, "DraftDeleted", before: new { entry.Date, entry.Description });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<JournalEntryDto> PostAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Journal entry", entryId);
        if (!await ApprovedToPostAsync(db, entry, cancellationToken))
        {
            await SaveAsync(db, cancellationToken);
            return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
        }

        await ledger.PostAsync(db, entry, cancellationToken);
        await SaveAsync(db, cancellationToken);
        return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
    }

    public async Task<JournalEntryDto> CreateAndPostAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);

        // Another module retrying the same source record gets the entry it already posted back.
        if (request.Source != JournalEntrySource.Manual && request.SourceId is { } sourceId)
        {
            var source = request.Source.ToDomain();
            var existing = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
                .FirstOrDefaultAsync(e => e.Source == source && e.SourceId == sourceId && e.Status == JournalStatus.Posted, cancellationToken);
            if (existing is not null)
            {
                return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(existing);
            }
        }

        var entry = await NewEntryAsync(db, request, cancellationToken);
        db.Set<JournalEntry>().Add(entry);
        if (!await ApprovedToPostAsync(db, entry, cancellationToken))
        {
            // A later workflow step must approve: keep it as a draft for them.
            await GeneralLedger.ValidateLinesAsync(db, entry, cancellationToken);
            await SaveAsync(db, cancellationToken);
            return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
        }

        await ledger.PostAsync(db, entry, cancellationToken);
        await SaveAsync(db, cancellationToken);
        return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(entry);
    }

    public async Task<JournalEntryDto> ReverseAsync(Guid entryId, ReverseJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Journal entry", entryId);
        if (entry.Source is JournalSource.Timesheet or JournalSource.Odc && entry.Status == JournalStatus.Posted)
        {
            throw new InvalidOperationException(entry.Source == JournalSource.Timesheet
                ? "This entry was posted from an approved timesheet. Reopen the timesheet to reverse it."
                : "This entry was posted from an approved ODC or expense. Reopen it to reverse the entry.");
        }

        var reversal = await ledger.ReverseAsync(db, entry, request.Date ?? entry.Date, request.Reason, cancellationToken);
        await SaveAsync(db, cancellationToken);
        return (await LedgerLookups.LoadAsync(db, cancellationToken)).Detail(reversal);
    }

    private async Task<JournalEntry> NewEntryAsync(IWorkbenchDbContext db, SaveJournalEntryRequest request, CancellationToken ct)
    {
        if (request.Source is JournalEntrySource.Timesheet or JournalEntrySource.Odc or JournalEntrySource.Reversal)
        {
            throw new ArgumentException($"{request.Source} entries are created by the ledger itself.");
        }

        var source = request.Source.ToDomain();
        var period = await GeneralLedger.PeriodForAsync(db, request.Date, ct);
        var revision = request.SourceId is { } sid && source != JournalSource.Manual
            ? await db.Set<JournalEntry>().CountAsync(e => e.Source == source && e.SourceId == sid, ct)
            : 0;
        var entry = new JournalEntry
        {
            Date = request.Date,
            FiscalPeriodId = period.Id,
            Source = source,
            SourceId = request.SourceId,
            SourceRevision = revision,
            Description = RequireDescription(request.Description),
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        AddLines(entry, request.Lines);
        await GeneralLedger.ValidateLinesAsync(db, entry, ct);
        return entry;
    }

    private static void AddLines(JournalEntry entry, IReadOnlyList<JournalLineInput>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return;
        }

        if (lines.Count > 500)
        {
            throw new ArgumentException("A journal entry can have at most 500 lines.");
        }

        foreach (var input in lines)
        {
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = entry.Lines.Count + 1,
                AccountId = input.AccountId,
                OrgUnitId = input.OrgUnitId,
                ProjectId = input.ProjectId,
                WbsElementId = input.WbsElementId,
                Debit = input.Debit,
                Credit = input.Credit,
                Memo = string.IsNullOrWhiteSpace(input.Memo) ? null : input.Memo.Trim(),
                TransactionCurrency = string.IsNullOrWhiteSpace(input.TransactionCurrency) ? null : input.TransactionCurrency.Trim().ToUpperInvariant(),
                TransactionAmount = string.IsNullOrWhiteSpace(input.TransactionCurrency) ? null : input.TransactionAmount,
                ExchangeRate = string.IsNullOrWhiteSpace(input.TransactionCurrency) ? null : input.ExchangeRate,
            });
        }
    }

    private static string RequireDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? throw new ArgumentException("A journal entry needs a description.")
        : description.Trim().Length > 500 ? throw new ArgumentException("The description can be at most 500 characters.")
        : description.Trim();

    private static async Task<JournalEntry> LoadDraftAsync(IWorkbenchDbContext db, Guid entryId, CancellationToken ct)
    {
        var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct)
            ?? throw WorkbenchNotFoundException.For("Journal entry", entryId);
        if (entry.Status != JournalStatus.Draft)
        {
            throw new InvalidOperationException($"Journal entry {entry.Number} is {entry.Status}; posted entries can't be changed. Reverse it instead.");
        }

        return entry;
    }

    /// <summary>
    /// Manual entries go through the journal-entry workflow (Setup › Approval workflows; by default Finance posts, as
    /// before). False while a later step still has to approve. Entries from other modules and calls without the engine
    /// registered (tests, seeding) aren't gated.
    /// </summary>
    private async Task<bool> ApprovedToPostAsync(IWorkbenchDbContext db, JournalEntry entry, CancellationToken ct)
    {
        if (approvals is null || entry.Source != JournalSource.Manual || entry.Status != JournalStatus.Draft)
        {
            return true;
        }

        var check = await approvals.CheckAsync(db, Contracts.Approvals.ApprovalDocumentTypes.JournalEntry, entry.Id, audit.Actor,
            _ => Task.FromResult(Approvals.ApprovalSubjects.JournalEntry(entry)), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("This journal entry is waiting on another approver.");
        }

        var outcome = await approvals.DecideAsync(db, check, Contracts.Approvals.ApprovalDecisionKind.Approve, audit.Actor, null, ct);
        if (!outcome.IsComplete)
        {
            audit.Record(db, GeneralLedger.AuditEntityType, entry.Id, "StepApproved", after: new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName).Distinct()) });
        }

        return outcome.IsComplete;
    }

    private static async Task SaveAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Another entry was posted at the same moment; try again.", ex);
        }
    }
}
