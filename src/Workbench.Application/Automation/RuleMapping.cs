using System.Text.Json;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Automations;
using Model = Workbench.Domain.Automation;

namespace Workbench.Application.Automation;

/// <summary>
/// Sheet rules as sheet automations and back. A rule's three triggers and four actions map one to one; conditions are one AND group.
/// The same mapping migrates stored rules (<see cref="Automations.SheetRuleMigration"/>).
/// </summary>
internal static class RuleMapping
{
    public static SaveAutomationRequest ToAutomation(Guid sheetId, SaveRuleRequest r) =>
        new(r.Name, ToDefinition(sheetId, r.Trigger, r.Conditions, r.Actions), IsEnabled: r.Enabled, Note: "Saved as a sheet rule");

    public static AutomationDefinition ToDefinition(Guid sheetId, RuleTriggerDto? t, IReadOnlyList<RuleConditionDto>? conditions, IReadOnlyList<RuleActionDto>? actions)
    {
        var trigger = (t?.Kind) switch
        {
            RuleTriggerKind.ItemCreated => new AutomationTrigger(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.Created]),
            RuleTriggerKind.ColumnChanged => new AutomationTrigger(AutomationTriggerKind.FieldChanged, FieldKey: t.Field, ToValue: t.ToValue),
            RuleTriggerKind.DateReached => new AutomationTrigger(AutomationTriggerKind.DateField, FieldKey: t.Field, OffsetDays: t.OffsetDays),
            _ => throw new ArgumentException("Choose a trigger."),
        };
        var group = (conditions ?? []).Count == 0 ? null : new AutomationConditionGroup(GroupLogic.All, conditions!.Select(c =>
            new AutomationCondition(ConditionSubject.Field, c.Operator switch
            {
                RuleConditionOperator.Equals => ConditionOperator.Equals,
                RuleConditionOperator.NotEquals => ConditionOperator.NotEquals,
                RuleConditionOperator.IsBlank => ConditionOperator.IsEmpty,
                RuleConditionOperator.Contains => ConditionOperator.Contains,
                _ => throw new ArgumentException("A condition has an unknown operator."),
            }, c.Field, c.Operator == RuleConditionOperator.IsBlank ? null : c.Value)).ToList());
        return new AutomationDefinition(trigger with { EntityType = AutomationSheets.EntityType, SheetId = sheetId }, group,
            (actions ?? []).Select(a => a.Kind switch
            {
                RuleActionKind.Notify => new AutomationAction(AutomationActionKind.Notify,
                    (a.Emails ?? []).Select(e => new AutomationRecipient(RecipientKind.Person, e))
                        .Concat((a.PersonFields ?? []).Select(f => new AutomationRecipient(RecipientKind.Field, f))).ToList(),
                    Title: a.Message),
                RuleActionKind.SetValue => new AutomationAction(AutomationActionKind.SetField, FieldKey: a.Field, Value: a.Value),
                RuleActionKind.AssignPerson => new AutomationAction(AutomationActionKind.AssignPerson, FieldKey: a.Field, Value: a.Value),
                RuleActionKind.CreateChildItem => new AutomationAction(AutomationActionKind.CreateSubItem, Value: a.Value),
                _ => throw new ArgumentException("An action has an unknown kind."),
            }).ToList());
    }

    /// <summary>The automation as a rule, with what a rule can express (other triggers read as "item created"; other actions are left out).</summary>
    public static RuleDto ToRule(AutomationDto a)
    {
        var t = a.Definition.Trigger;
        var trigger = t.Kind switch
        {
            AutomationTriggerKind.FieldChanged => new RuleTriggerDto(RuleTriggerKind.ColumnChanged, t.FieldKey, t.ToValue),
            AutomationTriggerKind.DateField => new RuleTriggerDto(RuleTriggerKind.DateReached, t.FieldKey, OffsetDays: t.OffsetDays ?? 0),
            _ => new RuleTriggerDto(RuleTriggerKind.ItemCreated),
        };
        var conditions = (a.Definition.Conditions?.Conditions ?? [])
            .Where(c => c.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals or ConditionOperator.IsEmpty or ConditionOperator.Contains)
            .Select(c => new RuleConditionDto(c.FieldKey ?? "", c.Operator switch
            {
                ConditionOperator.Equals => RuleConditionOperator.Equals,
                ConditionOperator.NotEquals => RuleConditionOperator.NotEquals,
                ConditionOperator.IsEmpty => RuleConditionOperator.IsBlank,
                _ => RuleConditionOperator.Contains,
            }, c.Value)).ToList();
        var actions = new List<RuleActionDto>();
        foreach (var x in a.Definition.Actions)
        {
            switch (x.Kind)
            {
                case AutomationActionKind.Notify:
                    actions.Add(new RuleActionDto(RuleActionKind.Notify,
                        Emails: (x.Recipients ?? []).Where(r => r.Kind == RecipientKind.Person).Select(r => r.Value).ToList(),
                        PersonFields: (x.Recipients ?? []).Where(r => r.Kind == RecipientKind.Field).Select(r => r.Value).ToList(), Message: x.Title));
                    break;
                case AutomationActionKind.SetField:
                    actions.Add(new RuleActionDto(RuleActionKind.SetValue, x.FieldKey, x.Value));
                    break;
                case AutomationActionKind.AssignPerson:
                    actions.Add(new RuleActionDto(RuleActionKind.AssignPerson, x.FieldKey, x.Value));
                    break;
                case AutomationActionKind.CreateSubItem:
                    actions.Add(new RuleActionDto(RuleActionKind.CreateChildItem, Value: x.Value));
                    break;
            }
        }

        return new RuleDto(a.Id, t.SheetId ?? Guid.Empty, a.Name, a.IsEnabled, a.CreatedBy, a.CreatedAtUtc, trigger, conditions, actions);
    }

    public static RuleRunDto ToRuleRun(Domain.Automations.AutomationRun r, string? itemTitle)
    {
        var steps = string.IsNullOrWhiteSpace(r.StepsJson) ? [] : JsonSerializer.Deserialize<List<AutomationStepDto>>(r.StepsJson, Automations.AutomationJson.Options) ?? [];
        var done = steps.Where(s => s.Outcome == AutomationStepOutcome.Done).Select(s => s.Detail).ToList();
        var outcome = r.Status switch
        {
            nameof(AutomationRunStatus.Succeeded) => RuleRunOutcome.Succeeded,
            nameof(AutomationRunStatus.Skipped) => RuleRunOutcome.Skipped,
            _ => RuleRunOutcome.Failed,
        };
        return new RuleRunDto(r.Id, r.AutomationId, r.AutomationName, r.RecordId, itemTitle, r.StartedAtUtc, outcome,
            done.Count == 0 ? null : string.Join("; ", done), r.Error);
    }

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
