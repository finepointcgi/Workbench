namespace Workbench.Domain.ProjectAccounting;

/// <summary>
/// An employee's start/stop timer on a charge code. At most one per employee (unique), kept on the server so it survives
/// reloads and follows the person between devices. Stopping it adds the elapsed time to the timesheet and removes the row.
/// </summary>
public sealed class RunningTimer : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid WbsElementId { get; set; }
    public DateTime StartedAtUtc { get; set; }

    /// <summary>What the person is working on; goes into the timesheet cell's comment when the timer stops.</summary>
    public string? Note { get; set; }
}
