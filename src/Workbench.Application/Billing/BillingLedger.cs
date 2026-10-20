using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Currencies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Posts billing documents through the general ledger in the caller's unit of work, so an invoice is never approved
/// without its entry. Idempotent per source document: an invoice or payment that already has a posted entry
/// (source Billing, source id = the document) gets that entry back.
/// <list type="bullet">
/// <item>Invoice: debit AR billed (1110) for the total; credit revenue by CLIN pricing (4010 cost-plus, 4020 T&amp;M,
/// 4030 labor hour, 4040 FFP) by project/WBS; withheld fee is earned but not billable, so it debits AR unbilled (1120)
/// and its release credits it.</item>
/// <item>Contract financing: progress and performance-based payment lines credit unliquidated contract financing (2320),
/// a liability, not revenue; liquidation lines on delivery invoices (negative) debit it back.</item>
/// <item>Payment: debit cash (1010), credit AR billed.</item>
/// <item>Unbilled accrual: debit AR unbilled, credit revenue; reversed on the first day of the next period.</item>
/// </list>
/// </summary>
public sealed class BillingLedger(GeneralLedger ledger)
{
    public const string Cash = "1010";
    public const string ArBilled = "1110";
    public const string ArUnbilled = "1120";
    public const string ContractFinancing = "2320";

    public string Actor => ledger.Actor;

    public DateTime UtcNow => ledger.UtcNow;

    public static string RevenueAccount(ClinPricing? pricing, ContractType contractType) => pricing switch
    {
        ClinPricing.TimeAndMaterials => "4020",
        ClinPricing.LaborHour => "4030",
        ClinPricing.FirmFixedPrice => "4040",
        not null => "4010",
        null => contractType switch
        {
            ContractType.TimeAndMaterials => "4020",
            ContractType.LaborHour => "4030",
            ContractType.FirmFixedPrice => "4040",
            _ => "4010",
        },
    };

    /// <summary>Posts an approved invoice (or credit memo). Returns the entry.</summary>
    public async Task<JournalEntry> PostInvoiceAsync(IWorkbenchDbContext db, Invoice invoice, Contract contract, IReadOnlyList<Clin> clins, CancellationToken ct)
    {
        if (await ExistingAsync(db, invoice.Id, ct) is { } existing)
        {
            return existing;
        }

        var credits = invoice.Lines.Select(l => (
            Account: l.Kind is InvoiceLineKind.FeeWithhold or InvoiceLineKind.WithholdRelease
                ? ArUnbilled
                : IsFinancing(l.Kind)
                    ? ContractFinancing
                    : RevenueAccount(clins.FirstOrDefault(c => c.Id == l.ClinId)?.Pricing, contract.Type),
            l.ProjectId,
            l.WbsElementId,
            Credit: l.Amount,
            Memo: l.Kind is InvoiceLineKind.FeeWithhold or InvoiceLineKind.WithholdRelease || IsFinancing(l.Kind) ? l.Description : null));
        var label = invoice.Type == InvoiceType.CreditMemo ? "Credit memo" : "Invoice";
        return await PostAsync(db, invoice.Id, invoice.InvoiceDate, $"{label} {invoice.Number} · {contract.ContractNumber}",
            (ArBilled, null, null, invoice.Total, $"{label} {invoice.Number}"), credits, ct, invoice.CompanyId,
            stamp: invoice.CurrencyCode is { } currency
                ? (entry, accounts) =>
                {
                    // Multicurrency: the receivable is owed in the invoice's currency.
                    foreach (var line in entry.Lines.Where(l => l.AccountId == accounts[ArBilled]))
                    {
                        line.TransactionCurrency = currency;
                        line.TransactionAmount = Math.Abs(invoice.TransactionTotal);
                        line.ExchangeRate = invoice.ExchangeRate;
                    }
                }
                : null);
    }

    /// <summary>
    /// Posts a receipt in a foreign currency: debit cash (the bank account kept in that currency, else 1010) at the receipt's
    /// rate, credit AR billed for what each invoice carried, and the difference to realized foreign exchange gain or loss.
    /// </summary>
    public async Task<JournalEntry> PostForeignPaymentAsync(
        IWorkbenchDbContext db, Payment payment, string customerName, IReadOnlyDictionary<Guid, Invoice> invoices, Guid realizedAccountId, CancellationToken ct)
    {
        if (await ExistingAsync(db, payment.Id, ct) is { } existing)
        {
            return existing;
        }

        var currency = payment.CurrencyCode!;
        var cash = await CurrencySupport.ForeignCashAccountAsync(db, currency, ct)
            ?? await db.Set<Account>().AsNoTracking().Where(a => a.Number == Cash).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Account {Cash} isn't in the chart of accounts. Install the default chart (Accounting › Chart of accounts) first.");
        var ar = await db.Set<Account>().AsNoTracking().Where(a => a.Number == ArBilled).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Account {ArBilled} isn't in the chart of accounts. Install the default chart (Accounting › Chart of accounts) first.");
        var memo = payment.Reference is null ? null : $"Ref {payment.Reference}";
        var entry = new JournalEntry
        {
            CompanyId = payment.CompanyId,
            Date = payment.ReceivedOn,
            Source = JournalSource.Billing,
            SourceId = payment.Id,
            SourceRevision = await db.Set<JournalEntry>().CountAsync(e => e.Source == JournalSource.Billing && e.SourceId == payment.Id, ct),
            Description = $"Cash receipt · {customerName} · {payment.TransactionAmount:N2} {currency}" + (memo is null ? "" : $" · {memo}"),
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        entry.Lines.Add(CurrencySupport.Line(entry, cash, payment.Amount, memo, currency, payment.TransactionAmount, payment.ExchangeRate));
        foreach (var a in payment.Applications)
        {
            var invoice = invoices[a.InvoiceId];
            entry.Lines.Add(CurrencySupport.Line(entry, ar, -a.Amount, $"Invoice {invoice.Number}", currency, a.TransactionAmount, invoice.ExchangeRate));
        }

        if (payment.RealizedGainLoss != 0)
        {
            entry.Lines.Add(CurrencySupport.Line(entry, realizedAccountId, -payment.RealizedGainLoss,
                $"Realized {(payment.RealizedGainLoss > 0 ? "gain" : "loss")} on {currency} receipt at {payment.ExchangeRate:0.######}"));
        }

        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, ct);
        return entry;
    }

    public async Task<JournalEntry> PostPaymentAsync(IWorkbenchDbContext db, Payment payment, string customerName, CancellationToken ct)
    {
        if (await ExistingAsync(db, payment.Id, ct) is { } existing)
        {
            return existing;
        }

        var memo = payment.Reference is null ? null : $"Ref {payment.Reference}";
        return await PostAsync(db, payment.Id, payment.ReceivedOn, $"Cash receipt · {customerName}" + (memo is null ? "" : $" · {memo}"),
            (Cash, null, null, payment.Amount, memo), [(ArBilled, null, null, payment.Amount, memo)], ct, payment.CompanyId);
    }

    /// <summary>Posts the accrual dated <paramref name="date"/> and its reversal on <paramref name="reverseOn"/>.</summary>
    public async Task<(JournalEntry Accrual, JournalEntry Reversal)> PostAccrualAsync(
        IWorkbenchDbContext db, Guid accrualId, DateOnly date, DateOnly reverseOn, string description,
        IEnumerable<(string Account, Guid? ProjectId, Guid? WbsElementId, decimal Credit, string? Memo)> revenue, CancellationToken ct)
    {
        var list = revenue.ToList();
        var entry = await PostAsync(db, accrualId, date, description, (ArUnbilled, null, null, list.Sum(r => r.Credit), "Earned, not yet billed"), list, ct);
        var reversal = await ledger.ReverseAsync(db, entry, reverseOn, "Automatic reversal of the unbilled revenue accrual", ct);
        return (entry, reversal);
    }

    /// <summary>Reverses a document's posted entry (void), dated <paramref name="date"/>. No-op when it isn't posted.</summary>
    public async Task<JournalEntry?> ReverseAsync(IWorkbenchDbContext db, Guid? entryId, DateOnly date, string reason, CancellationToken ct)
    {
        if (entryId is null)
        {
            return null;
        }

        var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct);
        return entry is { Status: JournalStatus.Posted } ? await ledger.ReverseAsync(db, entry, date, reason, ct) : null;
    }

    public static bool IsFinancing(InvoiceLineKind kind) =>
        kind is InvoiceLineKind.ProgressPayment or InvoiceLineKind.PerformanceBasedPayment or InvoiceLineKind.Liquidation;

    /// <summary>
    /// Adds 2320 to a chart installed before government billing. Saves, so call it on its own context before the posting
    /// unit of work (posting validates accounts against the database). No-op without a chart.
    /// </summary>
    public static async Task EnsureFinancingAccountAsync(IWorkbenchDbContext db, DateTime utcNow, CancellationToken ct)
    {
        if (await db.Set<Account>().AnyAsync(a => a.Number == ContractFinancing, ct) || !await db.Set<Account>().AnyAsync(ct))
        {
            return;
        }

        var parent = await db.Set<Account>().AsNoTracking().Where(a => a.Number == "2000").Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        db.Set<Account>().Add(new Account
        {
            Number = ContractFinancing,
            Name = "Unliquidated contract financing",
            Type = AccountType.Liability,
            NormalBalance = Account.DefaultNormalBalance(AccountType.Liability),
            ParentId = parent,
            Description = "Progress payments (FAR 52.232-16) and performance-based payments billed, less liquidations on deliveries",
            CreatedAtUtc = utcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<JournalEntry?> ExistingAsync(IWorkbenchDbContext db, Guid sourceId, CancellationToken ct) =>
        await db.Set<JournalEntry>().Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Source == JournalSource.Billing && e.SourceId == sourceId && e.Status == JournalStatus.Posted, ct);

    /// <summary>
    /// One debit (negative: credit) against signed credits, netted per account and segment so every line is positive.
    /// </summary>
    private async Task<JournalEntry> PostAsync(
        IWorkbenchDbContext db, Guid sourceId, DateOnly date, string description,
        (string Account, Guid? ProjectId, Guid? WbsElementId, decimal Debit, string? Memo) debit,
        IEnumerable<(string Account, Guid? ProjectId, Guid? WbsElementId, decimal Credit, string? Memo)> credits, CancellationToken ct,
        Guid companyId = default, Action<JournalEntry, IReadOnlyDictionary<string, Guid>>? stamp = null)
    {
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Number, a => a.Id, ct);
        Guid AccountId(string number) => accounts.TryGetValue(number, out var id)
            ? id
            : throw new InvalidOperationException($"Account {number} isn't in the chart of accounts. Install the default chart (Accounting › Chart of accounts) first.");

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            Date = date,
            Source = JournalSource.Billing,
            SourceId = sourceId,
            SourceRevision = await db.Set<JournalEntry>().CountAsync(e => e.Source == JournalSource.Billing && e.SourceId == sourceId, ct),
            Description = description.Length > 500 ? description[..500] : description,
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };

        void Add(string account, Guid? projectId, Guid? wbsId, decimal signedDebit, string? memo)
        {
            if (signedDebit == 0)
            {
                return;
            }

            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = entry.Lines.Count + 1,
                AccountId = AccountId(account),
                ProjectId = projectId,
                WbsElementId = wbsId,
                Debit = signedDebit > 0 ? signedDebit : 0,
                Credit = signedDebit < 0 ? -signedDebit : 0,
                Memo = memo,
            });
        }

        Add(debit.Account, debit.ProjectId, debit.WbsElementId, debit.Debit, debit.Memo);
        foreach (var g in credits.GroupBy(c => (c.Account, c.ProjectId, c.WbsElementId)).OrderBy(g => g.Key.Account, StringComparer.Ordinal))
        {
            Add(g.Key.Account, g.Key.ProjectId, g.Key.WbsElementId, -g.Sum(c => c.Credit), g.Select(c => c.Memo).FirstOrDefault(m => m is not null));
        }

        stamp?.Invoke(entry, accounts);
        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, ct);
        return entry;
    }
}
