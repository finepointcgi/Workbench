using Workbench.Domain.Billing;

namespace Workbench.Application.Currencies;

/// <summary>
/// Invoices to a foreign customer: billing is worked out in the functional currency (project costs and billing stay
/// functional) and the approved invoice is issued in the customer's currency at the spot rate on the invoice date.
/// </summary>
public static class InvoiceCurrency
{
    /// <summary>
    /// Fixes the rate and the line and total amounts in the invoice's currency, just before it's approved and posted. A
    /// credit memo takes the currency and rate of the invoice it credits (so it never realizes a gain or loss), and a credit
    /// of the whole open balance credits the whole open balance in that currency too.
    /// </summary>
    public static async Task ApplyAsync(IWorkbenchDbContext db, Invoice invoice, Invoice? credited, CancellationToken ct)
    {
        DocumentCurrency currency;
        if (invoice.Type == InvoiceType.CreditMemo)
        {
            currency = credited?.CurrencyCode is { } code ? await CurrencySupport.OfAsync(db, code, credited.ExchangeRate, ct) : DocumentCurrency.Functional;
        }
        else
        {
            currency = invoice.CurrencyCode is null ? DocumentCurrency.Functional : await CurrencySupport.ResolveAsync(db, invoice.CurrencyCode, invoice.InvoiceDate, null, ct);
        }

        invoice.CurrencyCode = currency.Code;
        invoice.ExchangeRate = currency.Rate;
        if (!currency.IsForeign)
        {
            invoice.TransactionTotal = 0;
            foreach (var line in invoice.Lines)
            {
                line.TransactionAmount = null;
            }

            return;
        }

        foreach (var line in invoice.Lines)
        {
            line.TransactionAmount = currency.ToTransaction(line.Amount);
        }

        var total = invoice.Lines.Sum(l => l.TransactionAmount ?? 0);
        if (invoice.Type == InvoiceType.CreditMemo && credited is not null && -invoice.Total == credited.Balance && invoice.Lines.Count > 0)
        {
            var last = invoice.Lines.OrderBy(l => l.LineNumber).Last();
            last.TransactionAmount += -credited.TransactionBalance - total;
            total = -credited.TransactionBalance;
        }

        invoice.TransactionTotal = total;
    }
}
