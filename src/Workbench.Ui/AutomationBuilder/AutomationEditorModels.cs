using MudBlazor;
using Workbench.Contracts.Automations;

namespace Workbench.Ui.AutomationBuilder;

// Mutable editor models for the builder; each converts to and from the contract records.

public sealed class TriggerModel
{
    public AutomationTriggerKind Kind { get; set; } = AutomationTriggerKind.RecordEvent;
    public string? EntityType { get; set; }
    public IReadOnlyCollection<RecordEventKind> Events { get; set; } = [RecordEventKind.Submitted];
    public string? EventKey { get; set; }
    public string? FieldKey { get; set; }
    public string? FromValue { get; set; }
    public string? ToValue { get; set; }
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;
    public int Hour { get; set; } = 7;
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    public int DayOfMonth { get; set; } = 1;
    public int OffsetDays { get; set; } = -30;
    public bool OffsetBefore { get; set; } = true;
    public ThresholdKind Threshold { get; set; } = ThresholdKind.ContractFundingBelow;
    public decimal Percent { get; set; } = 25m;
    public int Days { get; set; } = 2;

    public static TriggerModel From(AutomationTrigger t) => new()
    {
        Kind = t.Kind,
        EntityType = t.EntityType,
        Events = t.Events?.ToList() ?? [RecordEventKind.Submitted],
        EventKey = t.EventKey,
        FieldKey = t.FieldKey,
        FromValue = t.FromValue,
        ToValue = t.ToValue,
        Frequency = t.Frequency ?? ScheduleFrequency.Daily,
        Hour = t.Hour ?? 7,
        DayOfWeek = t.DayOfWeek ?? DayOfWeek.Monday,
        DayOfMonth = t.DayOfMonth ?? 1,
        OffsetDays = Math.Abs(t.OffsetDays ?? 30),
        OffsetBefore = (t.OffsetDays ?? -1) <= 0,
        Threshold = t.Threshold ?? ThresholdKind.ContractFundingBelow,
        Percent = t.Percent ?? 25m,
        Days = t.Days ?? 2,
    };

    public AutomationTrigger ToTrigger() => Kind switch
    {
        AutomationTriggerKind.RecordEvent => new(Kind, EntityType, Events.ToList()),
        AutomationTriggerKind.FieldChanged => new(Kind, EntityType, FieldKey: FieldKey, FromValue: Blank(FromValue), ToValue: Blank(ToValue)),
        AutomationTriggerKind.IntegrationEvent => new(Kind, EventKey: EventKey),
        AutomationTriggerKind.Schedule => new(Kind, EntityType, Frequency: Frequency, Hour: Hour,
            DayOfWeek: Frequency == ScheduleFrequency.Weekly ? DayOfWeek : null, DayOfMonth: Frequency == ScheduleFrequency.Monthly ? DayOfMonth : null),
        AutomationTriggerKind.DateField => new(Kind, EntityType, FieldKey: FieldKey, OffsetDays: OffsetBefore ? -OffsetDays : OffsetDays, Hour: Hour),
        AutomationTriggerKind.Threshold => new(Kind, Threshold == ThresholdKind.ApprovalWaiting ? EntityType : null, Threshold: Threshold,
            Percent: Threshold is ThresholdKind.ContractFundingBelow or ThresholdKind.ProjectBudgetOver ? Percent : null,
            Days: Threshold is ThresholdKind.TimesheetMissing or ThresholdKind.ApprovalWaiting ? Days : null),
        _ => new(Kind, EntityType),
    };

    /// <summary>The record type the rest of the builder works with (thresholds imply theirs).</summary>
    public string? EffectiveEntityType => Kind switch
    {
        AutomationTriggerKind.Threshold => Threshold switch
        {
            ThresholdKind.ContractFundingBelow => "contract",
            ThresholdKind.ProjectBudgetOver => "project",
            ThresholdKind.TimesheetMissing => "employee",
            _ => EntityType,
        },
        AutomationTriggerKind.IntegrationEvent => EventKey?.Split('.')[0] switch
        {
            "timesheet" => "timesheet",
            "invoice" => "invoice",
            "bill" => "ap-bill",
            "purchase_order" => "purchase-order",
            "project" => "project",
            "employee" => "employee",
            "opportunity" => "opportunity",
            _ => null,
        },
        _ => EntityType,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ConditionModel
{
    public ConditionSubject Subject { get; set; } = ConditionSubject.Field;
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
    public string? FieldKey { get; set; }
    public string? Value { get; set; }
    public IReadOnlyCollection<string> Values { get; set; } = [];

    public static ConditionModel From(AutomationCondition c) => new()
    {
        Subject = c.Subject, Operator = c.Operator, FieldKey = c.FieldKey, Value = c.Value, Values = c.Values?.ToList() ?? [],
    };

    public AutomationCondition ToCondition() => Subject == ConditionSubject.Field
        ? new(Subject, Operator, FieldKey, Operator is ConditionOperator.IsEmpty or ConditionOperator.IsNotEmpty ? null : Value)
        : new(Subject, Operator, null, null, Operator is ConditionOperator.IsEmpty or ConditionOperator.IsNotEmpty ? null : Values.ToList());
}

public sealed class GroupModel
{
    public GroupLogic Logic { get; set; } = GroupLogic.All;
    public List<ConditionModel> Conditions { get; set; } = [];
    public List<GroupModel> Groups { get; set; } = [];

    public static GroupModel From(AutomationConditionGroup? g) => g is null ? new GroupModel() : new GroupModel
    {
        Logic = g.Logic,
        Conditions = (g.Conditions ?? []).Select(ConditionModel.From).ToList(),
        Groups = (g.Groups ?? []).Select(From).ToList(),
    };

    public AutomationConditionGroup? ToGroup()
    {
        var group = new AutomationConditionGroup(Logic, Conditions.Select(c => c.ToCondition()).ToList(), Groups.Select(g => g.ToGroup()).OfType<AutomationConditionGroup>().ToList());
        return group.IsEmpty ? null : group;
    }
}

public sealed class RecipientModel
{
    public RecipientKind Kind { get; set; } = RecipientKind.Relationship;
    public string Value { get; set; } = AutomationRelationships.ProjectManager;
}

public sealed class ActionModel
{
    public AutomationActionKind Kind { get; set; } = AutomationActionKind.Notify;
    public List<RecipientModel> Recipients { get; set; } = [new()];
    public string? Title { get; set; }
    public string? Message { get; set; }
    public string? FieldKey { get; set; }
    public string? Value { get; set; }
    public int? DueInDays { get; set; }
    public string? WorkflowKey { get; set; }
    public string? ConnectorId { get; set; }
    public string? WebhookEndpointId { get; set; }
    public string? TargetAutomationId { get; set; }

    public static ActionModel From(AutomationAction a) => new()
    {
        Kind = a.Kind,
        Recipients = (a.Recipients ?? []).Select(r => new RecipientModel { Kind = r.Kind, Value = r.Value }).ToList(),
        Title = a.Title,
        Message = a.Message,
        FieldKey = a.FieldKey,
        Value = a.Value,
        DueInDays = a.DueInDays,
        WorkflowKey = a.WorkflowKey,
        ConnectorId = a.ConnectorId?.ToString(),
        WebhookEndpointId = a.WebhookEndpointId?.ToString(),
        TargetAutomationId = a.TargetAutomationId?.ToString(),
    };

    public AutomationAction ToAction() => new(Kind,
        Kind is AutomationActionKind.Notify or AutomationActionKind.CreateTask ? Recipients.Where(r => !string.IsNullOrWhiteSpace(r.Value)).Select(r => new AutomationRecipient(r.Kind, r.Value.Trim())).ToList() : null,
        Kind is AutomationActionKind.Block or AutomationActionKind.SetField or AutomationActionKind.RequireApproval or AutomationActionKind.StartAutomation ? null : Title,
        Kind is AutomationActionKind.SetField or AutomationActionKind.RequireApproval or AutomationActionKind.StartAutomation ? null : Message,
        Kind == AutomationActionKind.SetField ? FieldKey : null,
        Kind == AutomationActionKind.SetField ? Value : null,
        Kind == AutomationActionKind.CreateTask ? DueInDays : null,
        Kind == AutomationActionKind.RequireApproval ? WorkflowKey : null,
        Kind == AutomationActionKind.PostToChat && Guid.TryParse(ConnectorId, out var c) ? c : null,
        Kind == AutomationActionKind.CallWebhook && Guid.TryParse(WebhookEndpointId, out var w) ? w : null,
        Kind == AutomationActionKind.StartAutomation && Guid.TryParse(TargetAutomationId, out var t) ? t : null);
}

/// <summary>Labels and chips shared by the automation pages.</summary>
public static class AutomationUi
{
    public static string Trigger(AutomationTriggerKind kind) => kind switch
    {
        AutomationTriggerKind.RecordEvent => "Something happens to a record",
        AutomationTriggerKind.FieldChanged => "A field changes",
        AutomationTriggerKind.IntegrationEvent => "A Workbench event",
        AutomationTriggerKind.Schedule => "On a schedule",
        AutomationTriggerKind.DateField => "A date is coming up or has passed",
        AutomationTriggerKind.Threshold => "A threshold is crossed",
        _ => "Run by hand or by another automation",
    };

    public static string Event(RecordEventKind e) => e switch
    {
        RecordEventKind.StatusChanged => "Status changed",
        _ => e.ToString(),
    };

    public static string Threshold(ThresholdKind kind) => kind switch
    {
        ThresholdKind.ContractFundingBelow => "Contract funding remaining is below",
        ThresholdKind.ProjectBudgetOver => "Project cost is over this share of budget",
        ThresholdKind.TimesheetMissing => "Timesheet not submitted after the period ends",
        _ => "A document has waited for approval",
    };

    public static string Operator(ConditionOperator op) => op switch
    {
        ConditionOperator.Equals => "is",
        ConditionOperator.NotEquals => "is not",
        ConditionOperator.GreaterThan => "is greater than",
        ConditionOperator.GreaterOrEqual => "is at least",
        ConditionOperator.LessThan => "is less than",
        ConditionOperator.LessOrEqual => "is at most",
        ConditionOperator.Contains => "contains",
        ConditionOperator.NotContains => "doesn't contain",
        ConditionOperator.StartsWith => "starts with",
        ConditionOperator.IsEmpty => "is empty",
        ConditionOperator.IsNotEmpty => "is not empty",
        ConditionOperator.In => "is one of",
        _ => "is not one of",
    };

    public static string Subject(ConditionSubject subject) => subject switch
    {
        ConditionSubject.Field => "A field",
        ConditionSubject.ActorRole => "The person's role",
        ConditionSubject.Company => "The company",
        ConditionSubject.OrgUnit => "The org unit",
        _ => "The project",
    };

    public static string Action(AutomationActionKind kind) => kind switch
    {
        AutomationActionKind.Notify => "Notify people",
        AutomationActionKind.SetField => "Set a field",
        AutomationActionKind.CreateTask => "Create a to-do",
        AutomationActionKind.RequireApproval => "Require an approval",
        AutomationActionKind.PostToChat => "Post to Teams or Slack",
        AutomationActionKind.CallWebhook => "Call a webhook",
        AutomationActionKind.StartAutomation => "Start another automation",
        _ => "Refuse the change (validation)",
    };

    public static string ActionIcon(AutomationActionKind kind) => kind switch
    {
        AutomationActionKind.Notify => Icons.Material.Filled.NotificationsActive,
        AutomationActionKind.SetField => Icons.Material.Filled.EditNote,
        AutomationActionKind.CreateTask => Icons.Material.Filled.TaskAlt,
        AutomationActionKind.RequireApproval => Icons.Material.Filled.AltRoute,
        AutomationActionKind.PostToChat => Icons.Material.Filled.Forum,
        AutomationActionKind.CallWebhook => Icons.Material.Filled.Webhook,
        AutomationActionKind.StartAutomation => Icons.Material.Filled.PlayCircle,
        _ => Icons.Material.Filled.Block,
    };

    public static string Recipient(RecipientKind kind) => kind switch
    {
        RecipientKind.Person => "Person (email)",
        RecipientKind.Role => "Everyone in a role",
        RecipientKind.Field => "Person in a field",
        _ => "Related person",
    };

    public static Color StatusColor(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.Succeeded => Color.Success,
        AutomationRunStatus.Failed => Color.Error,
        AutomationRunStatus.PartiallyFailed => Color.Warning,
        AutomationRunStatus.Blocked => Color.Info,
        AutomationRunStatus.DryRun => Color.Primary,
        _ => Color.Default,
    };

    public static string Status(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.PartiallyFailed => "Partly failed",
        AutomationRunStatus.ConditionsNotMet => "Conditions not met",
        AutomationRunStatus.DryRun => "Dry run",
        _ => status.ToString(),
    };

    public static Color OutcomeColor(AutomationStepOutcome outcome) => outcome switch
    {
        AutomationStepOutcome.Done => Color.Success,
        AutomationStepOutcome.WouldDo => Color.Primary,
        AutomationStepOutcome.Failed => Color.Error,
        AutomationStepOutcome.Denied => Color.Warning,
        _ => Color.Default,
    };

    public static string When(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString("MMM d, h:mm tt") : "—";

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or Workbench.Contracts.WorkbenchNotFoundException;
}
