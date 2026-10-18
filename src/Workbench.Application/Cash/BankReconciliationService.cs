using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Cash;
using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Cash;

public sealed class BankReconciliationService(
    IWorkbenchDbContextFactory dbFactory,
    IJournalService journal,
    AuditRecorder audit,
    TimeProvider clock) : IBankReconciliationService
{
    public async Task<IReadOnlyList<BankReconciliationDto>> ListAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var recs = await db.Set<BankReconciliation>().AsNoTracking().Where(r => bankAccountId == null || r.BankAccountId == bankAccountId).ToListAsync(cancellationToken);
        var result = new List<BankReconciliationDto>();
        foreach (var group in recs.GroupBy(r => r.BankAccountId))
        {
            var state = await State.LoadAsync(db, group.Key, cancellationToken);
            foreach (var rec in group)
            {
                // Completed reconciliations show their snapshot; only open ones are recomputed.
                result.Add(rec.Status == BankReconciliationStatus.Completed ? ToDto(rec, state, Snapshot(rec), withItems: false) : ToDto(rec, state, state.Compute(rec), withItems: false));
            }
        }

        return result.OrderBy(r => r.BankAccountName).ThenByDescending(r => r.StatementEndDate).ToList();
    }

    public async Task<ReconciliationDefaultsDto> GetDefaultsAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await State.LoadAsync(db, bankAccountId, cancellationToken);
        var recs = await db.Set<BankReconciliation>().AsNoTracking().Where(r => r.BankAccountId == bankAccountId).ToListAsync(cancellationToken);
        var through = recs.Where(r => r.Status == BankReconciliationStatus.Completed).Select(r => (DateOnly?)r.StatementEndDate).Max();
        var open = recs.FirstOrDefault(r => r.Status == BankReconciliationStatus.InProgress);
        var latest = await db.Set<BankStatementImport>().AsNoTracking().Where(i => i.BankAccountId == bankAccountId && i.PeriodEnd != null)
            .OrderByDescending(i => i.PeriodEnd).ThenByDescending(i => i.ImportedAtUtc).FirstOrDefaultAsync(cancellationToken);
        DateOnly? end = latest?.PeriodEnd is { } e && (through is null || e > through) ? e : null;
        decimal? balance = end is { } d ? latest!.ClosingBalance ?? StatementBalance(state, recs, d) : null;
        return new ReconciliationDefaultsDto(bankAccountId, end, balance, through, open?.Id);
    }

    public async Task<BankReconciliationDto> StartAsync(StartReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<BankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.BankAccountId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Bank account", request.BankAccountId);
        var recs = await db.Set<BankReconciliation>().Where(r => r.BankAccountId == account.Id).ToListAsync(cancellationToken);
        var open = recs.FirstOrDefault(r => r.Status == BankReconciliationStatus.InProgress);
        if (open is not null)
        {
            if (open.StatementEndDate != request.StatementEndDate)
            {
                throw new InvalidOperationException($"{account.Name} already has a reconciliation in progress for {open.StatementEndDate:yyyy-MM-dd}; finish it first.");
            }

            if (request.StatementEndingBalance is { } b && b != open.StatementEndingBalance)
            {
                open.StatementEndingBalance = b;
                await db.SaveChangesAsync(cancellationToken);
            }

            var current = await State.LoadAsync(db, account.Id, cancellationToken);
            return ToDto(open, current, current.Compute(open), withItems: true);
        }

        if (!account.IsActive)
        {
            throw new ArgumentException($"{account.Name} is inactive.");
        }

        var through = recs.Where(r => r.Status == BankReconciliationStatus.Completed).Select(r => (DateOnly?)r.StatementEndDate).Max();
        if (through is { } t && request.StatementEndDate <= t)
        {
            throw new ArgumentException($"{account.Name} is reconciled through {t:yyyy-MM-dd}; choose a later statement date (or reopen that reconciliation).");
        }

        var state = await State.LoadAsync(db, account.Id, cancellationToken);
        var balance = request.StatementEndingBalance;
        if (balance is null)
        {
            var import = await db.Set<BankStatementImport>().AsNoTracking()
                .Where(i => i.BankAccountId == account.Id && i.PeriodEnd == request.StatementEndDate && i.ClosingBalance != null)
                .OrderByDescending(i => i.ImportedAtUtc).FirstOrDefaultAsync(cancellationToken);
            balance = import?.ClosingBalance ?? StatementBalance(state, recs, request.StatementEndDate);
        }

        var rec = new BankReconciliation
        {
            BankAccountId = account.Id,
            StatementEndDate = request.StatementEndDate,
            StatementEndingBalance = decimal.Round(balance.Value, 2),
            StartedBy = audit.Actor,
            StartedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<BankReconciliation>().Add(rec);
        audit.Record(db, nameof(BankReconciliation), rec.Id, "Started", after: new { Account = account.Name, rec.StatementEndDate, rec.StatementEndingBalance });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rec, state, state.Compute(rec), withItems: true);
    }

    public async Task<BankReconciliationDto> UpdateAsync(Guid reconciliationId, UpdateReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rec = await FindAsync(db, reconciliationId, cancellationToken);
        RequireInProgress(rec);
        var before = new { rec.StatementEndingBalance, rec.Note };
        rec.StatementEndingBalance = decimal.Round(request.StatementEndingBalance, 2);
        rec.Note = string.IsNullOrWhiteSpace(request.Note) ? rec.Note : request.Note.Trim();
        audit.Record(db, nameof(BankReconciliation), rec.Id, "Updated", before, new { rec.StatementEndingBalance, rec.Note });
        await db.SaveChangesAsync(cancellationToken);
        var state = await State.LoadAsync(db, rec.BankAccountId, cancellationToken);
        return ToDto(rec, state, state.Compute(rec), withItems: true);
    }

    public async Task<ReconcileWorkspaceDto> GetWorkspaceAsync(Guid reconciliationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rec = await FindAsync(db, reconciliationId, cancellationToken);
        var state = await State.LoadAsync(db, rec.BankAccountId, cancellationToken);
        var settings = await CashSupport.SettingsAsync(db, cancellationToken);
        var end = rec.StatementEndDate;

        // Open (unlocked) matches, and the items through the statement date that aren't locked; plus anything in an open
        // match so a match is always shown whole.
        var openMatches = state.Matches.Where(m => m.ReconciliationId is null).ToList();
        var inOpenMatch = openMatches.Select(m => m.Id).ToHashSet();
        var bank = state.Bank.Where(t => (t.Date <= end && t.MatchId is null) || (t.MatchId is { } m && inOpenMatch.Contains(m))).ToList();
        var gl = state.Gl.Where(l => state.GlMatch.TryGetValue(l.LineId, out var m) ? inOpenMatch.Contains(m) : l.Date <= end).ToList();
        return new ReconcileWorkspaceDto(
            ToDto(rec, state, state.Compute(rec), withItems: true),
            bank.Select(t => t.ToDto(state.Locked)).ToList(),
            gl.Select(state.GlItem).ToList(),
            openMatches.Select(state.MatchDto).ToList(),
            settings.MatchDateWindowDays);
    }

    public async Task<AutoMatchResultDto> AutoMatchAsync(AutoMatchRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await State.LoadAsync(db, request.BankAccountId, cancellationToken);
        var window = (await CashSupport.SettingsAsync(db, cancellationToken)).MatchDateWindowDays;
        var bankOpen = state.Bank.Where(t => t.MatchId is null && (request.Through is null || t.Date <= request.Through))
            .Select(t => new OpenBankItem(t.Id, t.Date, t.Amount, t.Type, t.Description, t.CheckNumber, t.BankReference)).ToList();
        var glOpen = state.Gl.Where(l => !state.GlMatch.ContainsKey(l.LineId) && (request.Through is null || l.Date <= request.Through.Value.AddDays(window))).ToList();
        var proposals = BankMatcher.Propose(bankOpen, glOpen, window);

        var bankById = await db.Set<BankTransaction>().Where(t => t.BankAccountId == state.Account.Id && t.MatchId == null).ToDictionaryAsync(t => t.Id, cancellationToken);
        var glById = state.Gl.ToDictionary(l => l.LineId);
        var now = clock.GetUtcNow().UtcDateTime;
        var created = new List<BankMatch>();
        foreach (var p in proposals)
        {
            var match = NewMatch(state.Account.Id, p.Rule, p.Confidence, p.Note, now);
            foreach (var id in p.JournalLineIds)
            {
                match.GlLines.Add(new BankMatchLine { MatchId = match.Id, BankAccountId = state.Account.Id, JournalLineId = id, Amount = glById[id].Amount });
            }

            foreach (var id in p.BankItemIds)
            {
                bankById[id].MatchId = match.Id;
            }

            db.Set<BankMatch>().Add(match);
            created.Add(match);
        }

        if (created.Count > 0)
        {
            audit.Record(db, nameof(BankAccount), state.Account.Id, "AutoMatched", after: new { Matches = created.Count, Rules = created.GroupBy(m => m.Rule.ToString()).ToDictionary(g => g.Key, g => g.Count()) });
        }

        await db.SaveChangesAsync(cancellationToken);
        var after = await State.LoadAsync(db, state.Account.Id, cancellationToken);
        return new AutoMatchResultDto(created.Count, proposals.Sum(p => p.BankItemIds.Count), proposals.Sum(p => p.JournalLineIds.Count),
            created.Select(m => after.MatchDto(after.Matches.First(x => x.Id == m.Id))).ToList());
    }

    public async Task<BankMatchDto> MatchAsync(MatchItemsRequest request, CancellationToken cancellationToken = default)
    {
        var bankIds = (request.BankItemIds ?? []).Distinct().ToList();
        var glIds = (request.JournalLineIds ?? []).Distinct().ToList();
        if (glIds.Count == 0)
        {
            throw new ArgumentException(bankIds.Count == 0 ? "Choose the bank items and GL items to match." : "Choose the GL items these bank items clear (or book the bank item to the GL).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await State.LoadAsync(db, request.BankAccountId, cancellationToken);
        var bank = await db.Set<BankTransaction>().Where(t => bankIds.Contains(t.Id)).ToListAsync(cancellationToken);
        if (bank.Count != bankIds.Count || bank.Any(t => t.BankAccountId != state.Account.Id))
        {
            throw new ArgumentException("Some of the bank items aren't on this bank account.");
        }

        if (bank.FirstOrDefault(t => t.MatchId is not null) is { } taken)
        {
            throw new InvalidOperationException($"The bank item {taken.Date:yyyy-MM-dd} {taken.Description} ({taken.Amount:N2}) is already matched; unmatch it first.");
        }

        var glById = state.Gl.ToDictionary(l => l.LineId);
        var gl = glIds.Select(id => glById.TryGetValue(id, out var l) ? l : throw new ArgumentException("Some of the GL items aren't posted lines on this account's cash account.")).ToList();
        if (gl.FirstOrDefault(l => state.GlMatch.ContainsKey(l.LineId)) is { } matched)
        {
            throw new InvalidOperationException($"The GL item {matched.EntryNumber} ({matched.Amount:N2}) is already matched; unmatch it first.");
        }

        var bankTotal = bank.Sum(t => t.Amount);
        var glTotal = gl.Sum(l => l.Amount);
        if (bankTotal != glTotal)
        {
            throw new ArgumentException($"The two sides don't agree: bank {bankTotal:N2}, GL {glTotal:N2} (difference {bankTotal - glTotal:N2}).");
        }

        var match = NewMatch(state.Account.Id, BankMatchRule.Manual, 100, string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), clock.GetUtcNow().UtcDateTime);
        foreach (var l in gl)
        {
            match.GlLines.Add(new BankMatchLine { MatchId = match.Id, BankAccountId = state.Account.Id, JournalLineId = l.LineId, Amount = l.Amount });
        }

        foreach (var t in bank)
        {
            t.MatchId = match.Id;
        }

        db.Set<BankMatch>().Add(match);
        audit.Record(db, nameof(BankMatch), match.Id, "Matched", after: new { BankItems = bank.Count, GlItems = gl.Count, Amount = bankTotal, match.Note });
        await db.SaveChangesAsync(cancellationToken);
        var after = await State.LoadAsync(db, state.Account.Id, cancellationToken);
        return after.MatchDto(after.Matches.First(m => m.Id == match.Id));
    }

    public async Task UnmatchAsync(Guid matchId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var match = await db.Set<BankMatch>().Include(m => m.GlLines).FirstOrDefaultAsync(m => m.Id == matchId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Match", matchId);
        if (match.ReconciliationId is { } recId)
        {
            var rec = await db.Set<BankReconciliation>().AsNoTracking().FirstAsync(r => r.Id == recId, cancellationToken);
            throw new InvalidOperationException($"This match is locked by the reconciliation through {rec.StatementEndDate:yyyy-MM-dd}; reopen that reconciliation to change it.");
        }

        var bank = await db.Set<BankTransaction>().Where(t => t.MatchId == match.Id).ToListAsync(cancellationToken);
        foreach (var t in bank)
        {
            t.MatchId = null;
        }

        audit.Record(db, nameof(BankMatch), match.Id, "Unmatched", before: new { Rule = match.Rule.ToString(), match.Confidence, BankItems = bank.Count, GlItems = match.GlLines.Count });
        db.Set<BankMatchLine>().RemoveRange(match.GlLines);
        db.Set<BankMatch>().Remove(match);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BookingAccountsDto> GetBookingAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking()
            .Where(a => a.IsActive && (a.Type == AccountType.Expense || a.Type == AccountType.Revenue)).ToListAsync(cancellationToken);
        var parents = accounts.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();
        var detail = accounts.Where(a => !parents.Contains(a.Id) && a.CostClass != CostClassification.AppliedBurden).OrderBy(a => a.Number).ToList();
        var fee = detail.FirstOrDefault(a => a.Number == CashSupport.BankFeeAccountNumber)
            ?? detail.FirstOrDefault(a => a.Type == AccountType.Expense && a.Name.Contains("bank", StringComparison.OrdinalIgnoreCase));
        var interest = detail.FirstOrDefault(a => a.Number == CashSupport.InterestIncomeAccountNumber)
            ?? detail.FirstOrDefault(a => a.Type == AccountType.Revenue && a.Name.Contains("interest", StringComparison.OrdinalIgnoreCase));
        return new BookingAccountsDto(detail.Select(a => new CashGlAccountDto(a.Id, a.Number, a.Name)).ToList(), fee?.Id, interest?.Id);
    }

    public async Task<BankMatchDto> BookBankItemAsync(BookBankItemRequest request, CancellationToken cancellationToken = default)
    {
        BankTransaction item;
        BankAccount account;
        Account offset;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            item = await db.Set<BankTransaction>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.BankItemId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Bank item", request.BankItemId);
            account = await db.Set<BankAccount>().AsNoTracking().FirstAsync(a => a.Id == item.BankAccountId, cancellationToken);
            if (item.MatchId is not null)
            {
                throw new InvalidOperationException($"The bank item {item.Description} is already matched.");
            }

            offset = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.OffsetAccountId, cancellationToken)
                ?? throw new ArgumentException("Choose the account to book the bank item to (bank fees, interest income...).");
            if (offset.Id == account.GlAccountId)
            {
                throw new ArgumentException("Book the bank item to an expense or income account, not to the cash account itself.");
            }
        }

        // Through the journal service (source Bank, one entry per bank item, so a retry returns the same entry).
        var amount = Math.Abs(item.Amount);
        var description = string.IsNullOrWhiteSpace(request.Description) ? $"{Label(item.Type)}: {item.Description}" : request.Description.Trim();
        var memo = $"{account.Name} {item.Date:yyyy-MM-dd}";
        JournalLineInput Cash(bool debit) => new(account.GlAccountId, debit ? amount : 0, debit ? 0 : amount, memo);
        JournalLineInput Offset(bool debit) => new(offset.Id, debit ? amount : 0, debit ? 0 : amount, memo, request.OrgUnitId);
        var entry = await journal.CreateAndPostAsync(new SaveJournalEntryRequest(item.Date, description,
            item.Amount > 0 ? [Cash(true), Offset(false)] : [Offset(true), Cash(false)], JournalEntrySource.Bank, item.Id), cancellationToken);

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var tracked = await db.Set<BankTransaction>().FirstAsync(t => t.Id == item.Id, cancellationToken);
            var cashLine = entry.Lines.First(l => l.AccountId == account.GlAccountId);
            var match = NewMatch(account.Id, BankMatchRule.BookedFromBank, 100, $"Booked as {entry.Entry.Number}", clock.GetUtcNow().UtcDateTime);
            match.GlLines.Add(new BankMatchLine { MatchId = match.Id, BankAccountId = account.Id, JournalLineId = cashLine.Id, Amount = cashLine.Debit - cashLine.Credit });
            tracked.MatchId = match.Id;
            tracked.JournalEntryId = entry.Entry.Id;
            db.Set<BankMatch>().Add(match);
            audit.Record(db, nameof(BankTransaction), item.Id, "Booked", after: new { Entry = entry.Entry.Number, Account = $"{offset.Number} {offset.Name}", item.Amount });
            await db.SaveChangesAsync(cancellationToken);
            var state = await State.LoadAsync(db, account.Id, cancellationToken);
            return state.MatchDto(state.Matches.First(m => m.Id == match.Id));
        }
    }

    public async Task<BankReconciliationDto> CompleteAsync(Guid reconciliationId, CompleteReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rec = await FindAsync(db, reconciliationId, cancellationToken);
        RequireInProgress(rec);
        var state = await State.LoadAsync(db, rec.BankAccountId, cancellationToken);
        var figures = state.Figures(rec);
        if (figures.Unrecorded.Count > 0)
        {
            throw new InvalidOperationException($"{figures.Unrecorded.Count} bank item(s) through {rec.StatementEndDate:yyyy-MM-dd} aren't in the GL yet " +
                $"({figures.UnrecordedBankItems:N2}); match them or book them first.");
        }

        if (figures.Difference != 0)
        {
            throw new InvalidOperationException($"The adjusted balances don't agree: bank {figures.AdjustedBankBalance:N2}, book {figures.AdjustedBookBalance:N2} " +
                $"(difference {figures.Difference:N2}). Check the statement ending balance and the matches.");
        }

        // Lock every open match that has cleared by the statement date.
        var clearedIds = state.ClearedMatchIds(rec.StatementEndDate);
        var toLock = await db.Set<BankMatch>().Where(m => m.BankAccountId == rec.BankAccountId && m.ReconciliationId == null && clearedIds.Contains(m.Id)).ToListAsync(cancellationToken);
        foreach (var m in toLock)
        {
            m.ReconciliationId = rec.Id;
        }

        rec.Status = BankReconciliationStatus.Completed;
        rec.GlBalance = figures.GlBalance;
        rec.DepositsInTransit = figures.DepositsInTransit;
        rec.OutstandingPayments = figures.OutstandingPayments;
        rec.UnrecordedBankItems = figures.UnrecordedBankItems;
        rec.CompletedBy = audit.Actor;
        rec.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            rec.Note = request.Note.Trim();
        }

        audit.Record(db, nameof(BankReconciliation), rec.Id, "Completed", new { Status = nameof(BankReconciliationStatus.InProgress) }, new
        {
            Status = nameof(BankReconciliationStatus.Completed),
            Account = state.Account.Name,
            rec.StatementEndDate,
            rec.StatementEndingBalance,
            rec.GlBalance,
            rec.DepositsInTransit,
            rec.OutstandingPayments,
            LockedMatches = toLock.Count,
        }, rec.Note);
        await db.SaveChangesAsync(cancellationToken);
        var after = await State.LoadAsync(db, rec.BankAccountId, cancellationToken);
        return ToDto(rec, after, after.Compute(rec), withItems: true);
    }

    public async Task<BankReconciliationDto> ReopenAsync(Guid reconciliationId, ReconciliationReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? throw new ArgumentException("A reason is required to reopen a reconciliation.") : request.Reason.Trim();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rec = await FindAsync(db, reconciliationId, cancellationToken);
        if (rec.Status != BankReconciliationStatus.Completed)
        {
            throw new InvalidOperationException("This reconciliation is already open.");
        }

        if (await db.Set<BankReconciliation>().AnyAsync(r => r.BankAccountId == rec.BankAccountId && r.StatementEndDate > rec.StatementEndDate, cancellationToken))
        {
            throw new InvalidOperationException("A later reconciliation exists for this account; reopen (or finish and reopen) the latest one first.");
        }

        var locked = await db.Set<BankMatch>().Where(m => m.ReconciliationId == rec.Id).ToListAsync(cancellationToken);
        foreach (var m in locked)
        {
            m.ReconciliationId = null;
        }

        rec.Status = BankReconciliationStatus.InProgress;
        rec.ReopenedBy = audit.Actor;
        rec.ReopenedAtUtc = clock.GetUtcNow().UtcDateTime;
        rec.ReopenReason = reason;
        audit.Record(db, nameof(BankReconciliation), rec.Id, "Reopened", new { Status = nameof(BankReconciliationStatus.Completed), rec.CompletedBy },
            new { Status = nameof(BankReconciliationStatus.InProgress), UnlockedMatches = locked.Count }, reason);
        await db.SaveChangesAsync(cancellationToken);
        var state = await State.LoadAsync(db, rec.BankAccountId, cancellationToken);
        return ToDto(rec, state, state.Compute(rec), withItems: true);
    }

    public async Task<ReconciliationReportDto> GetReportAsync(Guid reconciliationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rec = await FindAsync(db, reconciliationId, cancellationToken);
        var state = await State.LoadAsync(db, rec.BankAccountId, cancellationToken);
        var math = state.Compute(rec);
        if (rec.Status == BankReconciliationStatus.Completed)
        {
            // The figures as completed; the item lists as they stand (locked items don't move).
            math = Snapshot(rec) with { OutstandingItems = math.OutstandingItems, UnrecordedItems = math.UnrecordedItems };
        }

        // Cleared by this reconciliation: locked by it, or (while open) cleared by the date and not locked by an earlier one.
        var clearedIds = rec.Status == BankReconciliationStatus.Completed
            ? state.Matches.Where(m => m.ReconciliationId == rec.Id).Select(m => m.Id).ToHashSet()
            : state.ClearedMatchIds(rec.StatementEndDate).Where(id => state.Matches.First(m => m.Id == id).ReconciliationId is null).ToHashSet();
        var bank = state.Bank.Where(t => t.MatchId is { } m && clearedIds.Contains(m)).OrderBy(t => t.Date).Select(t => t.ToDto(state.Locked)).ToList();
        var gl = state.Gl.Where(l => state.GlMatch.TryGetValue(l.LineId, out var m) && clearedIds.Contains(m)).Select(state.GlItem).ToList();
        return new ReconciliationReportDto(ToDto(rec, state, math, withItems: true), state.Account.BankName, state.Account.RoutingNumber, bank, gl);
    }

    public async Task<IReadOnlyList<BankCloseWaiverDto>> ListCloseWaiversAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<BankCloseWaiver>().AsNoTracking().Where(w => fiscalPeriodId == null || w.FiscalPeriodId == fiscalPeriodId).ToListAsync(cancellationToken);
        return rows.OrderByDescending(w => w.WaivedAtUtc).Select(ToDto).ToList();
    }

    public async Task<BankCloseWaiverDto> WaiveCloseCheckAsync(WaiveBankCloseCheckRequest request, CancellationToken cancellationToken = default)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? throw new ArgumentException("A reason is required to waive the bank reconciliation check.") : request.Reason.Trim();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.FiscalPeriodId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Fiscal period", request.FiscalPeriodId);
        if (period.Status == PeriodStatus.Closed)
        {
            throw new InvalidOperationException($"{period.Name} is already closed.");
        }

        var waiver = new BankCloseWaiver { FiscalPeriodId = period.Id, Reason = reason, WaivedBy = audit.Actor, WaivedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.Set<BankCloseWaiver>().Add(waiver);
        audit.Record(db, nameof(FiscalPeriod), period.Id, "BankReconciliationWaived", after: new { Period = period.Name }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(waiver);
    }

    private static BankCloseWaiverDto ToDto(BankCloseWaiver w) => new(w.Id, w.FiscalPeriodId, w.Reason, w.WaivedBy, w.WaivedAtUtc);

    private static string Label(BankTransactionType type) => type switch
    {
        BankTransactionType.Fee => "Bank fee",
        BankTransactionType.Interest => "Interest income",
        BankTransactionType.Wire => "Wire",
        _ => "Bank item",
    };

    private BankMatch NewMatch(Guid accountId, BankMatchRule rule, int confidence, string? note, DateTime now) =>
        new() { BankAccountId = accountId, Rule = rule, Confidence = confidence, Note = note, MatchedBy = audit.Actor, MatchedAtUtc = now };

    private static void RequireInProgress(BankReconciliation rec)
    {
        if (rec.Status != BankReconciliationStatus.InProgress)
        {
            throw new InvalidOperationException($"The reconciliation through {rec.StatementEndDate:yyyy-MM-dd} is completed; reopen it (with a reason) to change it.");
        }
    }

    private static async Task<BankReconciliation> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<BankReconciliation>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Reconciliation", id);

    /// <summary>
    /// The bank balance at <paramref name="date"/> from the statements: the last completed reconciliation's ending balance
    /// (or the earliest statement's opening balance) plus the bank items since.
    /// </summary>
    private static decimal StatementBalance(State state, IReadOnlyList<BankReconciliation> recs, DateOnly date)
    {
        var previous = recs.Where(r => r.Status == BankReconciliationStatus.Completed && r.StatementEndDate < date).MaxBy(r => r.StatementEndDate);
        return previous is not null
            ? previous.StatementEndingBalance + state.Bank.Where(t => t.Date > previous.StatementEndDate && t.Date <= date).Sum(t => t.Amount)
            : state.OpeningBalance + state.Bank.Where(t => t.Date <= date).Sum(t => t.Amount);
    }

    private static ReconciliationMathDto Snapshot(BankReconciliation rec) => new(
        rec.StatementEndingBalance, rec.DepositsInTransit, rec.OutstandingPayments,
        rec.StatementEndingBalance + rec.DepositsInTransit - rec.OutstandingPayments,
        rec.GlBalance, rec.UnrecordedBankItems, rec.GlBalance + rec.UnrecordedBankItems, [], []);

    private static BankReconciliationDto ToDto(BankReconciliation r, State state, ReconciliationMathDto math, bool withItems) => new(
        r.Id, r.BankAccountId, state.Account.Name, CashSupport.Mask(state.Account.AccountNumberLast4), state.GlAccountNumber,
        r.StatementEndDate, r.StatementEndingBalance, r.Status.ToContract(), r.StartedBy, r.StartedAtUtc, r.CompletedBy, r.CompletedAtUtc,
        r.ReopenedBy, r.ReopenedAtUtc, r.ReopenReason, r.Note,
        withItems ? math : math with { OutstandingItems = [], UnrecordedItems = [] });

    /// <summary>One bank account's bank items, GL cash lines and matches, loaded once per operation.</summary>
    private sealed class State
    {
        public required BankAccount Account { get; init; }
        public required string GlAccountNumber { get; init; }
        public required List<BankTransaction> Bank { get; init; }
        public required List<GlCashLine> Gl { get; init; }
        public required List<BankMatch> Matches { get; init; }

        /// <summary>GL line → match.</summary>
        public required Dictionary<Guid, Guid> GlMatch { get; init; }

        /// <summary>Matches locked by a completed reconciliation.</summary>
        public required HashSet<Guid> Locked { get; init; }

        /// <summary>The earliest statement's opening balance (0 when none states one).</summary>
        public decimal OpeningBalance { get; init; }

        public static async Task<State> LoadAsync(IWorkbenchDbContext db, Guid bankAccountId, CancellationToken ct)
        {
            var account = await db.Set<BankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == bankAccountId, ct)
                ?? throw WorkbenchNotFoundException.For("Bank account", bankAccountId);
            var glNumber = await db.Set<Account>().AsNoTracking().Where(a => a.Id == account.GlAccountId).Select(a => a.Number).FirstOrDefaultAsync(ct) ?? "?";
            var bank = await db.Set<BankTransaction>().AsNoTracking().Where(t => t.BankAccountId == bankAccountId).ToListAsync(ct);
            var matches = await db.Set<BankMatch>().AsNoTracking().Include(m => m.GlLines).Where(m => m.BankAccountId == bankAccountId).ToListAsync(ct);
            var opening = await db.Set<BankStatementImport>().AsNoTracking().Where(i => i.BankAccountId == bankAccountId && i.OpeningBalance != null)
                .OrderBy(i => i.PeriodStart).Select(i => i.OpeningBalance).FirstOrDefaultAsync(ct);
            return new State
            {
                Account = account,
                GlAccountNumber = glNumber,
                Bank = bank.OrderBy(t => t.Date).ThenBy(t => t.CreatedAtUtc).ToList(),
                Gl = await CashLedger.LoadAsync(db, account.GlAccountId, ct),
                Matches = matches,
                GlMatch = matches.SelectMany(m => m.GlLines.Select(l => (l.JournalLineId, m.Id))).ToDictionary(x => x.JournalLineId, x => x.Id),
                Locked = matches.Where(m => m.ReconciliationId is not null).Select(m => m.Id).ToHashSet(),
                OpeningBalance = opening ?? 0m,
            };
        }

        private List<MatchDates> Dates()
        {
            var glDates = Gl.ToDictionary(l => l.LineId, l => l.Date);
            var bankByMatch = Bank.Where(t => t.MatchId is not null).ToLookup(t => t.MatchId!.Value, t => t.Date);
            return Matches.Select(m => new MatchDates(m.Id, bankByMatch[m.Id].ToList(),
                m.GlLines.Select(l => glDates.TryGetValue(l.JournalLineId, out var d) ? d : DateOnly.MaxValue).ToList())).ToList();
        }

        public HashSet<Guid> ClearedMatchIds(DateOnly end) =>
            Dates().Where(m => m.BankDates.All(d => d <= end) && m.GlDates.All(d => d <= end)).Select(m => m.MatchId).ToHashSet();

        public ReconciliationFigures Figures(BankReconciliation rec) =>
            ReconciliationMath.Compute(rec.StatementEndDate, rec.StatementEndingBalance, Gl, GlMatch, Bank, Dates());

        public ReconciliationMathDto Compute(BankReconciliation rec)
        {
            var f = Figures(rec);
            return new ReconciliationMathDto(f.StatementEndingBalance, f.DepositsInTransit, f.OutstandingPayments, f.AdjustedBankBalance,
                f.GlBalance, f.UnrecordedBankItems, f.AdjustedBookBalance,
                f.Outstanding.Select(GlItem).ToList(), f.Unrecorded.Select(t => t.ToDto(Locked)).ToList());
        }

        public GlCashItemDto GlItem(GlCashLine l)
        {
            var matchId = GlMatch.TryGetValue(l.LineId, out var m) ? m : (Guid?)null;
            return new GlCashItemDto(l.LineId, l.EntryId, l.EntryNumber, l.Date, l.Source.ToContract(), l.Description, l.Reference, l.Amount,
                matchId, matchId is { } id && Locked.Contains(id));
        }

        public BankMatchDto MatchDto(BankMatch m)
        {
            var bank = Bank.Where(t => t.MatchId == m.Id).ToList();
            return new BankMatchDto(m.Id, m.Rule.ToContract(), m.Confidence, m.Note, m.MatchedBy, m.MatchedAtUtc, m.ReconciliationId is not null,
                bank.Select(t => t.Id).ToList(), m.GlLines.Select(l => l.JournalLineId).ToList(), bank.Sum(t => t.Amount), m.GlLines.Sum(l => l.Amount));
        }
    }
}
