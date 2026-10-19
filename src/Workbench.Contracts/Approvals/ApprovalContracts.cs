namespace Workbench.Contracts.Approvals;

/// <summary>The document types that go through approval workflows. Add a key here (and a handler) to route a new one.</summary>
public static class ApprovalDocumentTypes
{
    public const string Timesheet = "timesheet";
    public const string ExpenseReport = "expense-report";
    public const string VendorOdc = "odc";
    public const string Requisition = "requisition";
    public const string ApBill = "ap-bill";
    public const string Invoice = "invoice";
    public const string JournalEntry = "journal-entry";
    public const string LeaveRequest = "leave-request";
    public const string HrChange = "hr-change";
    public const string Compensation = "compensation";

    public const string OpportunityGate = "opportunity-gate";

    public static readonly IReadOnlyList<string> All = [Timesheet, ExpenseReport, VendorOdc, Requisition, ApBill, Invoice, JournalEntry, LeaveRequest, HrChange, Compensation, OpportunityGate];

    public static string Label(string documentType) => documentType switch
    {
        Timesheet => "Timesheet",
        ExpenseReport => "Expense report",
        VendorOdc => "Project ODC",
        Requisition => "Purchase requisition",
        ApBill => "AP bill",
        Invoice => "Invoice",
        JournalEntry => "Manual journal entry",
        LeaveRequest => "Leave request",
        HrChange => "Personnel change",
        Compensation => "Compensation change",
        OpportunityGate => "Opportunity gate (go/no-go)",
        _ => documentType,
    };

    /// <summary>Documents whose amount is hours, not money (timesheets and leave requests).</summary>
    public static bool AmountIsHours(string documentType) => documentType is Timesheet or LeaveRequest;
}

/// <summary>Who approves a step.</summary>
public enum ApproverRuleKind
{
    /// <summary>The submitter's supervisor.</summary>
    Supervisor,

    /// <summary>The manager of each charged project: one approval per project, all required.</summary>
    ProjectManager,

    /// <summary>The approver set for each charged org unit (or the submitter's org unit): one approval per unit.</summary>
    OrgUnitManager,

    /// <summary>Any member of an org role (Finance, Admin, ResourceManager, Timekeeper, ProjectManager, HR).</summary>
    /// <summary>Any member of an org role (Finance, Admin, ResourceManager, Timekeeper, ProjectManager, BusinessDevelopment).</summary>
    OrgRole,

    /// <summary>Each listed person: every one of them must approve.</summary>
    SpecificPeople,

    /// <summary>Any one of the listed people.</summary>
    AnyOf,
}

public enum ApprovalOrgRole { Finance, Admin, ResourceManager, Timekeeper, ProjectManager, Hr, Materials, BusinessDevelopment }

/// <summary>What happens when the step's approver is the submitter (DCAA: nobody approves their own document).</summary>
public enum SelfApprovalPolicy
{
    /// <summary>Skip the step (or, with several approvals, just that one).</summary>
    Skip,

    /// <summary>Send it to the submitter's supervisor instead.</summary>
    SubmitterSupervisor,

    /// <summary>Leave it waiting with nobody able to decide, until an administrator changes the routing.</summary>
    Unassigned,

    /// <summary>Let the submitter approve (only for documents that today have no independent review, like invoices).</summary>
    Allow,
}

public enum ApprovalConditionKind
{
    /// <summary>The document total is over <see cref="ApprovalCondition.Amount"/>.</summary>
    AmountOver,

    /// <summary>The document total is under or equal to <see cref="ApprovalCondition.Amount"/>.</summary>
    AmountAtMost,

    /// <summary>The total is over the module's configured policy threshold (e.g. Purchasing:RequisitionFinanceThreshold).</summary>
    AmountOverPolicyThreshold,

    /// <summary>A charge is on one of <see cref="ApprovalCondition.Ids"/> (projects).</summary>
    ProjectIn,

    /// <summary>A charge is on a project of one of <see cref="ApprovalCondition.Ids"/> (contracts).</summary>
    ContractIn,

    /// <summary>A charge (or the submitter) is in one of <see cref="ApprovalCondition.Ids"/> (org units).</summary>
    OrgUnitIn,

    /// <summary>A charge is flagged unallowable (FAR 31.205).</summary>
    HasUnallowableCost,

    /// <summary>A charge has one of the cost types in <see cref="ApprovalCondition.Values"/> (e.g. Travel, Materials).</summary>
    CostTypeIn,

    /// <summary>A charge goes to a GL account rather than a project.</summary>
    HasAccountCharges,

    /// <summary>A project charge has no project manager able to approve it (none, or the submitter).</summary>
    HasUnroutedCharges,
}

public enum ConditionMatch { All, Any }

public sealed record ApproverRule(ApproverRuleKind Kind, ApprovalOrgRole? Role = null, IReadOnlyList<string>? People = null);

public sealed record ApprovalCondition(ApprovalConditionKind Kind, decimal? Amount = null, IReadOnlyList<Guid>? Ids = null, IReadOnlyList<string>? Values = null);

/// <summary>
/// One step. Steps run in order of <see cref="Sequence"/>; steps sharing a sequence run in parallel (all must approve).
/// A step with conditions applies only when they match. <see cref="Fallback"/> is used when the approver rule finds nobody.
/// </summary>
public sealed record ApprovalStepDefinition(
    string Name,
    int Sequence,
    ApproverRule Approver,
    ApproverRule? Fallback = null,
    IReadOnlyList<ApprovalCondition>? Conditions = null,
    ConditionMatch Match = ConditionMatch.All,
    SelfApprovalPolicy WhenSubmitterIsApprover = SelfApprovalPolicy.Skip,
    bool AdminsMayAct = false,
    int? RemindAfterBusinessDays = null,
    bool EscalateToApproverSupervisor = false);

public sealed record ApprovalWorkflowDefinition(IReadOnlyList<ApprovalStepDefinition> Steps);

// ---- Setup (Admins) ----

public sealed record ApprovalWorkflowSummaryDto(string DocumentType, string DocumentLabel, string Name, int Version, int StepCount, DateTime UpdatedAtUtc, int InFlight);

public sealed record ApprovalWorkflowVersionDto(Guid Id, int Version, string CreatedBy, DateTime CreatedAtUtc, string? Note, bool IsCurrent, int InFlight);

public sealed record ApprovalWorkflowDto(
    string DocumentType,
    string DocumentLabel,
    string Name,
    int Version,
    ApprovalWorkflowDefinition Definition,
    IReadOnlyList<ApprovalWorkflowVersionDto> Versions,
    bool CanEdit);

public sealed record SaveApprovalWorkflowRequest(string Name, ApprovalWorkflowDefinition Definition, string? Note = null);

public sealed record SetOrgUnitApproverRequest(Guid? EmployeeId);

public sealed record OrgUnitApproverDto(Guid OrgUnitId, string OrgUnitCode, string OrgUnitName, Guid? EmployeeId, string? EmployeeName);

/// <summary>Approval workflows per document type: read by Finance and Admin, changed by Admin. Every change is a new version.</summary>
public interface IApprovalWorkflowService
{
    Task<IReadOnlyList<ApprovalWorkflowSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ApprovalWorkflowDto> GetAsync(string documentType, CancellationToken cancellationToken = default);

    /// <summary>The steps of an earlier version (in-flight documents may still use it).</summary>
    Task<ApprovalWorkflowDefinition> GetVersionAsync(string documentType, int version, CancellationToken cancellationToken = default);

    /// <summary>Publishes a new version. Documents already submitted keep their version.</summary>
    Task<ApprovalWorkflowDto> SaveAsync(string documentType, SaveApprovalWorkflowRequest request, CancellationToken cancellationToken = default);

    /// <summary>Publishes the built-in default (today's fixed rules) as a new version.</summary>
    Task<ApprovalWorkflowDto> ResetToDefaultAsync(string documentType, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrgUnitApproverDto>> ListOrgUnitApproversAsync(CancellationToken cancellationToken = default);

    Task<OrgUnitApproverDto> SetOrgUnitApproverAsync(Guid orgUnitId, Guid? employeeId, CancellationToken cancellationToken = default);
}

// ---- Inbox (everyone) ----

public enum ApprovalTaskState { Waiting, Pending, Approved, Rejected, Returned, Skipped, Cancelled }

public enum ApprovalInstanceState { Pending, Approved, Rejected, Returned, Cancelled }

public sealed record ApprovalInboxItemDto(
    Guid TaskId,
    Guid InstanceId,
    string DocumentType,
    string DocumentLabel,
    Guid DocumentId,
    string Title,
    string? Summary,
    decimal? Amount,
    string SubmitterEmail,
    string StepName,
    string? ScopeLabel,
    DateTime? WaitingSinceUtc,
    int BusinessDaysWaiting,
    string? OnBehalfOf,
    string Link);

public sealed record ApprovalTaskDto(
    Guid Id,
    int Sequence,
    string StepName,
    string? ScopeLabel,
    ApprovalTaskState Status,
    IReadOnlyList<string> Assignees,
    string? AssigneeRole,
    string? DecidedBy,
    string? OnBehalfOf,
    DateTime? DecidedAtUtc,
    string? Comment,
    string? SkipReason);

public sealed record ApprovalActionDto(string Action, string Actor, string? OnBehalfOf, string? Comment, DateTime OccurredAtUtc, string? StepName);

public sealed record ApprovalInstanceDto(
    Guid Id,
    string DocumentType,
    Guid DocumentId,
    string Title,
    ApprovalInstanceState Status,
    int WorkflowVersion,
    string SubmitterEmail,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<ApprovalTaskDto> Tasks,
    IReadOnlyList<ApprovalActionDto> Trail);

public enum ApprovalDecisionKind { Approve, Reject, Return }

public sealed record ApprovalDecisionRequest(ApprovalDecisionKind Decision, string? Comment = null);

/// <summary>Everything waiting on the signed-in user, across document types, and the approval trail of a document.</summary>
public interface IApprovalInboxService
{
    Task<IReadOnlyList<ApprovalInboxItemDto>> ListMineAsync(CancellationToken cancellationToken = default);

    Task<int> CountMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Approves, rejects or returns the document through its own module (its state changes and posting run as usual).</summary>
    Task DecideAsync(string documentType, Guid documentId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every approval trip of a document, newest first. Visible to the submitter, participants, Finance and Admin.</summary>
    Task<IReadOnlyList<ApprovalInstanceDto>> GetHistoryAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default);
}

// ---- Delegation ----

public sealed record ApprovalDelegationDto(
    Guid Id,
    string DelegatorEmail,
    string DelegateEmail,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string? DocumentType,
    string? Reason,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc,
    bool IsActive);

/// <summary><see cref="DelegatorEmail"/> defaults to the signed-in user; only administrators delegate for someone else.</summary>
public sealed record SaveApprovalDelegationRequest(string DelegateEmail, DateOnly StartsOn, DateOnly EndsOn, string? DocumentType = null, string? Reason = null, string? DelegatorEmail = null);

public interface IApprovalDelegationService
{
    /// <summary>Delegations from or to the signed-in user (administrators: everyone's).</summary>
    Task<IReadOnlyList<ApprovalDelegationDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ApprovalDelegationDto> CreateAsync(SaveApprovalDelegationRequest request, CancellationToken cancellationToken = default);

    Task<ApprovalDelegationDto> RevokeAsync(Guid delegationId, CancellationToken cancellationToken = default);
}
