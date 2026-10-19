using Microsoft.EntityFrameworkCore;
using Workbench.Application.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.Currencies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Currencies;

/// <summary>An open foreign balance as of a date: in its currency, as carried (at its document rates) and as revalued.</summary>
public sealed record OpenForeignBalance(
    RevaluationItemKind Kind,
    Guid DocumentId,
    string DocumentNumber,
    string Party,
    DateOnly Date,
    string CurrencyCode,
    decimal DocumentRate,
    decimal TransactionAmount,
    decimal TransactionBalance,
    decimal CarryingAmount,
    Guid AccountId,
    string AccountNumber,
    decimal? Rate,
    decimal? RevaluedAmount)
{
    /// <summary>
    /// Gain (positive) or loss (negative) on revaluing. An asset (receivable, bank) gains when it's worth more; a payable
    /// gains when it costs less. In both cases it's also the signed debit to the balance's account.
    /// </summary>
    public decimal? GainLoss => RevaluedAmount is not { } revalued ? null
        : Kind == RevaluationItemKind.Payable ? CarryingAmount - revalued : revalued - CarryingAmount;
}

/// <summary>
/// Finds the open foreign currency balances on a date and values them at that date's month-end rate. Receivables and
/// payables are as of the date (documents dated on or before it, less payments and credits by then); a bank account kept in
/// a foreign currency is its GL cash account's balance in that currency, so its account must not be shared with a bank
/// account in another currency.
/// </summary>
public static class RevaluationEngine
{
    /// <param name="latestRate">Value at the newest rate of any type on the date (the report) instead of the month-end rate (the revaluation).</param>
    public static async Task<(List<OpenForeignBalance> Balances, List<string> Warnings)> OpenBalancesAsync(IWorkbenchDbContext db, DateOnly asOf, CancellationToken ct, bool latestRate = false)
    {
        var functional = await CurrencySupport.FunctionalAsync(db, ct);
        var warnings = new List<string>();
        var balances = new List<OpenForeignBalance>();
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Number, ct);

        // Receivables.
        var arAccount = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "1110", ct);
        var invoices = await db.Set<Invoice>().AsNoTracking()
            .Where(i => i.CurrencyCode != null && i.Type != InvoiceType.CreditMemo && i.InvoiceDate <= asOf
                && (i.Status == InvoiceStatus.Approved || i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid))
            .ToListAsync(ct);
        if (invoices.Count > 0)
        {
            var ids = invoices.Select(i => i.Id).ToList();
            var paid = (await db.Set<PaymentApplication>().AsNoTracking().Where(a => ids.Contains(a.InvoiceId))
                    .Join(db.Set<Payment>().Where(p => !p.IsVoid && p.ReceivedOn <= asOf), a => a.PaymentId, p => p.Id, (a, p) => new { a.InvoiceId, a.Amount, a.TransactionAmount })
                    .ToListAsync(ct))
                .GroupBy(a => a.InvoiceId).ToDictionary(g => g.Key, g => (Functional: g.Sum(a => a.Amount), Transaction: g.Sum(a => a.TransactionAmount ?? 0)));
            var credited = (await db.Set<Invoice>().AsNoTracking()
                    .Where(c => c.CreditOfInvoiceId != null && ids.Contains(c.CreditOfInvoiceId.Value) && c.InvoiceDate <= asOf
                        && (c.Status == InvoiceStatus.Approved || c.Status == InvoiceStatus.Sent || c.Status == InvoiceStatus.PartiallyPaid || c.Status == InvoiceStatus.Paid))
                    .Select(c => new { Id = c.CreditOfInvoiceId!.Value, c.Total, c.TransactionTotal }).ToListAsync(ct))
                .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => (Functional: -g.Sum(c => c.Total), Transaction: -g.Sum(c => c.TransactionTotal)));
            var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            foreach (var i in invoices.OrderBy(i => i.Number, StringComparer.Ordinal))
            {
                var p = paid.GetValueOrDefault(i.Id);
                var c = credited.GetValueOrDefault(i.Id);
                var transaction = i.TransactionTotal - p.Transaction - c.Transaction;
                var carrying = i.Total - p.Functional - c.Functional;
                if (transaction == 0 && carrying == 0)
                {
                    continue;
                }

                balances.Add(new OpenForeignBalance(RevaluationItemKind.Receivable, i.Id, i.Number, customers.GetValueOrDefault(i.CustomerId) ?? "", i.InvoiceDate,
                    i.CurrencyCode!, i.ExchangeRate, i.TransactionTotal, transaction, carrying, arAccount?.Id ?? Guid.Empty, arAccount?.Number ?? "1110", null, null));
            }
        }

        // Payables.
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => b.CurrencyCode != null && b.Status == ApBillStatus.Posted && b.InvoiceDate <= asOf).ToListAsync(ct);
        if (bills.Count > 0)
        {
            var payables = await PurchasingSupport.PayablesAccountAsync(db, ct);
            var ids = bills.Select(b => b.Id).ToList();
            var paid = (await db.Set<ApPayment>().AsNoTracking().Where(p => ids.Contains(p.BillId) && p.Status == ApPaymentStatus.Paid && p.PaidOn <= asOf)
                    .Select(p => new { p.BillId, p.Amount, p.TransactionAmount }).ToListAsync(ct))
                .GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => (Functional: g.Sum(p => p.Amount), Transaction: g.Sum(p => p.TransactionAmount)));
            var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name, ct);
            foreach (var b in bills.OrderBy(b => b.Number, StringComparer.Ordinal))
            {
                var p = paid.GetValueOrDefault(b.Id);
                var transaction = b.TransactionAmount - b.TransactionRetainage - p.Transaction;
                var carrying = b.Amount - b.RetainageAmount - p.Functional;
                if (transaction == 0 && carrying == 0)
                {
                    continue;
                }

                balances.Add(new OpenForeignBalance(RevaluationItemKind.Payable, b.Id, $"{b.Number} ({b.InvoiceNumber})", vendors.GetValueOrDefault(b.VendorId) ?? "", b.InvoiceDate,
                    b.CurrencyCode!, b.ExchangeRate, b.TransactionAmount, transaction, carrying, payables.Id, payables.Number, null, null));
            }
        }

        // Bank accounts kept in a foreign currency.
        var banks = await db.Set<BankAccount>().AsNoTracking().Where(b => b.IsActive).ToListAsync(ct);
        foreach (var bank in banks.Where(b => b.Currency != functional).OrderBy(b => b.Name))
        {
            if (banks.Any(o => o.Id != bank.Id && o.GlAccountId == bank.GlAccountId && o.Currency != bank.Currency))
            {
                warnings.Add($"{bank.Name} ({bank.Currency}) shares GL account {accounts.GetValueOrDefault(bank.GlAccountId)} with an account in another currency, so it isn't revalued. Give it its own cash account.");
                continue;
            }

            var lines = await (
                    from l in db.Set<JournalLine>().AsNoTracking()
                    join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                    where l.AccountId == bank.GlAccountId && e.Status != JournalStatus.Draft && e.Date <= asOf
                    select new { l.Debit, l.Credit, l.TransactionCurrency, l.TransactionAmount })
                .ToListAsync(ct);
            var carrying = lines.Sum(l => l.Debit - l.Credit);
            var transaction = lines.Where(l => l.TransactionCurrency == bank.Currency).Sum(l => (l.Debit > 0 ? 1 : -1) * (l.TransactionAmount ?? 0));
            if (lines.Any(l => l.TransactionCurrency != null && l.TransactionCurrency != bank.Currency))
            {
                warnings.Add($"{bank.Name} has postings in other currencies than {bank.Currency}; only its {bank.Currency} amounts are revalued.");
            }

            if (transaction == 0 && carrying == 0)
            {
                continue;
            }

            balances.Add(new OpenForeignBalance(RevaluationItemKind.Bank, bank.Id, $"{bank.Name} ••••{bank.AccountNumberLast4}", bank.BankName, asOf,
                bank.Currency, transaction == 0 ? 0 : Math.Round(carrying / transaction, CurrencyMath.RateDecimals), transaction, transaction, carrying,
                bank.GlAccountId, accounts.GetValueOrDefault(bank.GlAccountId) ?? "", null, null));
        }

        // Value everything at the month-end rate (falling back to the latest rate of another type).
        var valued = new List<OpenForeignBalance>();
        foreach (var group in balances.GroupBy(b => b.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var quote = latestRate
                ? await CurrencySupport.FindLatestRateAsync(db, group.Key, functional, asOf, ct)
                : await CurrencySupport.FindRateAsync(db, group.Key, functional, asOf, ExchangeRateType.MonthEnd, ct);
            if (quote is null)
            {
                warnings.Add($"There's no {group.Key} rate on or before {asOf:yyyy-MM-dd}, so {group.Count()} {group.Key} balance(s) aren't revalued. Enter a month-end rate under Setup › Currencies and rates.");
                valued.AddRange(group);
                continue;
            }

            if (!latestRate && (quote.IsFallback || quote.EffectiveDate < asOf.AddDays(-7)))
            {
                warnings.Add($"{group.Key}: used the {Describe(quote.RateType)} rate of {quote.EffectiveDate:yyyy-MM-dd} ({quote.Rate:0.######}){(quote.IsFallback ? "; there's no month-end rate" : "")}.");
            }

            valued.AddRange(group.Select(b => b with { Rate = quote.Rate, RevaluedAmount = CurrencyMath.ToFunctional(b.TransactionBalance, quote.Rate) }));
        }

        return (valued, warnings);
    }

    public static string Describe(ExchangeRateType type) => type switch
    {
        ExchangeRateType.MonthEnd => "month-end",
        ExchangeRateType.Average => "average",
        _ => "spot",
    };
}
