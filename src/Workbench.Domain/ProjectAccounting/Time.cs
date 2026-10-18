namespace Workbench.Domain.ProjectAccounting;

public enum TimesheetStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
}

/// <summary>One employee's timesheet for one timesheet period.</summary>
public sealed class Timesheet : Entity
{
    public Guid EmployeeId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public TimesheetStatus Status { get; set; } = TimesheetStatus.Draft;

    /// <summary>Employee's certification that the hours are accurate, set on submit.</summary>
    public DateTime? CertifiedAtUtc { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? ApprovedById { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>Incremented each time an approved timesheet is reopened for correction.</summary>
    public int Revision { get; set; }

    /// <summary>Set the first time the timesheet is submitted. From then on every entry change needs a revision and a reason.</summary>
    public DateTime? FirstSubmittedAtUtc { get; set; }

    /// <summary>The attestation text the employee certified, kept with the timesheet as evidence.</summary>
    public string? CertificationText { get; set; }

    /// <summary>Email of whoever approved or rejected the timesheet (a supervisor or, without one, an admin).</summary>
    public string? ReviewedBy { get; set; }

    public DateTime? RejectedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<TimeEntry> Entries { get; } = [];
}

/// <summary>Hours charged to one WBS element on one day.</summary>
public sealed class TimeEntry : Entity
{
    public Guid TimesheetId { get; set; }
    public Guid WbsElementId { get; set; }
    public Guid LaborCategoryId { get; set; }
    public DateOnly WorkDate { get; set; }
    public decimal Hours { get; set; }
    public string? Comment { get; set; }
    public DateTime EnteredAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Append-only record of a change to a time entry after it was first saved. DCAA expects the
/// original value, the new value, who changed it, when and why.
/// </summary>
public sealed class TimeEntryRevision : Entity
{
    public Guid TimeEntryId { get; set; }
    public Guid TimesheetId { get; set; }
    public decimal? OldHours { get; set; }
    public decimal? NewHours { get; set; }
    public Guid? OldWbsElementId { get; set; }
    public Guid? NewWbsElementId { get; set; }
    public Guid? OldLaborCategoryId { get; set; }
    public Guid? NewLaborCategoryId { get; set; }
    public DateOnly WorkDate { get; set; }

    /// <summary>The timesheet's <see cref="Timesheet.Revision"/> when the change was made.</summary>
    public int TimesheetRevision { get; set; }

    public required string ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public required string Reason { get; set; }
}
