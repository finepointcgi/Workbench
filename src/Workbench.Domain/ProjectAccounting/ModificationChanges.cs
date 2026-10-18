namespace Workbench.Domain.ProjectAccounting;

public enum ModificationChangeKind
{
    AddClin,
    ExerciseOption,
    ChangeClinValue,
    ExtendPeriod,
    Fund,
}

/// <summary>One change in a <see cref="ContractModification"/>. CLINs are named by number so a mod can add and fund a CLIN.</summary>
public sealed class ContractModificationChange : Entity
{
    public Guid ModificationId { get; set; }
    public int Sequence { get; set; }
    public ModificationChangeKind Kind { get; set; }
    public string? ClinNumber { get; set; }
    public decimal? Amount { get; set; }
    public DateOnly? NewPeriodEnd { get; set; }
    public string? ClinDescription { get; set; }
    public ClinPricing? Pricing { get; set; }
    public bool IsOption { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public decimal? FeeRate { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// A Limitation of Funds / Limitation of Cost notice that was sent for a CLIN. Append-only; one per CLIN, threshold and
/// funded value, so a notice isn't repeated until new funding changes the funded value.
/// </summary>
public sealed class ClinFundingNotice : Entity
{
    public Guid ContractId { get; set; }
    public Guid ClinId { get; set; }
    public int ThresholdPercent { get; set; }
    public decimal FundedValue { get; set; }
    public decimal IncurredAndCommitted { get; set; }
    public required string Title { get; set; }
    public required string Recipients { get; set; }
    public DateTime SentAtUtc { get; set; }
}
