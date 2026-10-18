using Microsoft.EntityFrameworkCore;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Model = Workbench.Domain.Automation;

namespace Workbench.Application.Automation;

public sealed class RuleService(
    IWorkbenchDbContextFactory dbFactory,
    IItemService items,
    ICurrentUser currentUser,
    TimeProvider clock) : IRuleService
{
    public async Task<IReadOnlyList<RuleDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var rules = await db.Set<Model.Rule>().AsNoTracking().Where(r => r.SheetId == sheetId)
            .OrderBy(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        return rules.Select(r => r.ToDto()).ToList();
    }

    public async Task<RuleDto> GetAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await FindAsync(db, sheetId, ruleId, cancellationToken)).ToDto();
    }

    public async Task<RuleDto> CreateAsync(Guid sheetId, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var columns = await ColumnsAsync(db, sheetId, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var rule = new Model.Rule
        {
            SheetId = sheetId,
            Name = "",
            CreatedBy = currentUser.Email,
            CreatedAtUtc = now,
        };
        Apply(rule, request, columns);
        rule.UpdatedAtUtc = now;
        db.Set<Model.Rule>().Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return rule.ToDto();
    }

    public async Task<RuleDto> UpdateAsync(Guid sheetId, Guid ruleId, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rule = await FindAsync(db, sheetId, ruleId, cancellationToken);
        Apply(rule, request, await ColumnsAsync(db, sheetId, cancellationToken));
        rule.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return rule.ToDto();
    }

    public async Task<RuleDto> SetEnabledAsync(Guid sheetId, Guid ruleId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rule = await FindAsync(db, sheetId, ruleId, cancellationToken);
        rule.Enabled = enabled;
        rule.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return rule.ToDto();
    }

    public async Task DeleteAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rule = await FindAsync(db, sheetId, ruleId, cancellationToken);
        var runs = await db.Set<Model.RuleRun>().Where(r => r.RuleId == ruleId).ToListAsync(cancellationToken);
        db.Set<Model.RuleRun>().RemoveRange(runs);
        db.Set<Model.Rule>().Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RuleRunDto>> ListRunsAsync(Guid sheetId, Guid? ruleId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var query = db.Set<Model.RuleRun>().AsNoTracking().Where(r => r.SheetId == sheetId);
        if (ruleId is { } id)
        {
            query = query.Where(r => r.RuleId == id);
        }

        var runs = await query.OrderByDescending(r => r.FiredAtUtc).ThenByDescending(r => r.Id).Take(limit).ToListAsync(cancellationToken);
        var ruleIds = runs.Select(r => r.RuleId).Distinct().ToList();
        var itemIds = runs.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).Distinct().ToList();
        var names = await db.Set<Model.Rule>().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var titles = await db.Items.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Title, cancellationToken);
        return runs.Select(r => new RuleRunDto(
            r.Id,
            r.RuleId,
            names.GetValueOrDefault(r.RuleId) ?? "(deleted rule)",
            r.ItemId,
            r.ItemId is { } item ? titles.GetValueOrDefault(item) : null,
            r.FiredAtUtc,
            (RuleRunOutcome)(int)r.Outcome,
            r.Detail,
            r.Error)).ToList();
    }

    public async Task<RuleTestResult> TestAsync(Guid sheetId, TestRuleRequest request, CancellationToken cancellationToken = default)
    {
        List<Column> columns;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            await EnsureSheetAsync(db, sheetId, cancellationToken);
            columns = await ColumnsAsync(db, sheetId, cancellationToken);
        }

        var rule = new Model.Rule { SheetId = sheetId, Name = "", CreatedBy = currentUser.Email };
        Apply(rule, request.Rule, columns);
        var item = (await items.ListAsync(sheetId, cancellationToken)).FirstOrDefault(i => i.Id == request.ItemId)
            ?? throw WorkbenchNotFoundException.For("Item", request.ItemId);

        var steps = new List<string>();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        bool triggerMatches;
        var trigger = rule.Trigger;
        var field = RuleLogic.FieldName(trigger.Field, columns);
        switch (trigger.Kind)
        {
            case Model.RuleTriggerKind.ItemCreated:
                triggerMatches = true;
                steps.Add("Trigger: runs when an item is added.");
                break;
            case Model.RuleTriggerKind.ColumnChanged:
            {
                var current = RuleLogic.Read(item, trigger.Field!);
                triggerMatches = trigger.ToValue is null || RuleLogic.Same(current, trigger.ToValue);
                steps.Add(trigger.ToValue is null
                    ? $"Trigger: runs when {field} changes (now {Quote(current)})."
                    : $"Trigger: runs when {field} changes to {Quote(trigger.ToValue)}; it is {Quote(current)}, so it {(triggerMatches ? "would" : "wouldn't")} match.");
                break;
            }

            default:
            {
                var date = RuleLogic.ReadDate(item, trigger.Field!);
                triggerMatches = date is not null;
                steps.Add(date is { } d
                    ? $"Trigger: {field} is {d:yyyy-MM-dd}, so it fires on {d.AddDays(trigger.OffsetDays):yyyy-MM-dd}."
                    : $"Trigger: {field} is empty, so it never fires.");
                break;
            }
        }

        var conditionsMatch = true;
        foreach (var condition in rule.Conditions)
        {
            var holds = RuleLogic.ConditionHolds(condition, item);
            conditionsMatch &= holds;
            var expected = condition.Operator is Model.RuleConditionOperator.IsBlank ? "" : $" {Quote(condition.Value)}";
            steps.Add($"{(holds ? "✓" : "✗")} {RuleLogic.FieldName(condition.Field, columns)} {Describe(condition.Operator)}{expected} (it is {Quote(RuleLogic.Read(item, condition.Field))}).");
        }

        var planned = rule.Actions.Select(a => a.Kind switch
        {
            Model.RuleActionKind.Notify => "Notify " + string.Join(", ",
                a.Emails.Concat(a.PersonFields.SelectMany(f => RuleLogic.People(RuleLogic.Read(item, f)))).Select(Notifier.Normalize).OfType<string>().Distinct()
                    .DefaultIfEmpty("no one")),
            Model.RuleActionKind.SetValue => $"Set {RuleLogic.FieldName(a.Field, columns)} to {Quote(RuleLogic.Render(a.Value, item, columns, today))}",
            Model.RuleActionKind.AssignPerson => $"Assign {RuleLogic.FieldName(a.Field, columns)} to {RuleLogic.Render(a.Value, item, columns, today)}",
            _ => $"Add subitem {Quote(RuleLogic.Render(a.Value, item, columns, today))}",
        }).ToList();

        return new RuleTestResult(triggerMatches, conditionsMatch, steps, planned);
    }

    private static string Describe(Model.RuleConditionOperator op) => op switch
    {
        Model.RuleConditionOperator.Equals => "equals",
        Model.RuleConditionOperator.NotEquals => "doesn't equal",
        Model.RuleConditionOperator.IsBlank => "is blank",
        _ => "contains",
    };

    private static string Quote(string? value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : $"\"{value}\"";

    /// <summary>Validates the request against the sheet's columns and copies it onto the rule.</summary>
    private static void Apply(Model.Rule rule, SaveRuleRequest request, IReadOnlyList<Column> columns)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ArgumentException("Give the rule a name of up to 200 characters.");
        }

        if (request.Trigger is null || !Enum.IsDefined(request.Trigger.Kind))
        {
            throw new ArgumentException("Choose a trigger.");
        }

        var trigger = request.Trigger.ToDomain();
        switch (trigger.Kind)
        {
            case Model.RuleTriggerKind.ItemCreated:
                trigger.Field = null;
                trigger.ToValue = null;
                trigger.OffsetDays = 0;
                break;
            case Model.RuleTriggerKind.ColumnChanged:
                RequireField(trigger.Field, columns, "The trigger");
                trigger.OffsetDays = 0;
                break;
            case Model.RuleTriggerKind.DateReached:
                if (trigger.Field is not (Model.RuleFields.Start or Model.RuleFields.Finish)
                    && !columns.Any(c => c.Id.ToString() == trigger.Field && c.Type == ColumnType.Date))
                {
                    throw new ArgumentException("A date trigger needs Start, Finish or a Date column.");
                }

                if (Math.Abs(trigger.OffsetDays) > 365)
                {
                    throw new ArgumentException("The date offset can be at most 365 days.");
                }

                trigger.ToValue = null;
                break;
        }

        var conditions = (request.Conditions ?? []).Select(c => c.ToDomain()).ToList();
        foreach (var condition in conditions)
        {
            RequireField(condition.Field, columns, "A condition");
            if (!Enum.IsDefined(condition.Operator))
            {
                throw new ArgumentException("A condition has an unknown operator.");
            }
        }

        var actions = (request.Actions ?? []).Select(a => a.ToDomain()).ToList();
        if (actions.Count == 0)
        {
            throw new ArgumentException("Add at least one action.");
        }

        foreach (var action in actions)
        {
            ValidateAction(action, columns);
        }

        rule.Name = name;
        rule.Enabled = request.Enabled;
        rule.Trigger = trigger;
        rule.Conditions = conditions;
        rule.Actions = actions;
    }

    private static void ValidateAction(Model.RuleAction action, IReadOnlyList<Column> columns)
    {
        switch (action.Kind)
        {
            case Model.RuleActionKind.Notify:
                if (action.Emails.FirstOrDefault(e => Notifier.Normalize(e) is null) is { } bad)
                {
                    throw new ArgumentException($"\"{bad}\" isn't a valid email.");
                }

                foreach (var field in action.PersonFields)
                {
                    RequirePerson(field, columns);
                }

                if (action.Emails.Count == 0 && action.PersonFields.Count == 0)
                {
                    throw new ArgumentException("Choose who to notify.");
                }

                break;
            case Model.RuleActionKind.SetValue:
                RequireField(action.Field, columns, "The set-value action");
                if (columns.Any(c => c.Id.ToString() == action.Field && c.Formula is not null))
                {
                    throw new ArgumentException("A formula column can't be set by a rule.");
                }

                break;
            case Model.RuleActionKind.AssignPerson:
                RequirePerson(action.Field, columns);
                if (string.IsNullOrWhiteSpace(action.Value) || (!action.Value.Contains('{') && Notifier.Normalize(action.Value) is null))
                {
                    throw new ArgumentException("Enter the email of the person to assign.");
                }

                break;
            case Model.RuleActionKind.CreateChildItem:
                if (string.IsNullOrWhiteSpace(action.Value))
                {
                    throw new ArgumentException("Enter a title for the subitem.");
                }

                break;
            default:
                throw new ArgumentException("An action has an unknown kind.");
        }
    }

    private static void RequireField(string? field, IReadOnlyList<Column> columns, string what)
    {
        if (field is Model.RuleFields.Title or Model.RuleFields.Start or Model.RuleFields.Finish
            || columns.Any(c => c.Id.ToString() == field))
        {
            return;
        }

        throw new ArgumentException($"{what} refers to a field that doesn't exist.");
    }

    private static void RequirePerson(string? field, IReadOnlyList<Column> columns)
    {
        if (!columns.Any(c => c.Id.ToString() == field && c.Type == ColumnType.Person))
        {
            throw new ArgumentException("Choose a Person column.");
        }
    }

    private static async Task<List<Column>> ColumnsAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);

    private static async Task EnsureSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }
    }

    private static async Task<Model.Rule> FindAsync(IWorkbenchDbContext db, Guid sheetId, Guid ruleId, CancellationToken cancellationToken) =>
        await db.Set<Model.Rule>().FirstOrDefaultAsync(r => r.Id == ruleId && r.SheetId == sheetId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Rule", ruleId);
}
