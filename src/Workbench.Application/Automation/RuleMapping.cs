using Workbench.Contracts.Automation;
using Model = Workbench.Domain.Automation;

namespace Workbench.Application.Automation;

internal static class RuleMapping
{
    public static RuleDto ToDto(this Model.Rule r) =>
        new(r.Id, r.SheetId, r.Name, r.Enabled, r.CreatedBy, r.CreatedAtUtc, r.Trigger.ToDto(),
            r.Conditions.Select(c => c.ToDto()).ToList(), r.Actions.Select(a => a.ToDto()).ToList());

    public static RuleTriggerDto ToDto(this Model.RuleTrigger t) =>
        new((RuleTriggerKind)(int)t.Kind, t.Field, t.ToValue, t.OffsetDays);

    public static RuleConditionDto ToDto(this Model.RuleCondition c) =>
        new(c.Field, (RuleConditionOperator)(int)c.Operator, c.Value);

    public static RuleActionDto ToDto(this Model.RuleAction a) =>
        new((RuleActionKind)(int)a.Kind, a.Field, a.Value, a.Emails.ToList(), a.PersonFields.ToList(), a.Message);

    public static Model.RuleTrigger ToDomain(this RuleTriggerDto t) => new()
    {
        Kind = (Model.RuleTriggerKind)(int)t.Kind,
        Field = Clean(t.Field),
        ToValue = Clean(t.ToValue),
        OffsetDays = t.OffsetDays,
    };

    public static Model.RuleCondition ToDomain(this RuleConditionDto c) => new()
    {
        Field = c.Field?.Trim() ?? "",
        Operator = (Model.RuleConditionOperator)(int)c.Operator,
        Value = c.Value,
    };

    public static Model.RuleAction ToDomain(this RuleActionDto a) => new()
    {
        Kind = (Model.RuleActionKind)(int)a.Kind,
        Field = Clean(a.Field),
        Value = a.Value,
        Emails = (a.Emails ?? []).Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList(),
        PersonFields = (a.PersonFields ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList(),
        Message = Clean(a.Message),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
