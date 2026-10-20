namespace Workbench.Domain.SalesTax;

/// <summary>
/// A place sales and use tax is charged: one state/county/city combination (a ship-to location). Its rate is the sum of
/// the components of the <see cref="TaxRate"/> in effect. Deactivated, never deleted.
/// </summary>
public sealed class TaxJurisdiction : Entity
{
    /// <summary>A short unique code, for example "VA-FAIRFAX-FAIRFAX" or a provider's location code.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>Two-letter state code, for example "VA".</summary>
    public required string State { get; set; }

    public string? County { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Effective-dated, append-only rates for a jurisdiction (a correction is a new rate). Each component is a fraction
/// (0.053 = 5.3%); the combined rate is their sum.
/// </summary>
public sealed class TaxRate : Entity
{
    public Guid JurisdictionId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public decimal StateRate { get; set; }
    public decimal CountyRate { get; set; }
    public decimal CityRate { get; set; }

    /// <summary>Special districts (transit, stadium...).</summary>
    public decimal SpecialRate { get; set; }

    public decimal CombinedRate => StateRate + CountyRate + CityRate + SpecialRate;

    /// <summary>"Manual", "CSV import" or an engine name.</summary>
    public required string Source { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum TaxTreatment
{
    /// <summary>Tax applies.</summary>
    Taxable,

    /// <summary>Never taxed (most services, for example).</summary>
    Exempt,

    /// <summary>Bought for resale: no tax when we buy it (with a resale certificate); taxed when we sell it.</summary>
    Resale,
}

/// <summary>A tax code for items, parts and charges: how sales and purchases of that kind are taxed.</summary>
public sealed class TaxCode : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public TaxTreatment Treatment { get; set; }
    public bool IsActive { get; set; } = true;
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>What a <see cref="TaxCodeAssignment"/> points at.</summary>
public enum TaxCodeTarget
{
    /// <summary>An invoice line kind (Labor, Odc, Milestone...): <see cref="TaxCodeAssignment.TargetKey"/> is the kind's name.</summary>
    InvoiceLineKind,

    /// <summary>An ODC type (Materials, Travel...) on invoices and on project-cost bill lines.</summary>
    OdcType,

    /// <summary>A GL account on account bill lines: the key is the account id.</summary>
    Account,

    /// <summary>A part (materials): the key is the part id.</summary>
    Part,
}

/// <summary>Which tax code applies to an invoice line kind, ODC type, account or part. One per target.</summary>
public sealed class TaxCodeAssignment : Entity
{
    public TaxCodeTarget Target { get; set; }
    public required string TargetKey { get; set; }
    public Guid TaxCodeId { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum CustomerTaxStatus
{
    /// <summary>Government customers (by category) are exempt; everyone else is taxable.</summary>
    Default,

    Taxable,
    Exempt,
}

/// <summary>A customer's sales tax setup: where it takes delivery and whether it's taxable. One per customer.</summary>
public sealed class CustomerTaxProfile : Entity
{
    public Guid CustomerId { get; set; }

    /// <summary>The ship-to jurisdiction invoices are taxed in (an invoice can override it).</summary>
    public Guid? ShipToJurisdictionId { get; set; }

    public CustomerTaxStatus Status { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A customer's exemption certificate (resale, nonprofit, government...), for one state or every state.</summary>
public sealed class TaxExemptionCertificate : Entity
{
    public Guid CustomerId { get; set; }
    public required string Number { get; set; }

    /// <summary>Two-letter state the certificate covers; null for a multistate (for example SST or MTC) certificate.</summary>
    public string? State { get; set; }

    /// <summary>For example "Resale", "Nonprofit", "Government", "Manufacturing".</summary>
    public required string Reason { get; set; }

    public DateOnly EffectiveDate { get; set; }

    /// <summary>Null when it doesn't expire.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>The scanned certificate: a <c>StoredFile</c> id.</summary>
    public Guid? FileId { get; set; }

    public bool IsRevoked { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public bool CoversOn(DateOnly date, string state) =>
        !IsRevoked && EffectiveDate <= date && (ExpiresOn is null || ExpiresOn >= date) && (State is null || string.Equals(State, state, StringComparison.Ordinal));
}

/// <summary>Sales and use tax setup (one row): the liability and expense accounts and the defaults.</summary>
public sealed class SalesTaxSettings : Entity
{
    /// <summary>Credited with sales tax billed on invoices (default 2310 Sales tax payable).</summary>
    public Guid? SalesTaxPayableAccountId { get; set; }

    /// <summary>Credited with use tax accrued on purchases (default 2320 Use tax payable).</summary>
    public Guid? UseTaxPayableAccountId { get; set; }

    /// <summary>Debited with accrued use tax (default 6250 Use tax expense).</summary>
    public Guid? UseTaxExpenseAccountId { get; set; }

    /// <summary>Where purchases are used when a bill doesn't say (our office); the use tax jurisdiction.</summary>
    public Guid? HomeJurisdictionId { get; set; }

    /// <summary>Tax code for invoice lines with no assignment; null = taxable.</summary>
    public Guid? DefaultSalesTaxCodeId { get; set; }

    /// <summary>Tax code for bill lines with no assignment; null = taxable.</summary>
    public Guid? DefaultPurchaseTaxCodeId { get; set; }

    /// <summary>Customer categories treated as government (exempt by default), comma-separated.</summary>
    public string GovernmentCategories { get; set; } = "DoD,Civilian,Federal,State,Local,Government,Foreign government";

    /// <summary>The engine that calculates tax ("Rate tables" unless a host registers another).</summary>
    public string Engine { get; set; } = "Rate tables";

    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// The sales tax worked out for an invoice (one per invoice, replaced while it's a draft). The tax itself is an invoice
/// line of kind SalesTax, so the invoice total and the receivable include it.
/// </summary>
public sealed class InvoiceTax : Entity
{
    public Guid InvoiceId { get; set; }
    public Guid? JurisdictionId { get; set; }

    /// <summary>Set by Finance on the invoice; otherwise the customer's ship-to applies.</summary>
    public Guid? JurisdictionOverrideId { get; set; }

    public decimal TaxableAmount { get; set; }
    public decimal ExemptAmount { get; set; }
    public decimal Rate { get; set; }
    public decimal StateTax { get; set; }
    public decimal CountyTax { get; set; }
    public decimal CityTax { get; set; }
    public decimal SpecialTax { get; set; }
    public decimal TaxAmount { get; set; }

    /// <summary>Why nothing (or less) was taxed: "Government customer", "Certificate R-123", ...</summary>
    public string? ExemptionReason { get; set; }

    public Guid? CertificateId { get; set; }
    public required string Engine { get; set; }
    public required string CalculatedBy { get; set; }
    public DateTime CalculatedAtUtc { get; set; }
}

/// <summary>
/// Sales or use tax on an AP bill. Vendor-charged tax is part of the bill's lines (recorded here for the return); when the
/// vendor charged none on a taxable purchase, use tax accrues: debit use tax expense, credit use tax payable.
/// </summary>
public sealed class ApBillTax : Entity
{
    public Guid BillId { get; set; }

    /// <summary>Where the purchase is used; null = the home jurisdiction.</summary>
    public Guid? JurisdictionId { get; set; }

    /// <summary>Tax the vendor charged (already included in the bill's lines).</summary>
    public decimal VendorChargedTax { get; set; }

    /// <summary>Overrides the line tax codes for the whole bill (for example Exempt or Resale).</summary>
    public Guid? TaxCodeOverrideId { get; set; }

    public decimal TaxableAmount { get; set; }
    public decimal Rate { get; set; }

    /// <summary>Use tax accrued (0 when the vendor charged tax or nothing is taxable).</summary>
    public decimal UseTaxAmount { get; set; }

    /// <summary>The accrual entry (GL source AP, source id = this row).</summary>
    public Guid? UseTaxJournalEntryId { get; set; }

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
