namespace Workbench.Domain.Closeout;

public enum CloseoutStatus
{
    Open,
    PhysicallyComplete,
    Closing,
    Closed,
}

/// <summary>The checklist items. Project closeouts use the cost, subcontract, property and retention items.</summary>
public enum CloseoutItem
{
    FinalRatesSettled,
    AllCostsPosted,
    FinalInvoiceIssued,
    SubcontractsClosed,
    PropertyDispositioned,
    FundsDeobligated,
    ReleaseOfClaims,
    AssignmentOfRefunds,
    RecordsRetention,
}

/// <summary>
/// The closeout of one contract or one project (exactly one of <see cref="ContractId"/> and <see cref="ProjectId"/>).
/// A closed contract or project refuses new charges; reopening needs a reason.
/// </summary>
public sealed class CloseoutRecord : Entity
{
    public Guid? ContractId { get; set; }
    public Guid? ProjectId { get; set; }
    public CloseoutStatus Status { get; set; } = CloseoutStatus.Open;
    public DateOnly? PhysicallyCompleteOn { get; set; }

    /// <summary>The invoice marked as the final invoice (contracts).</summary>
    public Guid? FinalInvoiceId { get; set; }

    /// <summary>Funded less billed, as recorded when Finance confirmed the de-obligation.</summary>
    public decimal? DeobligationAmount { get; set; }
    public string? DeobligationRecordedBy { get; set; }
    public DateTime? DeobligationRecordedAtUtc { get; set; }

    public DateOnly? ReleaseOfClaimsOn { get; set; }
    public string? ReleaseOfClaimsNote { get; set; }
    public DateOnly? AssignmentOfRefundsOn { get; set; }
    public string? AssignmentOfRefundsNote { get; set; }
    public DateOnly? RecordsRetentionUntil { get; set; }

    /// <summary>The contract or project status before closing, restored on reopen.</summary>
    public string? StatusBeforeClose { get; set; }

    public DateTime? ClosedAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public int ReopenCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>A checklist item Finance marked as not applicable (or satisfied outside Workbench), with the reason.</summary>
public sealed class CloseoutWaiver : Entity
{
    public Guid CloseoutId { get; set; }
    public CloseoutItem Item { get; set; }
    public required string Reason { get; set; }
    public required string WaivedBy { get; set; }
    public DateTime WaivedAtUtc { get; set; }
}
