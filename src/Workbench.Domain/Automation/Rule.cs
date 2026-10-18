namespace Workbench.Domain.Automation;

/// <summary>
/// An automation on a sheet: when <see cref="Trigger"/> happens to an item and every condition holds, run the actions.
/// Trigger, conditions and actions are stored as JSON.
/// </summary>
public sealed class Rule : Entity
{
    public Guid SheetId { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public RuleTrigger Trigger { get; set; } = new();
    public List<RuleCondition> Conditions { get; set; } = [];
    public List<RuleAction> Actions { get; set; } = [];
}

public enum RuleTriggerKind
{
    ItemCreated,
    ColumnChanged,
    DateReached,
}

/// <summary>
/// <see cref="Field"/> names what the trigger watches: a column id, or <see cref="RuleFields.Title"/>,
/// <see cref="RuleFields.Start"/> or <see cref="RuleFields.Finish"/>.
/// </summary>
public sealed class RuleTrigger
{
    public RuleTriggerKind Kind { get; set; }
    public string? Field { get; set; }

    /// <summary>ColumnChanged: only fire when the new value equals this (case-insensitive). Null fires on any change.</summary>
    public string? ToValue { get; set; }

    /// <summary>DateReached: days relative to the date; -2 means two days before.</summary>
    public int OffsetDays { get; set; }
}

public enum RuleConditionOperator
{
    Equals,
    NotEquals,
    IsBlank,
    Contains,
}

public sealed class RuleCondition
{
    public required string Field { get; set; }
    public RuleConditionOperator Operator { get; set; }
    public string? Value { get; set; }
}

public enum RuleActionKind
{
    Notify,
    SetValue,
    AssignPerson,
    CreateChildItem,
}

/// <summary>
/// One step of a rule. Notify uses <see cref="Emails"/>, <see cref="PersonFields"/> and <see cref="Message"/>;
/// SetValue and AssignPerson use <see cref="Field"/> and <see cref="Value"/>; CreateChildItem uses <see cref="Value"/>
/// as the title template. Templates may use <c>{Title}</c> or <c>{Column name}</c>.
/// </summary>
public sealed class RuleAction
{
    public RuleActionKind Kind { get; set; }
    public string? Field { get; set; }
    public string? Value { get; set; }
    public List<string> Emails { get; set; } = [];
    public List<string> PersonFields { get; set; } = [];
    public string? Message { get; set; }
}

/// <summary>Built-in item fields a rule can reference besides column ids.</summary>
public static class RuleFields
{
    public const string Title = "title";
    public const string Start = "start";
    public const string Finish = "finish";
}

public enum RuleRunOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>One time a rule fired (or was skipped) for an item.</summary>
public sealed class RuleRun : Entity
{
    public Guid RuleId { get; set; }
    public Guid SheetId { get; set; }
    public Guid? ItemId { get; set; }
    public DateTime FiredAtUtc { get; set; }
    public RuleRunOutcome Outcome { get; set; }

    /// <summary>What the actions did, e.g. <c>Notified pm@example.com; Set Owner</c>.</summary>
    public string? Detail { get; set; }

    public string? Error { get; set; }

    /// <summary>For date triggers, the date the rule fired for, so it fires once per item and date.</summary>
    public DateOnly? TriggerDate { get; set; }
}
