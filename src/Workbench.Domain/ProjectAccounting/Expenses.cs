namespace Workbench.Domain.ProjectAccounting;

/// <summary>
/// An employee's expense report (usually one trip). Its lines are <see cref="OdcEntry"/> rows of type Travel or Other
/// with <see cref="OdcEntry.ExpenseReportId"/> set; their status follows the report's. Approved by the employee's supervisor.
/// </summary>
public sealed class ExpenseReport : Entity
{
    public Guid EmployeeId { get; set; }
    public required string Title { get; set; }
    public string? Purpose { get; set; }
    public DateOnly? TripStart { get; set; }
    public DateOnly? TripEnd { get; set; }
    public OdcStatus Status { get; set; } = OdcStatus.Draft;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
}
