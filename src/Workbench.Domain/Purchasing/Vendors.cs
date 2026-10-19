namespace Workbench.Domain.Purchasing;

public enum VendorStatus
{
    Active,
    OnHold,
    Inactive,
}

/// <summary>A supplier or subcontractor that bills us. Shared by AP, subcontracts and vendor ODCs.</summary>
public sealed class Vendor : Workbench.Domain.Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public VendorStatus Status { get; set; } = VendorStatus.Active;

    /// <summary>Receives a 1099-NEC/MISC at year end.</summary>
    public bool Is1099 { get; set; }

    /// <summary>Only the last four digits of the TIN are stored in the clear; the full TIN is the AP module's concern.</summary>
    public string? TaxIdLast4 { get; set; }

    /// <summary>Unique Entity ID (SAM.gov).</summary>
    public string? Uei { get; set; }

    /// <summary>For example Small, SDVOSB, 8(a), WOSB, HUBZone, Large; used for subcontracting plan reporting.</summary>
    public string? BusinessSize { get; set; }

    /// <summary>Net days, for example 30.</summary>
    public int PaymentTermsDays { get; set; } = 30;

    public string? RemitToAddress { get; set; }
    public string? ContactEmail { get; set; }

    // ---- Added by accounts payable (additive) ----

    /// <summary>Comma-separated socio-economic categories, for example "SDB,WOSB,HUBZone,SDVOSB,VOSB".</summary>
    public string? SocioEconomicCategories { get; set; }

    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }

    /// <summary>The 1099 box payments are reported in, for example "NEC-1" (default) or "MISC-1".</summary>
    public string? Form1099Box { get; set; }

    /// <summary>The current W-9: a <c>StoredFile</c> id.</summary>
    public Guid? W9FileId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Added by multicurrency: the currency the vendor invoices in, when it isn't the functional currency (new bills default to it).</summary>
    public string? CurrencyCode { get; set; }
}

public enum SubcontractStatus
{
    Draft,
    Active,
    Closed,
}

/// <summary>A subcontract issued to a vendor under one of our prime contracts.</summary>
public sealed class Subcontract : Workbench.Domain.Entity
{
    public Guid VendorId { get; set; }

    /// <summary>The prime contract the work flows down from; null for commercial subcontracts.</summary>
    public Guid? PrimeContractId { get; set; }

    public Guid? ProjectId { get; set; }
    public required string Number { get; set; }
    public required string Description { get; set; }

    /// <summary>Uses the same pricing types as CLINs.</summary>
    public Workbench.Domain.ProjectAccounting.ClinPricing Pricing { get; set; }

    public SubcontractStatus Status { get; set; } = SubcontractStatus.Draft;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal CeilingValue { get; set; }
    public decimal FundedValue { get; set; }

    /// <summary>Percent (0-100) of each subcontract invoice held back as retainage (added by accounts payable).</summary>
    public decimal RetainagePercent { get; set; }
}
