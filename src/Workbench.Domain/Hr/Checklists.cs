namespace Workbench.Domain.Hr;

public enum ChecklistKind
{
    Onboarding,
    Offboarding,
}

/// <summary>Who does a checklist task: HR, IT (administrators), the manager (supervisor), the employee, or Finance.</summary>
public enum ChecklistOwner
{
    Hr,
    It,
    Manager,
    Employee,
    Finance,
}

/// <summary>Tasks Workbench carries out itself when they're completed.</summary>
public enum ChecklistAction
{
    None,

    /// <summary>Deactivates the employee's sign-in (user lifecycle; SCIM-managed users are also removed at the identity provider).</summary>
    DeprovisionAccess,

    /// <summary>Returns one asset in the employee's custody (fixed assets transfer, custodian cleared).</summary>
    RecoverAsset,

    /// <summary>The final timesheet (covering the last day) is approved.</summary>
    FinalTimesheet,

    /// <summary>Records the leave balance to pay out with the final paycheck.</summary>
    LeavePayout,

    /// <summary>Ends the employee's benefit elections at the end of the month of the last day.</summary>
    EndBenefits,
}

public sealed class ChecklistTemplate : Entity
{
    public required string Name { get; set; }
    public ChecklistKind Kind { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }

    public List<ChecklistTemplateTask> Tasks { get; } = [];
}

public sealed class ChecklistTemplateTask : Entity
{
    public Guid TemplateId { get; set; }
    public int Sequence { get; set; }
    public required string Title { get; set; }
    public ChecklistOwner Owner { get; set; }

    /// <summary>Due this many days after the start date (onboarding) or the last day (offboarding); negative is before.</summary>
    public int DueOffsetDays { get; set; }
}

public enum ChecklistStatus
{
    Open,
    Completed,
    Cancelled,
}

/// <summary>One employee's onboarding or offboarding, with its tasks.</summary>
public sealed class EmployeeChecklist : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid? TemplateId { get; set; }
    public ChecklistKind Kind { get; set; }

    /// <summary>The start date (onboarding) or the last day worked (offboarding).</summary>
    public DateOnly AnchorDate { get; set; }

    public ChecklistStatus Status { get; set; } = ChecklistStatus.Open;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    // Offboarding flags for payroll.
    public TerminationReason? TerminationReason { get; set; }

    /// <summary>Hours of leave to pay out with the final paycheck (recorded by the leave payout task).</summary>
    public decimal? LeavePayoutHours { get; set; }

    public bool FinalTimesheetApproved { get; set; }
    public DateTime? AccessDeprovisionedAtUtc { get; set; }

    public List<ChecklistTask> Tasks { get; } = [];
}

public enum ChecklistTaskStatus
{
    Open,
    Done,
    Skipped,
}

public sealed class ChecklistTask : Entity
{
    public Guid ChecklistId { get; set; }
    public int Sequence { get; set; }
    public required string Title { get; set; }
    public ChecklistOwner Owner { get; set; }

    /// <summary>The person it's assigned to (manager, employee); null for role tasks (HR, IT, Finance).</summary>
    public string? AssigneeEmail { get; set; }

    public DateOnly DueDate { get; set; }
    public ChecklistAction Action { get; set; }

    /// <summary>The asset to recover (<see cref="ChecklistAction.RecoverAsset"/>).</summary>
    public Guid? AssetId { get; set; }

    public ChecklistTaskStatus Status { get; set; } = ChecklistTaskStatus.Open;
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Note { get; set; }
}
