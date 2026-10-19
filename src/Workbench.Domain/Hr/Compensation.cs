using Workbench.Domain.Payroll;

namespace Workbench.Domain.Hr;

public enum CompensationReason
{
    NewHire,
    Merit,
    Promotion,
    MarketAdjustment,
    Equity,
    Correction,
    Other,
}

public enum CompensationChangeStatus
{
    /// <summary>A merit cycle proposal not yet submitted.</summary>
    Draft,
    Pending,

    /// <summary>Approved and written to the pay rate history (<see cref="EmployeePayRate"/>).</summary>
    Applied,
    Rejected,
    Cancelled,
}

/// <summary>
/// A proposed salary or rate change. Approved through the approval engine (document type <c>compensation</c>: HR, then
/// Finance when the annualized increase is over the threshold); the final approval appends an <see cref="EmployeePayRate"/>
/// row, which is what payroll and the Paylocity sync read. Amounts never go into audit rows.
/// </summary>
public sealed class CompensationChange : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid? CycleId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public PayType PayType { get; set; }
    public decimal? AnnualSalary { get; set; }
    public decimal? HourlyRate { get; set; }

    /// <summary>The annualized pay in effect before the change (for the increase and the Finance threshold).</summary>
    public decimal? PreviousAnnualized { get; set; }

    public CompensationReason Reason { get; set; }
    public string? Note { get; set; }
    public CompensationChangeStatus Status { get; set; } = CompensationChangeStatus.Pending;
    public required string RequestedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }

    /// <summary>The pay rate row written when it was applied.</summary>
    public Guid? PayRateId { get; set; }

    /// <summary>Annual pay: the salary, or the hourly rate × 2,080.</summary>
    public decimal? Annualized => PayType == PayType.Salaried ? AnnualSalary : HourlyRate * 2080m;
}

public enum MeritCycleStatus
{
    Planning,
    Submitted,
    Closed,
}

/// <summary>A merit/raise cycle: proposals (draft <see cref="CompensationChange"/>s) against a budget, submitted together.</summary>
public sealed class MeritCycle : Entity
{
    public required string Name { get; set; }
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Budget as a fraction of covered pay (0.03 = 3%).</summary>
    public decimal BudgetPercent { get; set; }

    public MeritCycleStatus Status { get; set; } = MeritCycleStatus.Planning;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
