namespace Workbench.Domain.Ceilings;

/// <summary>What a ceiling is set on. A labor-category ceiling is per WBS element (and everything under it).</summary>
public enum CeilingLevel
{
    Contract,
    Clin,
    Project,
    Wbs,
    LaborCategory,
}

/// <summary>Warn reports and alerts; Block also refuses the charge, bill, order or invoice that would go past the ceiling.</summary>
public enum CeilingEnforcement
{
    Warn,
    Block,
}

/// <summary>
/// A limit on hours and/or dollars. Hours and cost count charges (time on record, submitted and approved ODCs, open PO
/// commitments); <see cref="BillingCeiling"/> counts what invoices billed (contract and CLIN level only).
/// Ceilings are deactivated, never deleted.
/// </summary>
public sealed class Ceiling : Entity
{
    public CeilingLevel Level { get; set; }

    /// <summary>Always set: the contract the ceiling belongs to (the project's contract for project and WBS ceilings), or null for an indirect project.</summary>
    public Guid? ContractId { get; set; }

    public Guid? ClinId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public Guid? LaborCategoryId { get; set; }

    public decimal? HoursCeiling { get; set; }
    public decimal? CostCeiling { get; set; }
    public decimal? BillingCeiling { get; set; }

    public CeilingEnforcement Enforcement { get; set; } = CeilingEnforcement.Warn;

    /// <summary>Percent thresholds that raise an alert, comma separated (for example <c>80,95,100</c>).</summary>
    public string AlertThresholds { get; set; } = "80,95,100";

    /// <summary>When true, the manager of the ceiling's project (or of a project on its contract) may grant overrides too.</summary>
    public bool ProjectManagerMayOverride { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// A temporary allowance past a ceiling until <see cref="ExpiresAtUtc"/>. With no extra amounts, the ceiling only warns
/// while the override is in force.
/// </summary>
public sealed class CeilingOverride : Entity
{
    public Guid CeilingId { get; set; }
    public decimal? ExtraHours { get; set; }
    public decimal? ExtraCost { get; set; }
    public decimal? ExtraBilling { get; set; }
    public required string Reason { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public required string ApprovedBy { get; set; }
    public DateTime ApprovedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokeReason { get; set; }

    public bool IsActiveAt(DateTime utcNow) => RevokedAtUtc is null && ExpiresAtUtc > utcNow;
}

/// <summary>Which measure an alert was about.</summary>
public enum CeilingMeasure
{
    Hours,
    Cost,
    Billing,
}

/// <summary>One alert sent for a ceiling, measure and threshold, so each is sent once (until the ceiling changes).</summary>
public sealed class CeilingAlert : Entity
{
    public Guid CeilingId { get; set; }
    public CeilingMeasure Measure { get; set; }
    public int ThresholdPercent { get; set; }

    /// <summary>The ceiling amount the alert was measured against; raising the ceiling lets the threshold alert again.</summary>
    public decimal Limit { get; set; }

    public decimal Consumed { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public enum RateCeilingKind
{
    Fringe,
    Overhead,
    GeneralAndAdministrative,

    /// <summary>The highest fee rate billed on cost (for example 6% on a 7% CLIN).</summary>
    FeeRate,

    /// <summary>Total fee billed, in dollars.</summary>
    FeeAmount,
}

/// <summary>
/// A cap on what billing may apply on a contract (or one CLIN): an indirect rate cap (billing bills the lower of the
/// provisional/actual rate and the cap), a fee rate cap or a total fee cap.
/// </summary>
public sealed class RateCeiling : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>Fee caps may be per CLIN; indirect rate caps are contract-wide.</summary>
    public Guid? ClinId { get; set; }

    public RateCeilingKind Kind { get; set; }

    /// <summary>Applies to one fiscal year, or every year when null.</summary>
    public int? FiscalYear { get; set; }

    /// <summary>The cap for rate kinds, as a fraction (0.45 = 45%).</summary>
    public decimal? MaxRate { get; set; }

    /// <summary>The cap for <see cref="RateCeilingKind.FeeAmount"/>.</summary>
    public decimal? MaxAmount { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
