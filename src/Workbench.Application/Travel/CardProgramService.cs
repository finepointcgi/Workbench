using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Travel;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Travel;

namespace Workbench.Application.Travel;

/// <summary>Card programs, cards, statement imports and reconciliation (Finance and administrators; see the permission decorator).</summary>
public sealed class CardProgramService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<ICardFeed> feeds,
    AuditRecorder audit,
    ICurrentUser currentUser,
    INotificationPublisher notifications,
    TimeProvider clock) : ICardProgramService
{
    public const string ProgramEntity = nameof(CardProgram);
    public const string CardEntity = nameof(CorporateCard);
    public const string TransactionEntity = nameof(CardTransaction);
    public const int MaxFileBytes = 5 * 1024 * 1024;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<IReadOnlyList<CardProgramDto>> ListProgramsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var programs = await db.Set<CardProgram>().AsNoTracking().ToListAsync(cancellationToken);
        return await ToDtosAsync(db, programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken);
    }

    public async Task<CardProgramDto> CreateProgramAsync(SaveCardProgramRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var program = new CardProgram { Name = "", Issuer = "", CreatedBy = currentUser.Email, CreatedAtUtc = UtcNow };
        await ApplyAsync(db, program, request, cancellationToken);
        db.Set<CardProgram>().Add(program);
        audit.Record(db, ProgramEntity, program.Id, "Created", after: Snapshot(program));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [program], cancellationToken))[0];
    }

    public async Task<CardProgramDto> UpdateProgramAsync(Guid programId, SaveCardProgramRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var program = await db.Set<CardProgram>().FirstOrDefaultAsync(p => p.Id == programId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Card program", programId);
        var before = Snapshot(program);
        if ((CardPaymentKind)(int)program.PaymentResponsibility != request.PaymentResponsibility
            && await db.Set<CardTransaction>().AnyAsync(t => t.ProgramId == programId && t.OdcEntryId != null, cancellationToken))
        {
            throw new ArgumentException("Charges of this program are already on expense reports, so who pays the issuer can't change. Start a new program instead.");
        }

        await ApplyAsync(db, program, request, cancellationToken);
        audit.Record(db, ProgramEntity, program.Id, "Updated", before, Snapshot(program));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [program], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<CorporateCardDto>> ListCardsAsync(Guid? programId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cards = await db.Set<CorporateCard>().AsNoTracking().Where(c => programId == null || c.ProgramId == programId).ToListAsync(cancellationToken);
        return await CardDtosAsync(db, cards, cancellationToken);
    }

    public async Task<CorporateCardDto> CreateCardAsync(SaveCorporateCardRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var card = new CorporateCard { Last4 = "", CreatedAtUtc = UtcNow };
        await ApplyAsync(db, card, request, cancellationToken);
        db.Set<CorporateCard>().Add(card);
        audit.Record(db, CardEntity, card.Id, "Created", after: new { card.ProgramId, card.EmployeeId, card.Last4, card.IsActive });

        // Earlier charges of this card that nobody could match now belong to its holder.
        var unmatched = await db.Set<CardTransaction>().Where(t => t.ProgramId == card.ProgramId && t.CardId == null && t.CardLast4 == card.Last4).ToListAsync(cancellationToken);
        foreach (var t in unmatched)
        {
            (t.CardId, t.EmployeeId) = (card.Id, card.EmployeeId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await CardDtosAsync(db, [card], cancellationToken))[0];
    }

    public async Task<CorporateCardDto> UpdateCardAsync(Guid cardId, SaveCorporateCardRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var card = await db.Set<CorporateCard>().FirstOrDefaultAsync(c => c.Id == cardId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Card", cardId);
        var before = new { card.ProgramId, card.EmployeeId, card.Last4, card.IsActive };
        if ((request.ProgramId != card.ProgramId || request.EmployeeId != card.EmployeeId)
            && await db.Set<CardTransaction>().AnyAsync(t => t.CardId == cardId, cancellationToken))
        {
            throw new ArgumentException("This card has charges, so its program and holder can't change. Deactivate it and add a new card.");
        }

        await ApplyAsync(db, card, request, cancellationToken);
        audit.Record(db, CardEntity, card.Id, "Updated", before, new { card.ProgramId, card.EmployeeId, card.Last4, card.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return (await CardDtosAsync(db, [card], cancellationToken))[0];
    }

    public async Task<CardImportResultDto> ImportStatementAsync(Guid programId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = CardFeeds.Clean(Path.GetFileName(fileName ?? ""), 255) ?? "statement";
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("The file is empty.");
        }

        if (buffer.Length > MaxFileBytes)
        {
            throw new ArgumentException("Statement files can be up to 5 MB.");
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var feed = feeds.FirstOrDefault(f => f.CanRead(name, text))
            ?? throw new ArgumentException("Workbench reads card statements as CSV or OFX/QFX files. Export one of those from the card issuer's site.");
        var read = feed.Read(text);
        if (read.Transactions.Count == 0)
        {
            throw new ArgumentException("The file has no card transactions." + (read.Warnings.Count > 0 ? " " + read.Warnings[0] : ""));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var program = await db.Set<CardProgram>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == programId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Card program", programId);
        if (!program.IsActive)
        {
            throw new ArgumentException($"{program.Name} is inactive.");
        }

        var keys = CardFeeds.DedupeKeys(read.Transactions);
        var known = (await db.Set<CardTransaction>().AsNoTracking().Where(t => t.ProgramId == programId && keys.Contains(t.DedupeKey))
            .Select(t => t.DedupeKey).ToListAsync(cancellationToken)).ToHashSet();
        var cards = (await db.Set<CorporateCard>().AsNoTracking().Where(c => c.ProgramId == programId).ToListAsync(cancellationToken))
            .GroupBy(c => c.Last4).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.IsActive).ToList());
        var now = UtcNow;
        var import = new CardStatementImport { ProgramId = programId, FileName = name, Format = feed.Format, RowCount = read.Transactions.Count, ImportedBy = currentUser.Email, ImportedAtUtc = now };
        var warnings = new List<string>(read.Warnings);
        var added = new List<CardTransaction>();
        for (var i = 0; i < read.Transactions.Count; i++)
        {
            var row = read.Transactions[i];
            if (!known.Add(keys[i]))
            {
                import.DuplicateCount++;
                continue;
            }

            var matches = cards.GetValueOrDefault(row.CardLast4) ?? [];
            var card = matches.Count == 1 || (matches.Count > 1 && matches.Count(c => c.IsActive) == 1) ? matches[0] : null;
            if (card is null)
            {
                import.UnmatchedCount++;
            }

            added.Add(new CardTransaction
            {
                ProgramId = programId, ImportId = import.Id, CardId = card?.Id, EmployeeId = card?.EmployeeId, CardLast4 = row.CardLast4, DedupeKey = keys[i],
                ExternalId = row.ExternalId, TransactionDate = row.TransactionDate, PostedDate = row.PostedDate, Amount = row.Amount, Merchant = row.Merchant,
                MerchantCategory = row.MerchantCategory, CreatedAtUtc = now,
            });
        }

        import.ImportedCount = added.Count;
        import.ImportedTotal = added.Sum(t => t.Amount);
        if (import.UnmatchedCount > 0)
        {
            var digits = added.Where(t => t.CardId == null).Select(t => t.CardLast4.Length == 0 ? "(none)" : t.CardLast4).Distinct();
            warnings.Add($"{import.UnmatchedCount} charge(s) don't match a card in {program.Name} (last four digits {string.Join(", ", digits)}). Add the card or assign them.");
        }

        db.Set<CardStatementImport>().Add(import);
        db.Set<CardTransaction>().AddRange(added);
        audit.Record(db, ProgramEntity, programId, "StatementImported", null,
            new { import.FileName, import.Format, import.RowCount, import.ImportedCount, import.DuplicateCount, import.UnmatchedCount });
        await db.SaveChangesAsync(cancellationToken);

        foreach (var holder in added.Where(t => t.EmployeeId != null && t.Amount > 0).GroupBy(t => t.EmployeeId!.Value))
        {
            var email = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == holder.Key).Select(e => e.Email).FirstOrDefaultAsync(cancellationToken);
            if (email is not null && !string.Equals(email, currentUser.Email, StringComparison.OrdinalIgnoreCase))
            {
                await notifications.PublishAsync(email, "card.charges", $"{holder.Count()} new card charge(s) to add to an expense report",
                    $"{program.Name}: {holder.Sum(t => t.Amount):C}.", link: "expenses?tab=cards");
            }
        }

        return new CardImportResultDto(import.Id, import.Format, import.RowCount, import.ImportedCount, import.DuplicateCount, import.UnmatchedCount, import.ImportedTotal, warnings);
    }

    public async Task<IReadOnlyList<CardStatementImportDto>> ListImportsAsync(Guid? programId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var imports = await db.Set<CardStatementImport>().AsNoTracking().Where(i => programId == null || i.ProgramId == programId).ToListAsync(cancellationToken);
        var names = await db.Set<CardProgram>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        return imports.OrderByDescending(i => i.ImportedAtUtc)
            .Select(i => new CardStatementImportDto(i.Id, i.ProgramId, names.GetValueOrDefault(i.ProgramId) ?? "", i.FileName, i.Format, i.RowCount, i.ImportedCount,
                i.DuplicateCount, i.UnmatchedCount, i.ImportedTotal, i.ImportedBy, i.ImportedAtUtc))
            .ToList();
    }

    public async Task<IReadOnlyList<CardTransactionDto>> ListTransactionsAsync(CardTransactionQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new CardTransactionQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<CardTransaction>().AsNoTracking();
        if (query.ProgramId is { } program)
        {
            q = q.Where(t => t.ProgramId == program);
        }

        if (query.EmployeeId is { } employee)
        {
            q = q.Where(t => t.EmployeeId == employee);
        }

        if (query.ImportId is { } import)
        {
            q = q.Where(t => t.ImportId == import);
        }

        if (query.UnmatchedOnly)
        {
            q = q.Where(t => t.CardId == null);
        }

        var rows = (await q.ToListAsync(cancellationToken)).OrderByDescending(t => t.TransactionDate).ThenBy(t => t.Merchant).ToList();
        var dtos = await CardSupport.ToDtosAsync(db, rows, Today, cancellationToken);
        return dtos.Where(d => query.State is null || d.State == query.State).Take(Math.Clamp(query.Take, 1, 5000)).ToList();
    }

    public async Task<CardAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var today = asOf ?? Today;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<CardTransaction>().AsNoTracking().Where(t => !t.IsExcluded && t.Amount > 0 && t.TransactionDate <= today).ToListAsync(cancellationToken);
        var open = (await CardSupport.ToDtosAsync(db, rows, today, cancellationToken)).Where(d => CardSupport.IsUnsubmitted(d.State)).ToList();
        var result = open.GroupBy(d => d.EmployeeId)
            .Select(g => new CardAgingRowDto(g.Key, g.First().EmployeeName ?? "Unmatched card", string.Join(", ", g.Select(d => $"••{d.CardLast4}").Distinct()), g.Count(),
                g.Where(d => d.AgeDays <= 30).Sum(d => d.Amount), g.Where(d => d.AgeDays is > 30 and <= 60).Sum(d => d.Amount),
                g.Where(d => d.AgeDays is > 60 and <= 90).Sum(d => d.Amount), g.Where(d => d.AgeDays > 90).Sum(d => d.Amount), g.Count(d => d.IsOverdue)))
            .OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Total).ToList();
        return new CardAgingDto(today, result, result.Sum(r => r.Overdue));
    }

    public async Task<IReadOnlyList<CardReconciliationDto>> GetReconciliationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var programs = await db.Set<CardProgram>().AsNoTracking().ToListAsync(cancellationToken);
        var rows = await db.Set<CardTransaction>().AsNoTracking().ToListAsync(cancellationToken);
        var dtos = await CardSupport.ToDtosAsync(db, rows, Today, cancellationToken);
        return programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var mine = dtos.Where(d => d.ProgramId == p.Id).ToList();
            decimal Sum(params CardChargeState[] states) => mine.Where(d => states.Contains(d.State)).Sum(d => d.Amount);
            return new CardReconciliationDto(p.Id, p.Name, (CardPaymentKind)(int)p.PaymentResponsibility, mine.Count, mine.Sum(d => d.Amount),
                Sum(CardChargeState.Unassigned), Sum(CardChargeState.OnDraftReport, CardChargeState.Submitted), Sum(CardChargeState.Approved), Sum(CardChargeState.Excluded),
                mine.Count(d => d.CardId == null && d.State != CardChargeState.Excluded), mine.Count(d => d.IsOverdue));
        }).ToList();
    }

    public async Task<CardTransactionDto> ExcludeTransactionAsync(Guid transactionId, ExcludeCardTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var reason = CardFeeds.Clean(request?.Reason, 500) ?? throw new ArgumentException("Say why the charge is excluded (for example \"Duplicate of the March statement\").");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var t = await FindAsync(db, transactionId, cancellationToken);
        if (t.OdcEntryId is { } line && await db.Set<OdcEntry>().AnyAsync(o => o.Id == line, cancellationToken))
        {
            throw new ArgumentException("The charge is on an expense report. Take it off the report first.");
        }

        (t.OdcEntryId, t.IsExcluded, t.ExcludedReason, t.ExcludedBy, t.ExcludedAtUtc) = (null, true, reason, currentUser.Email, UtcNow);
        audit.Record(db, TransactionEntity, t.Id, "Excluded", null, new { t.Merchant, t.Amount }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await CardSupport.ToDtosAsync(db, [t], Today, cancellationToken))[0];
    }

    public async Task<CardTransactionDto> RestoreTransactionAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var t = await FindAsync(db, transactionId, cancellationToken);
        if (!t.IsExcluded)
        {
            throw new InvalidOperationException("The charge isn't excluded.");
        }

        (t.IsExcluded, t.ExcludedReason, t.ExcludedBy, t.ExcludedAtUtc) = (false, null, null, null);
        audit.Record(db, TransactionEntity, t.Id, "Restored");
        await db.SaveChangesAsync(cancellationToken);
        return (await CardSupport.ToDtosAsync(db, [t], Today, cancellationToken))[0];
    }

    public async Task<CardTransactionDto> AssignCardAsync(Guid transactionId, AssignCardRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var t = await FindAsync(db, transactionId, cancellationToken);
        if (t.OdcEntryId is not null)
        {
            throw new ArgumentException("The charge is already on an expense report.");
        }

        var card = await db.Set<CorporateCard>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CardId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Card", request.CardId);
        if (card.ProgramId != t.ProgramId)
        {
            throw new ArgumentException("Choose a card in the charge's own program.");
        }

        var before = new { t.CardId, t.EmployeeId };
        (t.CardId, t.EmployeeId) = (card.Id, card.EmployeeId);
        audit.Record(db, TransactionEntity, t.Id, "CardAssigned", before, new { t.CardId, t.EmployeeId });
        await db.SaveChangesAsync(cancellationToken);
        return (await CardSupport.ToDtosAsync(db, [t], Today, cancellationToken))[0];
    }

    // ---- Helpers ----

    private static async Task<CardTransaction> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<CardTransaction>().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Card charge", id);

    private async Task ApplyAsync(IWorkbenchDbContext db, CardProgram program, SaveCardProgramRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = CardFeeds.Clean(request.Name, 200) ?? throw new ArgumentException("Name the card program.");
        var issuer = CardFeeds.Clean(request.Issuer, 200) ?? throw new ArgumentException("Name the card issuer (the bank).");
        if (request.UnsubmittedAlertDays is < 1 or > 365)
        {
            throw new ArgumentException("Overdue after must be between 1 and 365 days.");
        }

        if (await db.Set<CardProgram>().AnyAsync(p => p.Name == name && p.Id != program.Id, ct))
        {
            throw new ArgumentException($"There's already a card program named \"{name}\".");
        }

        if (request.VendorId is { } vendorId && !await db.Set<Vendor>().AnyAsync(v => v.Id == vendorId, ct))
        {
            throw WorkbenchNotFoundException.For("Vendor", vendorId);
        }

        Guid? liability = null;
        if (request.LiabilityAccountId is { } accountId)
        {
            var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct) ?? throw WorkbenchNotFoundException.For("Account", accountId);
            if (!account.IsActive || account.Type != AccountType.Liability || await db.Set<Account>().AnyAsync(a => a.ParentId == accountId, ct))
            {
                throw new ArgumentException($"{account.Number} {account.Name} must be an active liability detail account.");
            }

            liability = account.Id;
        }
        else if (request.PaymentResponsibility == CardPaymentKind.CompanyPaid)
        {
            liability = (await CardSupport.EnsureLiabilityAccountAsync(db, UtcNow, ct)).Id;
        }

        program.Name = name;
        program.Issuer = issuer;
        program.PaymentResponsibility = (CardPaymentResponsibility)(int)request.PaymentResponsibility;
        program.VendorId = request.VendorId;
        program.LiabilityAccountId = liability;
        program.UnsubmittedAlertDays = request.UnsubmittedAlertDays;
        program.IsActive = request.IsActive;
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, CorporateCard card, SaveCorporateCardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var last4 = (request.Last4 ?? "").Trim();
        if (last4.Length != 4 || !last4.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Enter the card's last four digits (only those are stored).");
        }

        if (!await db.Set<CardProgram>().AnyAsync(p => p.Id == request.ProgramId, ct))
        {
            throw WorkbenchNotFoundException.For("Card program", request.ProgramId);
        }

        if (!await db.Set<Employee>().AnyAsync(e => e.Id == request.EmployeeId, ct))
        {
            throw WorkbenchNotFoundException.For("Employee", request.EmployeeId);
        }

        if (request.IsActive && await db.Set<CorporateCard>().AnyAsync(c => c.ProgramId == request.ProgramId && c.Last4 == last4 && c.IsActive && c.Id != card.Id, ct))
        {
            throw new ArgumentException($"Another active card in this program ends in {last4}; statements couldn't tell them apart.");
        }

        card.ProgramId = request.ProgramId;
        card.EmployeeId = request.EmployeeId;
        card.Last4 = last4;
        card.NameOnCard = CardFeeds.Clean(request.NameOnCard, 200);
        card.IsActive = request.IsActive;
    }

    private static object Snapshot(CardProgram p) => new
    {
        p.Name, p.Issuer, p.VendorId, PaymentResponsibility = p.PaymentResponsibility.ToString(), p.LiabilityAccountId, p.UnsubmittedAlertDays, p.IsActive,
    };

    private static async Task<IReadOnlyList<CardProgramDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<CardProgram> programs, CancellationToken ct)
    {
        var vendorIds = programs.Where(p => p.VendorId != null).Select(p => p.VendorId!.Value).ToList();
        var accountIds = programs.Where(p => p.LiabilityAccountId != null).Select(p => p.LiabilityAccountId!.Value).ToList();
        var ids = programs.Select(p => p.Id).ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => $"{a.Number} {a.Name}", ct);
        var counts = (await db.Set<CorporateCard>().AsNoTracking().Where(c => ids.Contains(c.ProgramId) && c.IsActive).Select(c => c.ProgramId).ToListAsync(ct))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return programs.Select(p => new CardProgramDto(p.Id, p.Name, p.Issuer, p.VendorId, p.VendorId is { } v ? vendors.GetValueOrDefault(v) : null,
            (CardPaymentKind)(int)p.PaymentResponsibility, p.LiabilityAccountId, p.LiabilityAccountId is { } a ? accounts.GetValueOrDefault(a) : null,
            p.UnsubmittedAlertDays, p.IsActive, counts.GetValueOrDefault(p.Id))).ToList();
    }

    internal static async Task<IReadOnlyList<CorporateCardDto>> CardDtosAsync(IWorkbenchDbContext db, IReadOnlyList<CorporateCard> cards, CancellationToken ct)
    {
        var programIds = cards.Select(c => c.ProgramId).Distinct().ToList();
        var employeeIds = cards.Select(c => c.EmployeeId).Distinct().ToList();
        var programs = await db.Set<CardProgram>().AsNoTracking().Where(p => programIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var names = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        return cards.OrderBy(c => programs[c.ProgramId].Name).ThenBy(c => names.GetValueOrDefault(c.EmployeeId))
            .Select(c => new CorporateCardDto(c.Id, c.ProgramId, programs[c.ProgramId].Name, (CardPaymentKind)(int)programs[c.ProgramId].PaymentResponsibility,
                c.EmployeeId, names.GetValueOrDefault(c.EmployeeId) ?? "", c.Last4, c.NameOnCard, c.IsActive))
            .ToList();
    }
}
