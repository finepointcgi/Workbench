namespace Workbench.Domain.ProjectAccounting;

/// <summary>How a CLIN is priced. A contract can mix CLIN types (for example cost-plus labor and FFP deliverables).</summary>
public enum ClinPricing
{
    FirmFixedPrice,
    TimeAndMaterials,
    LaborHour,
    CostPlusFixedFee,
    CostPlusAwardFee,
    CostPlusIncentiveFee,
    CostNoFee,
}

/// <summary>A contract line item number: the unit the government funds and is billed against.</summary>
public sealed class Clin : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>For example <c>0001</c>, <c>0001AA</c> (a SLIN), or <c>1001</c> for an option year.</summary>
    public required string Number { get; set; }

    public required string Description { get; set; }
    public ClinPricing Pricing { get; set; }
    public bool IsOption { get; set; }

    /// <summary>False until the option is exercised; unexercised options can't be charged or billed.</summary>
    public bool IsExercised { get; set; } = true;

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>CLIN value (price or estimated cost plus fee).</summary>
    public decimal CeilingValue { get; set; }

    /// <summary>Running total of funding actions; kept in step with <see cref="FundingAction"/> rows.</summary>
    public decimal FundedValue { get; set; }

    /// <summary>Fee rate for cost-plus CLINs (0.07 = 7%), applied to allowable cost when billing.</summary>
    public decimal? FeeRate { get; set; }

    public int Order { get; set; }
}

public enum ModificationStatus
{
    Draft,
    Executed,
}

/// <summary>A contract modification (P00001, ...). Executed mods are immutable and explain every change to value or funding.</summary>
public sealed class ContractModification : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>For example <c>P00001</c> (bilateral) or <c>A00001</c> (administrative).</summary>
    public required string Number { get; set; }

    public DateOnly EffectiveDate { get; set; }
    public required string Description { get; set; }
    public ModificationStatus Status { get; set; } = ModificationStatus.Draft;
    public DateTime? ExecutedAtUtc { get; set; }
    public string? ExecutedBy { get; set; }
}

/// <summary>An obligation (positive) or de-obligation (negative) of funds on a CLIN, usually through a modification.</summary>
public sealed class FundingAction : Entity
{
    public Guid ContractId { get; set; }
    public Guid ClinId { get; set; }
    public Guid? ModificationId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string CreatedBy { get; set; }
}
