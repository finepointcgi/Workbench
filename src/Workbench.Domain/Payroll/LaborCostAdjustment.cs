namespace Workbench.Domain.Payroll;

/// <summary>
/// The labor cost correction for one posted timesheet after a back-dated rate change: the difference between what was
/// posted and what the hours cost at the rates now in effect on each work date. Its journal entries (source
/// <c>LaborAdjustment</c>, source id = this row) post to the work date's fiscal period, or the first open period when
/// that one is closed (<see cref="Redirected"/>). Amounts are cost, not pay, so they can be shown to Finance.
/// </summary>
public sealed class LaborCostAdjustment : Entity
{
    public Guid TimesheetId { get; set; }

    /// <summary>The timesheet revision whose posting was adjusted. A later revision is costed afresh, so this one's entries are reversed.</summary>
    public int TimesheetRevision { get; set; }

    public Guid EmployeeId { get; set; }

    /// <summary>The effective date of the rate change that caused it.</summary>
    public DateOnly RateEffectiveFrom { get; set; }

    /// <summary>Net change to labor cost (positive = more cost).</summary>
    public decimal Amount { get; set; }

    /// <summary>The fiscal periods the hours fall in (names, comma separated).</summary>
    public required string WorkPeriods { get; set; }

    /// <summary>True when a work period was closed, so (part of) the adjustment posted to the first open period.</summary>
    public bool Redirected { get; set; }

    public required string Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
