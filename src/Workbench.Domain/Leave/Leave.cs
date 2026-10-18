namespace Workbench.Domain.Leave;

public enum LeaveType
{
    Pto,
    Sick,
    Holiday,
    Other,
}

public enum AccrualMethod
{
    /// <summary>No accrual (e.g. holidays, which come from the holiday calendar).</summary>
    None,

    /// <summary>A fixed number of hours each pay period.</summary>
    PerPayPeriod,

    /// <summary>Hours per approved hour worked (leave hours don't count as worked).</summary>
    PerHourWorked,

    /// <summary>A lump sum once a calendar year, in the year's first accrual run.</summary>
    AnnualGrant,
}

/// <summary>A kind of leave, the Leave charge code it is recorded on, and how it accrues.</summary>
public sealed class LeavePolicy : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public LeaveType Type { get; set; }

    /// <summary>The Leave WBS element (charge code) this leave is taken on, e.g. <c>LV.PTO</c>.</summary>
    public Guid WbsElementId { get; set; }

    public AccrualMethod Method { get; set; }

    /// <summary>Balance cap: accrual stops at this balance. Tiers can override it.</summary>
    public decimal? MaxBalance { get; set; }

    /// <summary>At year end, hours above this limit are forfeited. Null carries everything over.</summary>
    public decimal? CarryoverLimit { get; set; }

    /// <summary>Days after the hire date before accrual starts.</summary>
    public int WaitingPeriodDays { get; set; }

    /// <summary>When false, timesheets and requests can't take the balance below zero.</summary>
    public bool AllowNegativeBalance { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }

    public List<LeaveAccrualTier> Tiers { get; } = [];
}

/// <summary>An accrual rate from a number of years of service up (the highest tier the employee reaches applies).</summary>
public sealed class LeaveAccrualTier : Entity
{
    public Guid PolicyId { get; set; }
    public int MinYearsOfService { get; set; }

    /// <summary>Hours per pay period, per hour worked or per year, depending on the policy's method.</summary>
    public decimal Rate { get; set; }

    /// <summary>Overrides the policy's balance cap for this tier.</summary>
    public decimal? MaxBalance { get; set; }
}

public enum LeaveLedgerKind
{
    Accrual,
    Grant,
    Usage,
    Adjustment,
    Forfeiture,
}

/// <summary>
/// One movement in an employee's leave balance. Append-only: the balance is the sum of the rows, and a correction is a
/// new row. <see cref="SourceKey"/> makes runs idempotent (<c>accrual:{payPeriodId}</c>, <c>grant:2026</c>,
/// <c>yearend:2026</c>, <c>usage:{timesheetId}</c>).
/// </summary>
public sealed class LeaveLedgerEntry : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid PolicyId { get; set; }
    public DateOnly Date { get; set; }
    public LeaveLedgerKind Kind { get; set; }

    /// <summary>Positive adds to the balance, negative takes from it.</summary>
    public decimal Hours { get; set; }

    public string? SourceKey { get; set; }
    public string? Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum LeaveRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>An employee's request for leave on some days, decided by their supervisor.</summary>
public sealed class LeaveRequest : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid PolicyId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal Hours { get; set; }
    public string? Comment { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }

    public List<LeaveRequestDay> Days { get; } = [];
}

public sealed class LeaveRequestDay : Entity
{
    public Guid RequestId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Hours { get; set; }
}

/// <summary>A company holiday. Charged on the Holiday leave policy's charge code.</summary>
public sealed class Holiday : Entity
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }

    /// <summary>Hours of holiday pay; null means the employee's standard day.</summary>
    public decimal? Hours { get; set; }
}
