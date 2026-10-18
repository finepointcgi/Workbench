namespace Workbench.Domain.Approvals;

/// <summary>
/// The approval workflow for one document type (timesheet, expense report, requisition, ...). Its steps live in
/// immutable <see cref="ApprovalWorkflowVersion"/>s; saving a change adds a version, and documents already in flight
/// keep the version they started with.
/// </summary>
public sealed class ApprovalWorkflow : Entity
{
    /// <summary>A key from <c>ApprovalDocumentTypes</c>, e.g. <c>timesheet</c>. Unique.</summary>
    public required string DocumentType { get; set; }

    public required string Name { get; set; }

    /// <summary>The version new submissions use.</summary>
    public Guid? CurrentVersionId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One published, immutable definition of a workflow's steps (JSON of <c>ApprovalWorkflowDefinition</c>).</summary>
public sealed class ApprovalWorkflowVersion : Entity
{
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    public required string DefinitionJson { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Note { get; set; }
}

public enum ApprovalInstanceStatus
{
    Pending,
    Approved,
    Rejected,
    Returned,
    Cancelled,
}

/// <summary>One trip of one document through its workflow, from submission to the final decision.</summary>
public sealed class ApprovalInstance : Entity
{
    public required string DocumentType { get; set; }
    public Guid DocumentId { get; set; }
    public Guid WorkflowVersionId { get; set; }
    public ApprovalInstanceStatus Status { get; set; } = ApprovalInstanceStatus.Pending;

    /// <summary>Lower-case email of whoever submitted the document (never their own approver).</summary>
    public required string SubmitterEmail { get; set; }

    public required string Title { get; set; }
    public string? Summary { get; set; }
    public decimal? Amount { get; set; }

    /// <summary>App-relative link to the document.</summary>
    public required string Link { get; set; }

    /// <summary>The sequence number whose tasks are pending (steps with the same sequence run in parallel).</summary>
    public int CurrentSequence { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public enum ApprovalTaskStatus
{
    /// <summary>A later step, not yet active.</summary>
    Waiting,
    Pending,
    Approved,
    Rejected,
    Returned,
    Skipped,
    Cancelled,
}

/// <summary>
/// One approval someone must give: a step, for one scope (a charged project or org unit, or the whole document).
/// Any one of the assignees (or of the role's members, or their delegates) decides it.
/// </summary>
public sealed class ApprovalTask : Entity
{
    public Guid InstanceId { get; set; }
    public int StepIndex { get; set; }
    public int Sequence { get; set; }
    public required string StepName { get; set; }

    /// <summary>What the task covers: <c>project:{id}</c>, <c>orgunit:{id}</c>, <c>person:{email}</c>, or null for the whole document.</summary>
    public string? ScopeKey { get; set; }

    public string? ScopeLabel { get; set; }
    public ApprovalTaskStatus Status { get; set; }

    /// <summary>Comma-separated lower-case emails who may decide (any one of them).</summary>
    public string AssigneesCsv { get; set; } = "";

    /// <summary>An org role (Finance, Admin, ...) whose current members may decide, besides the listed assignees.</summary>
    public string? AssigneeRole { get; set; }

    /// <summary>Comma-separated lower-case emails who may never decide this task (the submitter, whoever entered a linked cost).</summary>
    public string ExcludedCsv { get; set; } = "";

    public bool AdminsMayAct { get; set; }
    public int? RemindAfterBusinessDays { get; set; }
    public bool EscalateToSupervisor { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }
    public string? DecidedBy { get; set; }

    /// <summary>When a delegate decided: the assignee they stood in for.</summary>
    public string? OnBehalfOf { get; set; }

    public DateTime? DecidedAtUtc { get; set; }
    public string? Comment { get; set; }
    public string? SkipReason { get; set; }
    public DateTime? LastRemindedAtUtc { get; set; }
    public int RemindersSent { get; set; }
}

/// <summary>Append-only trail of everything that happened to an approval instance.</summary>
public sealed class ApprovalAction : Entity
{
    public Guid InstanceId { get; set; }
    public Guid? TaskId { get; set; }

    /// <summary>Submitted, Approved, Rejected, Returned, Skipped, Activated, Reminded, Escalated, Cancelled, Completed.</summary>
    public required string Action { get; set; }

    public required string Actor { get; set; }
    public string? OnBehalfOf { get; set; }
    public string? Comment { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>An out-of-office delegation: the delegate may decide the delegator's approvals between the dates.</summary>
public sealed class ApprovalDelegation : Entity
{
    public required string DelegatorEmail { get; set; }
    public required string DelegateEmail { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }

    /// <summary>Limits the delegation to one document type; null for all.</summary>
    public string? DocumentType { get; set; }

    public string? Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public bool IsActiveOn(DateOnly date) => RevokedAtUtc is null && StartsOn <= date && date <= EndsOn;
}

/// <summary>Who approves for an org unit (the "org-unit manager" approver rule).</summary>
public sealed class OrgUnitApprover : Entity
{
    public Guid OrgUnitId { get; set; }
    public Guid EmployeeId { get; set; }
}
