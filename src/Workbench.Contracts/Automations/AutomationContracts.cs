using Workbench.Contracts.Customization;

namespace Workbench.Contracts.Automations;

// Admin › Automation: no-code "when this happens, if these are true, do that" across every module (not just sheets, which
// keep their own rules in Contracts/Automation). A definition is versioned JSON; every save publishes a new version.

/// <summary>What starts an automation.</summary>
public enum AutomationTriggerKind
{
    /// <summary>Something happened to a record: created, updated, status changed, submitted, approved, rejected, deleted.</summary>
    RecordEvent,

    /// <summary>A record's field changed, optionally from and/or to a given value.</summary>
    FieldChanged,

    /// <summary>One of the integration events (the webhook catalog), such as <c>invoice.approved</c>.</summary>
    IntegrationEvent,

    /// <summary>Daily, weekly or monthly; for every record of <see cref="AutomationTrigger.EntityType"/> when one is set.</summary>
    Schedule,

    /// <summary>A record's date field is N days away (negative offset) or N days past (positive offset).</summary>
    DateField,

    /// <summary>A measured threshold is crossed (funding, budget, a missing timesheet, an approval waiting).</summary>
    Threshold,

    /// <summary>Only when someone runs it, or another automation starts it.</summary>
    Manual,
}

public enum RecordEventKind
{
    Created,
    Updated,
    StatusChanged,
    Submitted,
    Approved,
    Rejected,
    Deleted,

    /// <summary>Sheets: a row was added by a form (<see cref="AutomationSheets.ItemAddedByFormEvent"/>).</summary>
    AddedByForm,
}

public enum ScheduleFrequency { Daily, Weekly, Monthly }

public enum ThresholdKind
{
    /// <summary>A contract's remaining funding (funded less incurred) is below <see cref="AutomationTrigger.Percent"/> of funded.</summary>
    ContractFundingBelow,

    /// <summary>A project's actual cost is over <see cref="AutomationTrigger.Percent"/> of its current budget.</summary>
    ProjectBudgetOver,

    /// <summary>An employee's timesheet for a finished period isn't submitted <see cref="AutomationTrigger.Days"/> days after it ended.</summary>
    TimesheetMissing,

    /// <summary>A document has waited for approval more than <see cref="AutomationTrigger.Days"/> business days.</summary>
    ApprovalWaiting,
}

/// <param name="EntityType">An <see cref="IEntityFieldCatalog.EntityTypes"/> key (required except for integration events and plain schedules).</param>
/// <param name="Events">Record events that fire it (any of them).</param>
/// <param name="EventKey">The integration event key, for <see cref="AutomationTriggerKind.IntegrationEvent"/>.</param>
/// <param name="FieldKey">The field for <see cref="AutomationTriggerKind.FieldChanged"/> and <see cref="AutomationTriggerKind.DateField"/>.</param>
/// <param name="FromValue">Field changed: only when the old value was this (null = any).</param>
/// <param name="ToValue">Field changed: only when the new value is this (null = any).</param>
/// <param name="Hour">Schedules and date fields: the hour of day (organization time) it runs at, 0–23.</param>
/// <param name="OffsetDays">Date field: -30 = 30 days before the date, 5 = 5 days after it.</param>
/// <param name="Percent">Thresholds: the percentage (25 = 25%).</param>
/// <param name="Days">Thresholds: days late (missing timesheets) or business days waiting (approvals).</param>
/// <param name="SheetId">Sheet automations: the sheet it belongs to (the record type is then <see cref="AutomationSheets.EntityType"/>,
/// fields are column ids or <see cref="AutomationSheets.Title"/>, <see cref="AutomationSheets.Start"/>, <see cref="AutomationSheets.Finish"/>).</param>
public sealed record AutomationTrigger(
    AutomationTriggerKind Kind,
    string? EntityType = null,
    IReadOnlyList<RecordEventKind>? Events = null,
    string? EventKey = null,
    string? FieldKey = null,
    string? FromValue = null,
    string? ToValue = null,
    ScheduleFrequency? Frequency = null,
    int? Hour = null,
    DayOfWeek? DayOfWeek = null,
    int? DayOfMonth = null,
    int? OffsetDays = null,
    ThresholdKind? Threshold = null,
    decimal? Percent = null,
    int? Days = null,
    Guid? SheetId = null);

/// <summary>What a condition looks at.</summary>
public enum ConditionSubject
{
    /// <summary>A field of the record (built-in or custom), by <see cref="AutomationCondition.FieldKey"/>.</summary>
    Field,

    /// <summary>The person whose change fired it (their organization roles): <see cref="ApprovalOrgRoleName"/> values.</summary>
    ActorRole,

    /// <summary>The record's company (ids in <see cref="AutomationCondition.Values"/>).</summary>
    Company,

    /// <summary>The record's org unit (ids).</summary>
    OrgUnit,

    /// <summary>The record's project (ids).</summary>
    Project,
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Contains,
    NotContains,
    StartsWith,
    IsEmpty,
    IsNotEmpty,
    In,
    NotIn,
}

/// <summary>One comparison. Values are invariant strings (numbers <c>1234.5</c>, dates <c>yyyy-MM-dd</c>, booleans <c>true</c>).</summary>
public sealed record AutomationCondition(
    ConditionSubject Subject,
    ConditionOperator Operator,
    string? FieldKey = null,
    string? Value = null,
    IReadOnlyList<string>? Values = null);

public enum GroupLogic { All, Any }

/// <summary>A group of conditions and nested groups, joined by AND (<see cref="GroupLogic.All"/>) or OR (<see cref="GroupLogic.Any"/>).</summary>
public sealed record AutomationConditionGroup(
    GroupLogic Logic = GroupLogic.All,
    IReadOnlyList<AutomationCondition>? Conditions = null,
    IReadOnlyList<AutomationConditionGroup>? Groups = null)
{
    public bool IsEmpty => (Conditions?.Count ?? 0) == 0 && (Groups ?? []).All(g => g.IsEmpty);
}

public enum AutomationActionKind
{
    /// <summary>In-app notification (emailed by each recipient's preference) to people, roles or field-referenced users.</summary>
    Notify,

    /// <summary>Sets a field of the record.</summary>
    SetField,

    /// <summary>Adds a to-do for each recipient (shown on their home page until done).</summary>
    CreateTask,

    /// <summary>Routes the record through a record approval workflow (Setup › Approval workflows, "custom-…").</summary>
    RequireApproval,

    /// <summary>Posts a message to a Teams or Slack connector (Admin › Integrations).</summary>
    PostToChat,

    /// <summary>Sends a signed POST to a webhook endpoint (Admin › Integrations).</summary>
    CallWebhook,

    /// <summary>Starts another automation (manual trigger) on the same record.</summary>
    StartAutomation,

    /// <summary>Refuses the change with <see cref="AutomationAction.Message"/> (validation). Only for record events.</summary>
    Block,

    // ---- Sheet actions (sheet automations only) ----

    /// <summary>Puts the email in <see cref="AutomationAction.Value"/> (a template) into the Person column <see cref="AutomationAction.FieldKey"/>.</summary>
    AssignPerson,

    /// <summary>Adds a sub-item titled <see cref="AutomationAction.Value"/> (a template) under the row.</summary>
    CreateSubItem,

    /// <summary>Adds an item titled <see cref="AutomationAction.Value"/> to <see cref="AutomationAction.TargetSheetId"/> (this sheet when null).</summary>
    CreateItem,

    /// <summary>Moves the row (and its sub-items) to <see cref="AutomationAction.TargetSheetId"/>, matching columns by name.</summary>
    MoveRow,

    /// <summary>Copies the row (and its sub-items) to <see cref="AutomationAction.TargetSheetId"/>, matching columns by name.</summary>
    CopyRow,

    /// <summary>Emails the recipients directly (subject <see cref="AutomationAction.Title"/>, body <see cref="AutomationAction.Message"/>), whatever their notification settings.</summary>
    SendEmail,
}

public enum RecipientKind
{
    /// <summary>An email address.</summary>
    Person,

    /// <summary>Everyone with an organization role: an <see cref="ApprovalOrgRoleName"/> value.</summary>
    Role,

    /// <summary>A field of the record holding an employee id or an email.</summary>
    Field,

    /// <summary>A relationship of the record (<see cref="AutomationRelationships"/>): the submitter, the project manager, ...</summary>
    Relationship,
}

public sealed record AutomationRecipient(RecipientKind Kind, string Value);

/// <param name="Title">Notify/task/chat: the title; <c>{field}</c> tokens are replaced with the record's values.</param>
/// <param name="Message">Notify/chat body, or the refusal message for <see cref="AutomationActionKind.Block"/>.</param>
/// <param name="Value">Set field: the new value (tokens allowed).</param>
/// <param name="DueInDays">Create task: due this many days after the run.</param>
/// <param name="WorkflowKey">Require approval: the record approval workflow's document type key.</param>
public sealed record AutomationAction(
    AutomationActionKind Kind,
    IReadOnlyList<AutomationRecipient>? Recipients = null,
    string? Title = null,
    string? Message = null,
    string? FieldKey = null,
    string? Value = null,
    int? DueInDays = null,
    string? WorkflowKey = null,
    Guid? ConnectorId = null,
    Guid? WebhookEndpointId = null,
    Guid? TargetAutomationId = null,
    Guid? TargetSheetId = null);

public enum AutomationRunAs
{
    /// <summary>The person whose change fired it (for schedules and thresholds: whoever last published it).</summary>
    Actor,

    /// <summary>A designated service user (<see cref="AutomationDefinition.ServicePrincipalEmail"/>), with that user's roles.</summary>
    ServicePrincipal,
}

public sealed record AutomationDefinition(
    AutomationTrigger Trigger,
    AutomationConditionGroup? Conditions,
    IReadOnlyList<AutomationAction> Actions,
    AutomationRunAs RunAs = AutomationRunAs.Actor,
    string? ServicePrincipalEmail = null);

/// <summary>The organization role names conditions and recipients use (same as approval workflows).</summary>
public static class ApprovalOrgRoleName
{
    public static readonly IReadOnlyList<string> All = ["Employee", "Finance", "Admin", "ResourceManager", "Timekeeper", "ProjectManager", "Hr", "Materials", "BusinessDevelopment", "Pricing"];
}

/// <summary>People related to a record that recipients can name. Not every entity type has every relationship.</summary>
public static class AutomationRelationships
{
    public const string Actor = "actor";
    public const string Submitter = "submitter";
    public const string Employee = "employee";
    public const string Supervisor = "supervisor";
    public const string ProjectManager = "projectManager";
    public const string Approvers = "approvers";
    public const string ApproversSupervisors = "approversSupervisors";

    public static readonly IReadOnlyList<string> All = [Actor, Submitter, Employee, Supervisor, ProjectManager, Approvers, ApproversSupervisors];

    public static string Label(string key) => key switch
    {
        Actor => "The person who made the change",
        Submitter => "The submitter",
        Employee => "The employee",
        Supervisor => "The employee's supervisor",
        ProjectManager => "The project manager",
        Approvers => "The current approvers",
        ApproversSupervisors => "The current approvers' supervisors",
        _ => key,
    };
}

// ---- Admin ----

public sealed record AutomationSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    AutomationTriggerKind TriggerKind,
    string? EntityType,
    string TriggerSummary,
    int Version,
    string UpdatedBy,
    DateTime UpdatedAtUtc,
    DateTime? LastRunAtUtc,
    string? LastRunStatus,
    int RunsLast7Days,
    int FailuresLast7Days,
    Guid? SheetId = null,
    string? SheetName = null);

public sealed record AutomationVersionDto(int Version, string CreatedBy, DateTime CreatedAtUtc, string? Note, bool IsCurrent);

public sealed record AutomationDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    int Version,
    AutomationDefinition Definition,
    IReadOnlyList<AutomationVersionDto> Versions,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string UpdatedBy,
    DateTime UpdatedAtUtc,
    string? TemplateKey);

public sealed record SaveAutomationRequest(string Name, AutomationDefinition Definition, string? Description = null, bool IsEnabled = true, string? Note = null, string? TemplateKey = null);

public enum AutomationRunStatus
{
    Succeeded,

    /// <summary>Some actions ran, some failed or were refused.</summary>
    PartiallyFailed,

    Failed,

    /// <summary>The trigger fired but the conditions didn't match; nothing was done.</summary>
    ConditionsNotMet,

    /// <summary>A Block action refused the change.</summary>
    Blocked,

    /// <summary>Not run: disabled, paused, rate limited or stopped by loop protection (see the error).</summary>
    Skipped,

    /// <summary>A dry run: nothing was changed.</summary>
    DryRun,
}

public enum AutomationStepOutcome { Done, WouldDo, Failed, Denied, Skipped }

/// <summary>One action of a run: what it did (or would do), or why it didn't.</summary>
public sealed record AutomationStepDto(int Index, AutomationActionKind Kind, AutomationStepOutcome Outcome, string Detail);

public sealed record AutomationConditionResultDto(string Description, bool Matched, string? Actual);

public sealed record AutomationRunDto(
    Guid Id,
    Guid AutomationId,
    string AutomationName,
    int Version,
    AutomationTriggerKind TriggerKind,
    string? TriggerDetail,
    string? EntityType,
    Guid? RecordId,
    string? RecordLabel,
    string? Actor,
    string Principal,
    int Depth,
    Guid? ParentRunId,
    AutomationRunStatus Status,
    string? Error,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    IReadOnlyList<AutomationStepDto> Steps,
    IReadOnlyList<AutomationConditionResultDto> Conditions,
    IReadOnlyDictionary<string, string?> Inputs);

public sealed record AutomationRunQuery(Guid? AutomationId = null, AutomationRunStatus? Status = null, string? EntityType = null, Guid? RecordId = null, int Take = 100);

/// <summary>
/// Test an automation against a record: either a saved one (<see cref="AutomationId"/>) or the definition being edited.
/// Nothing is changed; the result says what would happen, and the run is logged as a dry run.
/// </summary>
public sealed record AutomationDryRunRequest(Guid? AutomationId, AutomationDefinition? Definition, Guid? RecordId, string? EntityType = null, string? Name = null);

public sealed record AutomationRunNowRequest(Guid? RecordId = null);

public sealed record AutomationSettingsDto(bool PausedAll, string? PausedBy, DateTime? PausedAtUtc, int MaxChainDepth, int MaxRunsPerRecordPerHour);

public sealed record SaveAutomationSettingsRequest(bool PausedAll, int MaxChainDepth, int MaxRunsPerRecordPerHour);

// ---- The builder's pick lists ----

/// <param name="SettableFields">Field keys the "set a field" action may change (custom fields and a few descriptive built-in ones).</param>
public sealed record AutomationEntityTypeDto(string Key, string Label, IReadOnlyList<EntityFieldDto> Fields, IReadOnlyList<string> Relationships, string? ApprovalDocumentType,
    IReadOnlyList<string> SettableFields);

public sealed record AutomationEventDto(string Key, string Label, string Group, string? EntityType);

public sealed record AutomationOptionDto(string Id, string Label);

public sealed record AutomationCatalogDto(
    IReadOnlyList<AutomationEntityTypeDto> EntityTypes,
    IReadOnlyList<AutomationEventDto> Events,
    IReadOnlyList<string> Roles,
    IReadOnlyList<AutomationOptionDto> ApprovalWorkflows,
    IReadOnlyList<AutomationOptionDto> ChatConnectors,
    IReadOnlyList<AutomationOptionDto> WebhookEndpoints,
    IReadOnlyList<AutomationOptionDto> Automations,
    IReadOnlyList<AutomationOptionDto> Companies,
    IReadOnlyList<AutomationOptionDto> OrgUnits,
    IReadOnlyList<AutomationOptionDto> Projects);

/// <summary>
/// A recipe: a plain sentence with blanks (<see cref="Sentence"/> holds <c>{blankKey}</c> tokens, each one of <see cref="Blanks"/>).
/// <see cref="Request"/> is the automation with the blanks' defaults; <see cref="IAutomationService.BuildRecipeAsync"/> fills them in.
/// </summary>
/// <param name="Scope"><see cref="AutomationRecipeScope.Sheet"/> recipes need a sheet; record recipes work across modules.</param>
public sealed record AutomationTemplateDto(string Key, string Name, string Description, string Category, SaveAutomationRequest Request,
    AutomationRecipeScope Scope = AutomationRecipeScope.Record, string? Sentence = null, IReadOnlyList<AutomationRecipeBlankDto>? Blanks = null);

public enum AutomationRecipeScope { Record, Sheet }

/// <summary>What a recipe's blank asks for.</summary>
public enum AutomationRecipeBlankKind
{
    /// <summary>A sheet column (or Title / Start / Finish).</summary>
    Column,

    /// <summary>Start, Finish or a Date column.</summary>
    DateColumn,

    /// <summary>A Person column.</summary>
    PersonColumn,

    /// <summary>A value of the column chosen in <see cref="AutomationRecipeBlankDto.DependsOn"/> (its dropdown options when it has them).</summary>
    ColumnValue,

    /// <summary>An email address.</summary>
    Person,

    /// <summary>Another sheet.</summary>
    Sheet,

    Number,
    Text,

    /// <summary>A record approval workflow (Setup › Approval workflows).</summary>
    ApprovalWorkflow,
}

/// <param name="Default">The value used until it's filled in (null: it must be filled in). For column blanks, a column name to pick when
/// the sheet has one (e.g. "Status").</param>
/// <param name="DependsOn">For <see cref="AutomationRecipeBlankKind.ColumnValue"/>: the column blank whose values it offers.</param>
public sealed record AutomationRecipeBlankDto(string Key, string Label, AutomationRecipeBlankKind Kind, string? Default = null, string? DependsOn = null);

// ---- Sheet automations (same model and builder, scoped to one sheet) ----

/// <summary>Sheet automations: when a row is added, changed or a date arrives, act on the row.</summary>
public static class AutomationSheets
{
    /// <summary>The record type of sheet rows.</summary>
    public const string EntityType = "sheet-item";

    public const string Title = "title";
    public const string Start = "start";
    public const string Finish = "finish";

    /// <summary>
    /// The event a form raises when it adds a row (the forms work raises it; automations listen with a record-event trigger on
    /// <see cref="RecordEventKind.AddedByForm"/>). See <c>Application.Automations.SheetAutomationEvents.AddedByForm</c>.
    /// </summary>
    public const string ItemAddedByFormEvent = "sheet.item.added_by_form";

    /// <summary>Chains of sheet automations (an action triggering another) stop after this many levels, whatever the settings.</summary>
    public const int MaxChainDepth = 3;

    public static bool IsBuiltInField(string? key) => key is Title or Start or Finish;
}

/// <summary>A sheet's column as the builder offers it.</summary>
/// <param name="Type">The column kind (<c>Text</c>, <c>Dropdown</c>, <c>Person</c>, <c>Date</c>, ...), or <c>Title</c>/<c>Date</c> for built-in fields.</param>
public sealed record SheetAutomationFieldDto(string Key, string Label, string Type, IReadOnlyList<string> Options, bool IsFormula);

public sealed record SheetAutomationCatalogDto(
    Guid SheetId,
    string SheetName,
    IReadOnlyList<SheetAutomationFieldDto> Fields,
    IReadOnlyList<AutomationOptionDto> Rows,
    IReadOnlyList<AutomationOptionDto> TargetSheets,
    IReadOnlyList<AutomationOptionDto> ApprovalWorkflows,
    IReadOnlyList<AutomationOptionDto> ChatConnectors,
    IReadOnlyList<AutomationOptionDto> WebhookEndpoints,
    bool CanEdit);

/// <summary>
/// Admin › Automation. Managing needs the "manage automations" permission (administrators until the permission catalog
/// lands); running one by hand needs "run automation". Every change is a new version and an audit row.
/// </summary>
public interface IAutomationService
{
    Task<IReadOnlyList<AutomationSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<AutomationDto> GetAsync(Guid automationId, CancellationToken cancellationToken = default);

    Task<AutomationDefinition> GetVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default);

    Task<AutomationDto> CreateAsync(SaveAutomationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Publishes a new version; runs already logged keep the version they ran.</summary>
    Task<AutomationDto> UpdateAsync(Guid automationId, SaveAutomationRequest request, CancellationToken cancellationToken = default);

    /// <summary>The disable switch for one automation.</summary>
    Task<AutomationDto> SetEnabledAsync(Guid automationId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Publishes an earlier version again as the newest one.</summary>
    Task<AutomationDto> RestoreVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default);

    /// <summary>Removes it from the list; its versions and run log are kept.</summary>
    Task DeleteAsync(Guid automationId, CancellationToken cancellationToken = default);

    Task<AutomationRunDto> DryRunAsync(AutomationDryRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs it now (for real), as the signed-in user or its service user, on a record when it has an entity type.</summary>
    Task<AutomationRunDto> RunNowAsync(Guid automationId, AutomationRunNowRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationRunDto>> ListRunsAsync(AutomationRunQuery query, CancellationToken cancellationToken = default);

    Task<AutomationRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<AutomationCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>Records of an entity type for the dry-run picker, newest first, filtered by <paramref name="search"/>.</summary>
    Task<IReadOnlyList<AutomationOptionDto>> ListRecordsAsync(string entityType, string? search = null, CancellationToken cancellationToken = default);

    IReadOnlyList<AutomationTemplateDto> ListTemplates();

    /// <summary>A recipe with its blanks filled in (blank key → value; missing ones use the defaults), ready for the builder.</summary>
    Task<SaveAutomationRequest> BuildRecipeAsync(string key, Guid? sheetId, IReadOnlyDictionary<string, string?> blanks, CancellationToken cancellationToken = default);

    /// <summary>A sheet's automations. Viewers of the sheet may list them; creating and changing them needs Editor (or managing automations).</summary>
    Task<IReadOnlyList<AutomationSummaryDto>> ListForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>The builder's pick lists for a sheet: its columns and rows, sheets to move or copy to, workflows and connectors.</summary>
    Task<SheetAutomationCatalogDto> GetSheetCatalogAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<AutomationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>The disable switch for every automation at once, and the loop protection limits.</summary>
    Task<AutomationSettingsDto> SaveSettingsAsync(SaveAutomationSettingsRequest request, CancellationToken cancellationToken = default);
}

// ---- Tasks automations create (everyone, their own) ----

public sealed record AutomationTaskDto(Guid Id, string Title, string? Notes, DateOnly? DueOn, string? Link, string AutomationName, DateTime CreatedAtUtc, DateTime? CompletedAtUtc);

public interface IAutomationTaskService
{
    /// <summary>The signed-in user's open tasks from automations.</summary>
    Task<IReadOnlyList<AutomationTaskDto>> ListMineAsync(CancellationToken cancellationToken = default);

    Task<AutomationTaskDto> CompleteAsync(Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when an automation's Block action refuses a change: a 400 with the automation's message.</summary>
public sealed class AutomationBlockedException(string message, Guid automationId) : ArgumentException(message)
{
    public Guid AutomationId { get; } = automationId;
}

/// <summary>
/// Permission keys for the permission catalog (the custom-roles work). Until it lands, <c>IAutomationPermissions</c>
/// answers them: managing and running automations is for administrators; <see cref="FinanceFields"/> is Finance or Admin.
/// </summary>
public static class AutomationPermissionKeys
{
    public const string Manage = "automations.manage";
    public const string Run = "automations.run";

    /// <summary>Field-level key on business-sensitive money fields (contract values, budgets, pipeline values).</summary>
    public const string FinanceFields = "finance";

    public static string RunOne(Guid automationId) => $"{Run}:{automationId}";
}
