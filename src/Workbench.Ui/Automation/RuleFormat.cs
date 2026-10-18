using Workbench.Contracts.Automation;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Automation;

/// <summary>A field a rule can use: a built-in item field or a column.</summary>
public sealed record RuleField(string Key, string Name, ColumnKind Kind, IReadOnlyList<string> Options, bool IsFormula);

internal static class RuleFormat
{
    public static IReadOnlyList<RuleField> Fields(SheetDetail sheet) =>
    [
        new(RuleFields.Title, "Title", ColumnKind.Text, [], false),
        new(RuleFields.Start, "Start", ColumnKind.Date, [], false),
        new(RuleFields.Finish, "Finish", ColumnKind.Date, [], false),
        .. sheet.Columns.OrderBy(c => c.Order).Select(c => new RuleField(c.Id.ToString(), c.Name, c.Type, c.Options, c.Formula is not null)),
    ];

    public static string FieldName(SheetDetail sheet, string? key) =>
        Fields(sheet).FirstOrDefault(f => f.Key == key)?.Name ?? "(deleted column)";

    public static string Describe(SheetDetail sheet, RuleDto rule)
    {
        var trigger = rule.Trigger;
        var when = trigger.Kind switch
        {
            RuleTriggerKind.ItemCreated => "When an item is added",
            RuleTriggerKind.ColumnChanged when trigger.ToValue is { } to => $"When {FieldName(sheet, trigger.Field)} changes to \"{to}\"",
            RuleTriggerKind.ColumnChanged => $"When {FieldName(sheet, trigger.Field)} changes",
            _ => $"{Offset(trigger.OffsetDays)} {FieldName(sheet, trigger.Field)}",
        };
        var conditions = rule.Conditions.Count == 0
            ? ""
            : " and " + string.Join(" and ", rule.Conditions.Select(c => $"{FieldName(sheet, c.Field)} {OperatorName(c.Operator)}{(c.Operator == RuleConditionOperator.IsBlank ? "" : $" \"{c.Value}\"")}"));
        return $"{when}{conditions} → {string.Join(", ", rule.Actions.Select(a => ActionSummary(sheet, a)))}";
    }

    public static string Offset(int days) => days switch
    {
        0 => "On",
        < 0 => $"{-days} day{(days == -1 ? "" : "s")} before",
        _ => $"{days} day{(days == 1 ? "" : "s")} after",
    };

    public static string ActionSummary(SheetDetail sheet, RuleActionDto action) => action.Kind switch
    {
        RuleActionKind.Notify => "notify " + string.Join(", ",
            (action.Emails ?? []).Concat((action.PersonFields ?? []).Select(f => FieldName(sheet, f))).DefaultIfEmpty("no one")),
        RuleActionKind.SetValue => $"set {FieldName(sheet, action.Field)} to \"{action.Value}\"",
        RuleActionKind.AssignPerson => $"assign {FieldName(sheet, action.Field)} to {action.Value}",
        _ => $"add subitem \"{action.Value}\"",
    };

    public static string OperatorName(RuleConditionOperator op) => op switch
    {
        RuleConditionOperator.Equals => "equals",
        RuleConditionOperator.NotEquals => "doesn't equal",
        RuleConditionOperator.IsBlank => "is blank",
        _ => "contains",
    };

    public static string TriggerName(RuleTriggerKind kind) => kind switch
    {
        RuleTriggerKind.ItemCreated => "An item is added",
        RuleTriggerKind.ColumnChanged => "A field changes",
        _ => "A date is reached",
    };

    public static string ActionName(RuleActionKind kind) => kind switch
    {
        RuleActionKind.Notify => "Notify people",
        RuleActionKind.SetValue => "Set a field",
        RuleActionKind.AssignPerson => "Assign a person",
        _ => "Add a subitem",
    };
}

/// <summary>Editable copies of the rule records for the editor dialog.</summary>
internal sealed class ConditionModel
{
    public string Field { get; set; } = RuleFields.Title;
    public RuleConditionOperator Operator { get; set; }
    public string? Value { get; set; }
}

internal sealed class ActionModel
{
    public RuleActionKind Kind { get; set; }
    public string? Field { get; set; }
    public string? Value { get; set; }
    public string? Emails { get; set; }
    public IReadOnlyCollection<string> PersonFields { get; set; } = [];
    public string? Message { get; set; }

    public RuleActionDto ToDto() => new(
        Kind,
        Kind is RuleActionKind.SetValue or RuleActionKind.AssignPerson ? Field : null,
        Value,
        Kind == RuleActionKind.Notify
            ? (Emails ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [],
        Kind == RuleActionKind.Notify ? PersonFields.ToList() : [],
        Kind == RuleActionKind.Notify ? Message : null);

    public static ActionModel From(RuleActionDto a) => new()
    {
        Kind = a.Kind,
        Field = a.Field,
        Value = a.Value,
        Emails = string.Join(", ", a.Emails ?? []),
        PersonFields = (a.PersonFields ?? []).ToList(),
        Message = a.Message,
    };
}
