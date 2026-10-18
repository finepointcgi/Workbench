namespace Workbench.Contracts.Automation;

public enum RuleTriggerKind
{
    /// <summary>An item is added to the sheet.</summary>
    ItemCreated,

    /// <summary>A field changes; optionally only when it changes to <see cref="RuleTriggerDto.ToValue"/>.</summary>
    ColumnChanged,

    /// <summary>A date field (Start, Finish or a Date column) plus <see cref="RuleTriggerDto.OffsetDays"/> is reached.</summary>
    DateReached,
}

public enum RuleConditionOperator
{
    Equals,
    NotEquals,
    IsBlank,
    Contains,
}

public enum RuleActionKind
{
    /// <summary>Notify <see cref="RuleActionDto.Emails"/> and the people in <see cref="RuleActionDto.PersonFields"/>.</summary>
    Notify,

    /// <summary>Set <see cref="RuleActionDto.Field"/> to <see cref="RuleActionDto.Value"/> (a template).</summary>
    SetValue,

    /// <summary>Set the Person column <see cref="RuleActionDto.Field"/> to the email in <see cref="RuleActionDto.Value"/>.</summary>
    AssignPerson,

    /// <summary>Add a child item titled <see cref="RuleActionDto.Value"/> (a template).</summary>
    CreateChildItem,
}

public enum RuleRunOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>
/// Fields a rule can reference: a column id (<c>Guid.ToString()</c>) or one of these built-in fields.
/// Templates replace <c>{Title}</c> and <c>{Column name}</c> with the item's values.
/// </summary>
public static class RuleFields
{
    public const string Title = "title";
    public const string Start = "start";
    public const string Finish = "finish";

    /// <summary>Rule chains (a rule's action triggering another rule) stop after this many levels.</summary>
    public const int MaxChainDepth = 3;

    /// <summary>The actor recorded for changes made by rules.</summary>
    public const string AutomationActor = "Workbench Automation";
}

public sealed record RuleTriggerDto(RuleTriggerKind Kind, string? Field = null, string? ToValue = null, int OffsetDays = 0);

public sealed record RuleConditionDto(string Field, RuleConditionOperator Operator, string? Value = null);

public sealed record RuleActionDto(
    RuleActionKind Kind,
    string? Field = null,
    string? Value = null,
    IReadOnlyList<string>? Emails = null,
    IReadOnlyList<string>? PersonFields = null,
    string? Message = null);

/// <summary>A rule: when <see cref="Trigger"/> happens and every condition holds (AND), run <see cref="Actions"/> in order.</summary>
public sealed record RuleDto(
    Guid Id,
    Guid SheetId,
    string Name,
    bool Enabled,
    string CreatedBy,
    DateTime CreatedAtUtc,
    RuleTriggerDto Trigger,
    IReadOnlyList<RuleConditionDto> Conditions,
    IReadOnlyList<RuleActionDto> Actions);

public sealed record SaveRuleRequest(
    string Name,
    bool Enabled,
    RuleTriggerDto Trigger,
    IReadOnlyList<RuleConditionDto> Conditions,
    IReadOnlyList<RuleActionDto> Actions);

public sealed record RuleRunDto(
    Guid Id,
    Guid RuleId,
    string RuleName,
    Guid? ItemId,
    string? ItemTitle,
    DateTime FiredAtUtc,
    RuleRunOutcome Outcome,
    string? Detail,
    string? Error);

/// <summary>Dry run of a (possibly unsaved) rule against one item. Nothing is changed or sent.</summary>
public sealed record TestRuleRequest(SaveRuleRequest Rule, Guid ItemId);

/// <summary>
/// <see cref="Steps"/> explains the trigger and each condition; <see cref="PlannedActions"/> describes what would run.
/// </summary>
public sealed record RuleTestResult(bool TriggerMatches, bool ConditionsMatch, IReadOnlyList<string> Steps, IReadOnlyList<string> PlannedActions)
{
    public bool WouldRun => TriggerMatches && ConditionsMatch;
}

public interface IRuleService
{
    Task<IReadOnlyList<RuleDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<RuleDto> GetAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default);

    Task<RuleDto> CreateAsync(Guid sheetId, SaveRuleRequest request, CancellationToken cancellationToken = default);

    Task<RuleDto> UpdateAsync(Guid sheetId, Guid ruleId, SaveRuleRequest request, CancellationToken cancellationToken = default);

    Task<RuleDto> SetEnabledAsync(Guid sheetId, Guid ruleId, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>Recent runs of the sheet's rules (or one rule), newest first.</summary>
    Task<IReadOnlyList<RuleRunDto>> ListRunsAsync(Guid sheetId, Guid? ruleId = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<RuleTestResult> TestAsync(Guid sheetId, TestRuleRequest request, CancellationToken cancellationToken = default);
}
