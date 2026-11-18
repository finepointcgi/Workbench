using Workbench.Domain.Billing;

namespace Workbench.Application.Billing;

/// <summary>
/// Where invoicing asks about sales tax. Sales and use tax (rates, codes, exemptions, the return) belong to the accounting license module, which registers the
/// implementation; a host without it registers nothing, and invoices carry no tax lines. Core keeps the <c>InvoiceLineKind.SalesTax</c> member and the
/// ledger posting for such lines (an invoice made while the module was installed still posts and exports).
/// </summary>
public interface IInvoiceTaxHook
{
    /// <summary>Makes sure the liability account sales tax lines credit exists, before the posting looks it up.</summary>
    Task EnsureAccountsAsync(CancellationToken cancellationToken);

    /// <summary>Puts sales tax on a draft invoice in the caller's unit of work (replaces its sales tax line and records the calculation).</summary>
    Task ApplyAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken cancellationToken);

    /// <summary>The number of the account sales tax lines credit: the sales tax setup's choice, else <see cref="SalesTaxDefaults.PayableNumber"/>.</summary>
    Task<string> PayableNumberAsync(IWorkbenchDbContext db, CancellationToken cancellationToken);
}

/// <summary>What the ledger uses for sales tax lines when no sales tax setup says otherwise.</summary>
public static class SalesTaxDefaults
{
    /// <summary>Account 2150, Sales tax payable.</summary>
    public const string PayableNumber = "2150";
}

/// <summary>The sales tax record of a posted vendor bill, as payables sees it (the bill's use tax journal entry is reversed with the bill).</summary>
public sealed record BillTaxRecord(Guid Id, Guid? UseTaxJournalEntryId);

/// <summary>Where payables asks about use tax on vendor bills. Registered by the accounting license module; without it bills carry none.</summary>
public interface IBillTaxHook
{
    /// <summary>Accrues use tax on a posted bill's taxable lines the vendor didn't tax. Never fails the posting.</summary>
    Task TryAccrueAsync(Guid billId, CancellationToken cancellationToken);

    /// <summary>The tax record of the bill, or null.</summary>
    Task<BillTaxRecord?> FindAsync(IWorkbenchDbContext db, Guid billId, CancellationToken cancellationToken);
}
