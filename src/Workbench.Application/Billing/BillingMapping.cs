using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Billing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>Company details printed on invoices (configuration section <c>Billing</c>).</summary>
public sealed class BillingOptions
{
    public string CompanyName { get; set; } = "";
    public string? RemitToAddress { get; set; }
    public string? CageCode { get; set; }

    /// <summary>Unique Entity ID (SAM.gov), printed on vouchers.</summary>
    public string? Uei { get; set; }
}

internal static class BillingMapping
{
    public static InvoiceKind ToContract(this InvoiceType t) => (InvoiceKind)(int)t;

    public static InvoiceType ToDomain(this InvoiceKind t) => (InvoiceType)(int)t;

    public static InvoiceState ToContract(this InvoiceStatus s) => (InvoiceState)(int)s;

    public static InvoiceStatus ToDomain(this InvoiceState s) => (InvoiceStatus)(int)s;

    public static InvoiceLineType ToContract(this InvoiceLineKind k) => (InvoiceLineType)(int)k;

    public static InvoiceLineKind ToDomain(this InvoiceLineType k) => (InvoiceLineKind)(int)k;

    public static CashReceiptMethod ToContract(this PaymentMethod m) => (CashReceiptMethod)(int)m;

    public static PaymentMethod ToDomain(this CashReceiptMethod m) => (PaymentMethod)(int)m;

    public static string Label(ClinPricing p) => p switch
    {
        ClinPricing.FirmFixedPrice => "FFP",
        ClinPricing.TimeAndMaterials => "T&M",
        ClinPricing.LaborHour => "Labor hour",
        ClinPricing.CostPlusFixedFee => "CPFF",
        ClinPricing.CostPlusAwardFee => "CPAF",
        ClinPricing.CostPlusIncentiveFee => "CPIF",
        ClinPricing.CostNoFee => "Cost (no fee)",
        _ => p.ToString(),
    };

    public static string Label(ContractType t) => t switch
    {
        ContractType.FirmFixedPrice => "Firm fixed price",
        ContractType.TimeAndMaterials => "Time and materials",
        ContractType.LaborHour => "Labor hour",
        ContractType.CostPlusFixedFee => "Cost plus fixed fee",
        ContractType.CostPlusAwardFee => "Cost plus award fee",
        ContractType.CostPlusIncentiveFee => "Cost plus incentive fee",
        _ => t.ToString(),
    };

    public static int DaysPastDue(Invoice i, DateOnly today) =>
        i.IsOpenReceivable && i.Balance > 0 && today > i.DueDate ? today.DayNumber - i.DueDate.DayNumber : 0;
}

/// <summary>Names and codes for billing DTOs, loaded once per call.</summary>
internal sealed class BillingLookups
{
    public required Dictionary<Guid, Contract> Contracts { get; init; }
    public required Dictionary<Guid, Customer> Customers { get; init; }
    public required Dictionary<Guid, Clin> Clins { get; init; }
    public required Dictionary<Guid, string> Projects { get; init; }
    public required Dictionary<Guid, string> Wbs { get; init; }
    public required Dictionary<Guid, string> Categories { get; init; }

    public static async Task<BillingLookups> LoadAsync(IWorkbenchDbContext db, CancellationToken ct) => new()
    {
        Contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
        Customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
        Clins = await db.Set<Clin>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
        Projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, ct),
        Wbs = await db.Set<WbsElement>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct),
        Categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct),
    };

    public InvoiceSummaryDto Summary(Invoice i, DateOnly today) => new(
        i.Id,
        i.Number,
        i.ContractId,
        Contracts.GetValueOrDefault(i.ContractId)?.ContractNumber ?? "",
        i.CustomerId,
        Customers.GetValueOrDefault(i.CustomerId)?.Name ?? "",
        i.Type.ToContract(),
        i.Status.ToContract(),
        i.PeriodFrom,
        i.PeriodTo,
        i.InvoiceDate,
        i.DueDate,
        i.Total,
        i.AmountPaid,
        i.AmountCredited,
        i.Balance,
        BillingMapping.DaysPastDue(i, today));

    public InvoiceLineDto Line(InvoiceLine l, Guid? id = null) => new(
        id ?? l.Id,
        l.LineNumber,
        l.ClinId,
        l.ClinId is { } c ? Clins.GetValueOrDefault(c)?.Number : null,
        l.Kind.ToContract(),
        l.Description,
        l.ProjectId,
        l.ProjectId is { } p ? Projects.GetValueOrDefault(p) : null,
        l.WbsElementId,
        l.WbsElementId is { } w ? Wbs.GetValueOrDefault(w) : null,
        l.LaborCategoryId,
        l.LaborCategoryId is { } lc ? Categories.GetValueOrDefault(lc) : null,
        l.OdcType,
        l.Quantity,
        l.Rate,
        l.Amount,
        l.IsGenerated,
        l.Sources.Count);

    public InvoiceLineDto Line(DraftLine d, int number) => new(
        Guid.Empty,
        number,
        d.ClinId,
        d.ClinId is { } c ? Clins.GetValueOrDefault(c)?.Number : null,
        d.Kind.ToContract(),
        d.Description,
        d.ProjectId,
        d.ProjectId is { } p ? Projects.GetValueOrDefault(p) : null,
        d.WbsElementId,
        d.WbsElementId is { } w ? Wbs.GetValueOrDefault(w) : null,
        d.LaborCategoryId,
        d.LaborCategoryId is { } lc ? Categories.GetValueOrDefault(lc) : null,
        d.OdcType,
        d.Quantity,
        d.Rate,
        d.Amount,
        true,
        d.Sources.Count);

    public static async Task<Dictionary<Guid, string>> JournalNumbersAsync(IWorkbenchDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Set<JournalEntry>().AsNoTracking().Where(e => list.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
    }
}
