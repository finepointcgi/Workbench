using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Contract financing on invoices. Progress payment and performance-based payment invoices carry financing lines (no CLIN,
/// so they don't count against CLIN funding or revenue); delivery invoices (FFP milestones and T&amp;M) on a financed
/// contract get a negative liquidation line: the liquidation rate × the delivery amount, at most what is still
/// unliquidated. The unliquidated balance is financing on approved invoices less liquidation on every live invoice, so two
/// drafts can't liquidate the same financing.
/// </summary>
internal static class ContractFinancingLedger
{
    /// <summary>FAR 32.1004(b)(3): performance-based payments total at most 90% of the contract price.</summary>
    public const decimal PerformanceBasedCap = 0.90m;

    public static bool IsDelivery(InvoiceType type) => type is InvoiceType.FixedPriceMilestone or InvoiceType.TimeAndMaterials;

    public static bool IsFinancing(InvoiceType type) => type is InvoiceType.ProgressPayment or InvoiceType.PerformanceBased;

    public static bool IsFinancingLine(InvoiceLineKind kind) => kind is InvoiceLineKind.ProgressPayment or InvoiceLineKind.PerformanceBasedPayment;

    public static decimal Financing(Invoice invoice) => invoice.Lines.Where(l => IsFinancingLine(l.Kind)).Sum(l => l.Amount);

    /// <summary>Liquidated on the invoice, as a positive number.</summary>
    public static decimal Liquidation(Invoice invoice) => -invoice.Lines.Where(l => l.Kind == InvoiceLineKind.Liquidation).Sum(l => l.Amount);

    public static decimal Delivery(Invoice invoice) => IsDelivery(invoice.Type) ? invoice.Lines.Where(l => l.Kind != InvoiceLineKind.Liquidation).Sum(l => l.Amount) : 0;

    public static Task<ContractGovernmentBilling?> SettingsAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct) =>
        db.Set<ContractGovernmentBilling>().AsNoTracking().FirstOrDefaultAsync(s => s.ContractId == contractId, ct);

    public static decimal ContractPrice(ContractGovernmentBilling? settings, Contract contract) => settings?.ContractPrice ?? contract.CeilingValue;

    /// <summary>The liquidation rate: the setting, or for performance-based payments without one, events total ÷ contract price.</summary>
    public static async Task<decimal> LiquidationRateAsync(IWorkbenchDbContext db, ContractGovernmentBilling settings, Contract contract, CancellationToken ct)
    {
        if (settings.Financing != ContractFinancing.PerformanceBased || settings.LiquidationRate > 0)
        {
            return settings.LiquidationRate;
        }

        var price = ContractPrice(settings, contract);
        var events = await db.Set<PerformanceBasedPaymentEvent>().AsNoTracking().Where(e => e.ContractId == contract.Id).Select(e => e.Amount).ToListAsync(ct);
        return price > 0 ? Math.Round(events.Sum() / price, 6, MidpointRounding.AwayFromZero) : 0;
    }

    /// <summary>Financing approved, less liquidation on every live invoice except <paramref name="excludeInvoiceId"/>.</summary>
    public static decimal Unliquidated(IEnumerable<Invoice> live, Guid? excludeInvoiceId)
    {
        var others = live.Where(i => i.Id != excludeInvoiceId && i.Status != InvoiceStatus.Void).ToList();
        return others.Where(i => i.IsBilled).Sum(Financing) - others.Sum(Liquidation);
    }

    /// <summary>
    /// Adds the liquidation line to a delivery invoice's generated lines (and a note), or returns the generation unchanged.
    /// <paramref name="manual"/> are the draft's manual lines, which count toward the delivery amount.
    /// </summary>
    public static async Task<Generation> WithLiquidationAsync(
        IWorkbenchDbContext db, BillingContext ctx, InvoiceType type, Generation gen, IEnumerable<InvoiceLine> manual, Guid? excludeInvoiceId, CancellationToken ct)
    {
        if (!IsDelivery(type) || await SettingsAsync(db, ctx.Contract.Id, ct) is not { Financing: not ContractFinancing.None } settings)
        {
            return gen;
        }

        var gross = gen.Lines.Where(l => l.Kind != InvoiceLineKind.Liquidation).Sum(l => l.Amount)
            + manual.Where(l => l.Kind != InvoiceLineKind.Liquidation).Sum(l => l.Amount);
        if (gross <= 0)
        {
            return gen;
        }

        var live = await InvoiceGenerator.PriorInvoicesAsync(db, ctx.Contract.Id, excludeInvoiceId, approvedOnly: false, ct);
        var unliquidated = Unliquidated(live, excludeInvoiceId);
        var rate = await LiquidationRateAsync(db, settings, ctx.Contract, ct);
        var amount = Math.Min(Money.Round(gross * rate), unliquidated);
        var label = settings.Financing == ContractFinancing.ProgressPayments ? "progress payments" : "performance-based payments";
        if (amount <= 0)
        {
            return unliquidated <= 0 && rate > 0
                ? gen with { Warnings = [.. gen.Warnings, $"No {label} are outstanding, so nothing is liquidated on this invoice."] }
                : gen;
        }

        var line = new DraftLine(null, InvoiceLineKind.Liquidation, $"Liquidation of {label} ({rate:P2} of {gross:N2})",
            null, null, null, null, 1, rate, -amount, []);
        var warnings = gen.Warnings.ToList();
        if (amount < Money.Round(gross * rate))
        {
            warnings.Add($"Liquidation is limited to the {unliquidated:C} of {label} still outstanding.");
        }

        return gen with { Lines = [.. gen.Lines, line], Warnings = warnings };
    }

    /// <summary>Refuses approving a delivery invoice whose liquidation is more than what is outstanding now.</summary>
    public static async Task EnsureLiquidationWithinBalanceAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct)
    {
        var liquidation = Liquidation(invoice);
        if (liquidation <= 0)
        {
            return;
        }

        var live = await InvoiceGenerator.PriorInvoicesAsync(db, invoice.ContractId, invoice.Id, approvedOnly: false, ct);
        var outstanding = Unliquidated(live.Where(i => i.IsBilled), invoice.Id);
        if (liquidation > outstanding)
        {
            throw new InvalidOperationException(
                $"This invoice liquidates {liquidation:C} but only {Math.Max(0, outstanding):C} of contract financing is outstanding. Regenerate the draft.");
        }
    }

    /// <summary>Refuses voiding a financing invoice that deliveries have already liquidated against.</summary>
    public static async Task EnsureFinancingCanBeVoidedAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct)
    {
        if (!IsFinancing(invoice.Type) || !invoice.IsBilled)
        {
            return;
        }

        var live = await InvoiceGenerator.PriorInvoicesAsync(db, invoice.ContractId, invoice.Id, approvedOnly: false, ct);
        if (Unliquidated(live, invoice.Id) < 0)
        {
            throw new InvalidOperationException(
                $"Deliveries have already liquidated part of invoice {invoice.Number}. Void or credit those delivery invoices first.");
        }
    }
}
