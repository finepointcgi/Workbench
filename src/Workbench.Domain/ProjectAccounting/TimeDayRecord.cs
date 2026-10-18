namespace Workbench.Domain.ProjectAccounting;

/// <summary>
/// When an employee first recorded hours for a work date. DCAA expects time to be recorded daily, so this is kept
/// even if the entries are later changed or deleted, and the daily-entry compliance report reads it.
/// </summary>
public sealed class TimeDayRecord : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid TimesheetId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime FirstEnteredAtUtc { get; set; }
}
