using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Currencies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Currencies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Currencies;

/// <summary>
/// The period-end unrealized revaluation (Costpoint "Revalue foreign currency"): open foreign AR, AP and bank balances at
/// the month-end rate, posted on the period's last day against the unrealized gain/loss account and reversed on the next
/// period's first day, so documents keep their own rate and the realized gain or loss on payment stays right.
/// </summary>
public sealed class RevaluationService(IWorkbenchDbContextFactory dbFactory, GeneralLedger ledger, AuditRecorder audit, TimeProvider clock) : IRevaluationService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<RevaluationSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var runs = await db.Set<CurrencyRevaluation>().AsNoTracking().ToListAsync(cancellationToken);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var entryIds = runs.Select(r => r.JournalEntryId).Concat(runs.Where(r => r.ReversalEntryId != null).Select(r => r.ReversalEntryId!.Value)).ToList();
        var numbers = await db.Set<JournalEntry>().AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, cancellationToken);
        return runs.OrderByDescending(r => r.RateDate).Select(r => new RevaluationSummaryDto(
            r.Id, r.FiscalPeriodId, periods.GetValueOrDefault(r.FiscalPeriodId) ?? "", r.RateDate, r.NetGainLoss,
            numbers.GetValueOrDefault(r.JournalEntryId) ?? "", r.ReversalEntryId is { } rid ? numbers.GetValueOrDefault(rid) : null, r.PostedBy, r.PostedAtUtc)).ToList();
    }

    public async Task<RevaluationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        return await PreviewAsync(db, period, cancellationToken);
    }

    public async Task<RevaluationPreviewDto> PostAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await CurrencySupport.EnsureGainLossAccountsAsync(dbFactory, UtcNow, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        if (await db.Set<CurrencyRevaluation>().AnyAsync(r => r.FiscalPeriodId == period.Id, cancellationToken))
        {
            throw new InvalidOperationException($"{period.Name} is already revalued.");
        }

        if (period.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed; reopen it to revalue.");
        }

        var next = await NextPeriodAsync(db, period, cancellationToken)
            ?? throw new ArgumentException($"There's no fiscal period after {period.Name} to reverse the revaluation in. Add the next fiscal year under Setup › Fiscal calendar.");
        if (next.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {next.Name} is closed, so the revaluation can't be reversed in it.");
        }

        var (balances, _) = await RevaluationEngine.OpenBalancesAsync(db, period.End, cancellationToken);
        var revalued = balances.Where(b => b.GainLoss is { } g && g != 0).ToList();
        if (revalued.Count == 0)
        {
            throw new ArgumentException(balances.Count == 0
                ? $"There are no open foreign currency balances at {period.End:yyyy-MM-dd}, so there's nothing to revalue."
                : "The month-end rates don't change any balance (or rates are missing), so there's nothing to post.");
        }

        var (_, unrealized) = await CurrencySupport.GainLossAccountsAsync(db, UtcNow, cancellationToken);
        var run = new CurrencyRevaluation { FiscalPeriodId = period.Id, RateDate = period.End, PostedBy = audit.Actor, PostedAtUtc = UtcNow };
        var entry = new JournalEntry
        {
            Date = period.End,
            Source = JournalSource.Revaluation,
            SourceId = run.Id,
            Description = $"Foreign currency revaluation {period.Name} at month-end rates",
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };

        // One line per account and currency (the documents are on the run's lines); functional only, so a revalued bank
        // account's balance in its own currency doesn't move.
        foreach (var g in revalued.GroupBy(b => (b.AccountId, b.CurrencyCode, b.Kind)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.CurrencyCode, StringComparer.Ordinal))
        {
            var net = g.Sum(b => b.GainLoss!.Value);
            if (net != 0)
            {
                entry.Lines.Add(CurrencySupport.Line(entry, g.Key.AccountId, net, $"Revalue {g.Count()} {g.Key.Kind.ToString().ToLowerInvariant()} balance(s) in {g.Key.CurrencyCode} at {g.First().Rate:0.######}"));
            }
        }

        var total = revalued.Sum(b => b.GainLoss!.Value);
        if (total != 0)
        {
            entry.Lines.Add(CurrencySupport.Line(entry, unrealized.Id, -total, total > 0 ? "Unrealized foreign exchange gain" : "Unrealized foreign exchange loss"));
        }

        if (entry.Lines.Count < 2)
        {
            throw new ArgumentException("The gains and losses offset exactly, so there's nothing to post.");
        }

        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, cancellationToken);
        var reversal = await ledger.ReverseAsync(db, entry, next.Start, "Automatic reversal of the period-end foreign currency revaluation", cancellationToken);

        run.JournalEntryId = entry.Id;
        run.ReversalEntryId = reversal.Id;
        run.NetGainLoss = total;
        foreach (var b in revalued)
        {
            run.Lines.Add(new CurrencyRevaluationLine
            {
                RevaluationId = run.Id,
                Kind = b.Kind,
                DocumentId = b.DocumentId,
                DocumentNumber = b.DocumentNumber.Length > 100 ? b.DocumentNumber[..100] : b.DocumentNumber,
                CurrencyCode = b.CurrencyCode,
                TransactionBalance = b.TransactionBalance,
                CarryingAmount = b.CarryingAmount,
                Rate = b.Rate!.Value,
                RevaluedAmount = b.RevaluedAmount!.Value,
                GainLoss = b.GainLoss!.Value,
                AccountId = b.AccountId,
            });
        }

        db.Set<CurrencyRevaluation>().Add(run);
        audit.Record(db, CurrencySupport.AuditEntityRevaluation, run.Id, "Posted",
            after: new { Period = period.Name, run.RateDate, run.NetGainLoss, Balances = revalued.Count, Journal = entry.Number, Reversal = reversal.Number });
        await db.SaveChangesAsync(cancellationToken);
        return await PreviewAsync(db, period, cancellationToken);
    }

    public async Task<ForeignCurrencyReportDto> GetReportAsync(DateOnly asOf, DateOnly? from = null, CancellationToken cancellationToken = default)
    {
        var start = from ?? new DateOnly(asOf.Year, asOf.Month, 1);
        if (start > asOf)
        {
            throw new ArgumentException("The start date is after the as-of date.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var functional = await CurrencySupport.FunctionalAsync(db, cancellationToken);
        var (balances, warnings) = await RevaluationEngine.OpenBalancesAsync(db, asOf, cancellationToken, latestRate: true);
        var documents = balances.Select(b => new ForeignDocumentDto(b.Kind.ToContract(), b.DocumentId, b.DocumentNumber, b.Party, b.Date, b.CurrencyCode, b.DocumentRate,
            b.TransactionAmount, b.TransactionBalance, b.CarryingAmount, b.Rate, b.RevaluedAmount, b.GainLoss)).ToList();
        var byCurrency = balances.GroupBy(b => b.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var ar = g.Where(b => b.Kind == RevaluationItemKind.Receivable).Sum(b => b.TransactionBalance);
            var ap = g.Where(b => b.Kind == RevaluationItemKind.Payable).Sum(b => b.TransactionBalance);
            var bank = g.Where(b => b.Kind == RevaluationItemKind.Bank).Sum(b => b.TransactionBalance);
            var rate = g.First().Rate;
            var net = ar + bank - ap;
            return new ForeignCurrencyExposureDto(g.Key, ar, ap, bank, net, rate, rate is { } r ? CurrencyMath.ToFunctional(net, r) : null);
        }).ToList();

        var realized = new List<RealizedGainLossDto>();
        var apPayments = await db.Set<ApPayment>().AsNoTracking()
            .Where(p => p.CurrencyCode != null && p.Status == ApPaymentStatus.Paid && p.PaidOn >= start && p.PaidOn <= asOf).ToListAsync(cancellationToken);
        var arPayments = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.CurrencyCode != null && !p.IsVoid && p.ReceivedOn >= start && p.ReceivedOn <= asOf).ToListAsync(cancellationToken);
        var billIds = apPayments.Select(p => p.BillId).ToList();
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var entryIds = apPayments.Select(p => p.JournalEntryId).Concat(arPayments.Select(p => p.JournalEntryId)).OfType<Guid>().ToList();
        var numbers = await db.Set<JournalEntry>().AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, cancellationToken);
        realized.AddRange(apPayments.Select(p => new RealizedGainLossDto(p.PaidOn!.Value, $"{bills.GetValueOrDefault(p.BillId)?.Number} payment {p.Reference}",
            vendors.GetValueOrDefault(p.VendorId) ?? "", p.CurrencyCode!, p.TransactionAmount, bills.GetValueOrDefault(p.BillId)?.ExchangeRate ?? 0, p.ExchangeRate,
            p.RealizedGainLoss, p.JournalEntryId is { } j ? numbers.GetValueOrDefault(j) : null)));
        realized.AddRange(arPayments.Select(p => new RealizedGainLossDto(p.ReceivedOn, $"Receipt {p.Reference}".Trim(), customers.GetValueOrDefault(p.CustomerId) ?? "",
            p.CurrencyCode!, p.TransactionAmount, p.TransactionAmount == 0 ? 0 : Math.Round((p.Amount - p.RealizedGainLoss) / p.TransactionAmount, CurrencyMath.RateDecimals),
            p.ExchangeRate, p.RealizedGainLoss, p.JournalEntryId is { } j ? numbers.GetValueOrDefault(j) : null)));
        realized = realized.OrderBy(r => r.Date).ThenBy(r => r.Document, StringComparer.Ordinal).ToList();

        return new ForeignCurrencyReportDto(start, asOf, functional, byCurrency, documents, realized,
            realized.Sum(r => r.GainLoss), documents.Sum(d => d.UnrealizedGainLoss ?? 0), warnings);
    }

    // ---- Helpers ----

    private async Task<RevaluationPreviewDto> PreviewAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var functional = await CurrencySupport.FunctionalAsync(db, ct);
        var next = await NextPeriodAsync(db, period, ct);
        var reverseOn = next?.Start ?? period.End.AddDays(1);
        var run = await db.Set<CurrencyRevaluation>().AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.FiscalPeriodId == period.Id, ct);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Number, ct);
        if (run is not null)
        {
            var parties = await PartiesAsync(db, run.Lines.Select(l => (l.Kind, l.DocumentId)).ToList(), ct);
            var lines = run.Lines.OrderBy(l => l.Kind).ThenBy(l => l.DocumentNumber, StringComparer.Ordinal).Select(l => new RevaluationLineDto(
                l.Kind.ToContract(), l.DocumentId, l.DocumentNumber, parties.GetValueOrDefault(l.DocumentId) ?? "", l.CurrencyCode, l.TransactionBalance,
                l.CarryingAmount, l.Rate, l.RevaluedAmount, l.GainLoss, accounts.GetValueOrDefault(l.AccountId) ?? "")).ToList();
            var ids = new[] { run.JournalEntryId, run.ReversalEntryId ?? Guid.Empty };
            var numbers = await db.Set<JournalEntry>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
            return new RevaluationPreviewDto(period.Id, period.Name, run.RateDate, reverseOn, functional, lines,
                lines.Where(l => l.GainLoss > 0).Sum(l => l.GainLoss), lines.Where(l => l.GainLoss < 0).Sum(l => l.GainLoss), run.NetGainLoss, [],
                true, run.JournalEntryId, numbers.GetValueOrDefault(run.JournalEntryId), run.ReversalEntryId,
                run.ReversalEntryId is { } rid ? numbers.GetValueOrDefault(rid) : null, run.PostedBy, run.PostedAtUtc, false);
        }

        var (balances, warnings) = await RevaluationEngine.OpenBalancesAsync(db, period.End, ct);
        if (period.Status != PeriodStatus.Open)
        {
            warnings.Add($"{period.Name} is closed; reopen it to revalue.");
        }

        if (next is null)
        {
            warnings.Add($"There's no fiscal period after {period.Name} to reverse the revaluation in.");
        }
        else if (next.Status != PeriodStatus.Open)
        {
            warnings.Add($"{next.Name} is closed, so the reversal can't post.");
        }

        var preview = balances.Select(b => new RevaluationLineDto(b.Kind.ToContract(), b.DocumentId, b.DocumentNumber, b.Party, b.CurrencyCode, b.TransactionBalance,
            b.CarryingAmount, b.Rate ?? 0, b.RevaluedAmount ?? b.CarryingAmount, b.GainLoss ?? 0, b.AccountNumber)).ToList();
        var canPost = period.Status == PeriodStatus.Open && next?.Status == PeriodStatus.Open && preview.Any(l => l.GainLoss != 0);
        return new RevaluationPreviewDto(period.Id, period.Name, period.End, reverseOn, functional, preview,
            preview.Where(l => l.GainLoss > 0).Sum(l => l.GainLoss), preview.Where(l => l.GainLoss < 0).Sum(l => l.GainLoss), preview.Sum(l => l.GainLoss), warnings,
            false, null, null, null, null, null, null, canPost);
    }

    private static async Task<Dictionary<Guid, string>> PartiesAsync(IWorkbenchDbContext db, IReadOnlyList<(RevaluationItemKind Kind, Guid Id)> docs, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        var invoiceIds = docs.Where(d => d.Kind == RevaluationItemKind.Receivable).Select(d => d.Id).ToList();
        foreach (var x in await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id))
                     .Join(db.Set<Customer>(), i => i.CustomerId, c => c.Id, (i, c) => new { i.Id, c.Name }).ToListAsync(ct))
        {
            result[x.Id] = x.Name;
        }

        var billIds = docs.Where(d => d.Kind == RevaluationItemKind.Payable).Select(d => d.Id).ToList();
        foreach (var x in await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id))
                     .Join(db.Set<Vendor>(), b => b.VendorId, v => v.Id, (b, v) => new { b.Id, v.Name }).ToListAsync(ct))
        {
            result[x.Id] = x.Name;
        }

        var bankIds = docs.Where(d => d.Kind == RevaluationItemKind.Bank).Select(d => d.Id).ToList();
        foreach (var x in await db.Set<Domain.Cash.BankAccount>().AsNoTracking().Where(b => bankIds.Contains(b.Id)).Select(b => new { b.Id, b.BankName }).ToListAsync(ct))
        {
            result[x.Id] = x.BankName;
        }

        return result;
    }

    private static async Task<FiscalPeriod> FindPeriodAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Fiscal period", id);

    private static async Task<FiscalPeriod?> NextPeriodAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var day = period.End.AddDays(1);
        return await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= day && p.End >= day, ct);
    }
}

/// <summary>
/// Period close: open foreign currency balances are revalued at month end. Passes when the period has a revaluation, when
/// there's nothing in another currency to revalue, or when no revaluation can post (no open period after it to reverse in).
/// </summary>
public sealed class CurrencyRevaluationCloseCheck : IPeriodCloseCheck
{
    public const string Title = "Foreign currency balances revalued";

    public async Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var run = await db.Set<CurrencyRevaluation>().AsNoTracking().FirstOrDefaultAsync(r => r.FiscalPeriodId == period.Id, ct);
        if (run is not null)
        {
            return new CloseCheckDto(CloseCheckKind.CurrencyRevalued, Title, true, 0, $"Revalued by {run.PostedBy}: net {(run.NetGainLoss >= 0 ? "gain" : "loss")} {Math.Abs(run.NetGainLoss):N2}", []);
        }

        var (balances, _) = await RevaluationEngine.OpenBalancesAsync(db, period.End, ct);
        var changing = balances.Where(b => b.GainLoss is { } g && g != 0).ToList();
        if (changing.Count == 0)
        {
            return new CloseCheckDto(CloseCheckKind.CurrencyRevalued, Title, true, 0,
                balances.Count == 0 ? "No open foreign currency balances" : "Month-end rates don't change any foreign balance", []);
        }

        // Without an open period after this one a revaluation can't be reversed, so it can't be posted and doesn't block.
        var day = period.End.AddDays(1);
        var next = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= day && p.End >= day, ct);
        if (next is not { Status: PeriodStatus.Open })
        {
            return new CloseCheckDto(CloseCheckKind.CurrencyRevalued, Title, true, changing.Count,
                $"{changing.Count} foreign currency balance(s) would change, but there's no open period after {period.Name} to reverse a revaluation in", []);
        }

        return new CloseCheckDto(CloseCheckKind.CurrencyRevalued, Title, false, changing.Count,
            $"{changing.Count} foreign currency balance(s) to revalue; post the revaluation under Accounting › Currency revaluation",
            changing.Take(20).Select(b => $"{b.DocumentNumber}: {b.TransactionBalance:N2} {b.CurrencyCode}, {(b.GainLoss > 0 ? "gain" : "loss")} {Math.Abs(b.GainLoss!.Value):N2}").ToList());
    }
}
