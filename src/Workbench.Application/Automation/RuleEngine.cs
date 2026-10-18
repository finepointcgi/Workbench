using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Notifications;
using Workbench.Application.Sheets;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Automation;
using MaxDepth = Workbench.Contracts.Automation.RuleFields;

namespace Workbench.Application.Automation;

/// <summary>
/// Runs rules. Item triggers come from <see cref="AutomationItemService"/>; date triggers from
/// <see cref="DateTriggerBackgroundService"/>. Actions go through the <see cref="IItemService"/> passed in (the
/// decorated chain), so they record activity, publish live changes and can trigger further rules, up to
/// <see cref="MaxDepth.MaxChainDepth"/> levels. Every run is logged as a <see cref="RuleRun"/>.
/// </summary>
public sealed class RuleEngine(IWorkbenchDbContextFactory dbFactory, Notifier notifier, TimeProvider clock, ILogger<RuleEngine>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>A date trigger missed (e.g. while the server was down) still fires up to this many days late.</summary>
    public const int DateCatchUpDays = 7;

    public Task OnItemCreatedAsync(IItemService items, Guid sheetId, ItemDto item) =>
        EvaluateAsync(items, sheetId, item, (rule, _) => rule.Trigger.Kind == RuleTriggerKind.ItemCreated);

    public Task OnItemUpdatedAsync(IItemService items, Guid sheetId, ItemDto before, ItemDto after) =>
        EvaluateAsync(items, sheetId, after, (rule, sheet) =>
            // Formula values aren't stored, so "before" has none to compare; formula columns can't trigger on change.
            !sheet.FormulaColumnIds.Contains(rule.Trigger.Field ?? "")
            && RuleLogic.ChangeMatches(rule.Trigger, before, after));

    /// <summary>
    /// Fires date rules that are due today (or were missed in the last few days) through <paramref name="items"/>,
    /// as whoever that service checks. Returns how many ran. The hourly job uses the overload taking a
    /// <see cref="DateRuleAuthor"/> instead, so each rule runs as its author.
    /// </summary>
    public Task<int> RunDateTriggersAsync(IItemService items, CancellationToken cancellationToken = default) =>
        RunDateTriggersAsync(items, null, cancellationToken);

    /// <summary>The distinct authors (lower-case emails) of enabled date rules, for running each author's rules in their own scope.</summary>
    public async Task<IReadOnlyList<string>> ListDateRuleAuthorsAsync(CancellationToken cancellationToken = default) =>
        (await LoadDateRulesAsync(cancellationToken)).Select(r => AuthorOf(r)).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Fires the due date rules written by <paramref name="author"/> (all of them when null). Before a sheet's rules
    /// run, <see cref="DateRuleAuthor.WhyNotAsync"/> is asked whether the author may still change that sheet; when
    /// not, the rules are skipped and the reason is logged and recorded as a skipped run (once per rule and day).
    /// </summary>
    public async Task<int> RunDateTriggersAsync(IItemService items, DateRuleAuthor? author, CancellationToken cancellationToken = default)
    {
        var today = Today();
        var rules = (await LoadDateRulesAsync(cancellationToken))
            .Where(r => author is null || AuthorOf(r) == author.Email)
            .ToList();

        var fired = 0;
        foreach (var bySheet in rules.GroupBy(r => r.SheetId))
        {
            var sheet = await LoadSheetAsync(bySheet.Key, cancellationToken);
            if (sheet is null)
            {
                continue;
            }

            if (author is not null && await author.WhyNotAsync(sheet.Id, cancellationToken) is { } reason)
            {
                foreach (var rule in bySheet)
                {
                    await RecordSkippedAsync(rule, reason, cancellationToken);
                }

                continue;
            }

            var sheetItems = await items.ListAsync(bySheet.Key, cancellationToken);
            foreach (var rule in bySheet)
            {
                foreach (var item in sheetItems)
                {
                    if (RuleLogic.ReadDate(item, rule.Trigger.Field!) is not { } date)
                    {
                        continue;
                    }

                    var fireOn = date.AddDays(rule.Trigger.OffsetDays);
                    if (fireOn > today || today.DayNumber - fireOn.DayNumber > DateCatchUpDays || !RuleLogic.ConditionsHold(rule, item))
                    {
                        continue;
                    }

                    await using (var db = await dbFactory.CreateAsync(cancellationToken))
                    {
                        if (await db.Set<RuleRun>().AnyAsync(r => r.RuleId == rule.Id && r.ItemId == item.Id && r.TriggerDate == date, cancellationToken))
                        {
                            continue;
                        }
                    }

                    await RunAsync(items, sheet, rule, item, date);
                    fired++;
                }
            }
        }

        return fired;
    }

    private async Task<List<Rule>> LoadDateRulesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<Rule>().AsNoTracking().Where(r => r.Enabled).ToListAsync(cancellationToken))
            .Where(r => r.Trigger.Kind == RuleTriggerKind.DateReached && r.Trigger.Field is not null)
            .ToList();
    }

    private static string AuthorOf(Rule rule) => (rule.CreatedBy ?? "").Trim().ToLowerInvariant();

    /// <summary>Logs why a date rule didn't run and records it as a skipped run, at most once per rule and day.</summary>
    private async Task RecordSkippedAsync(Rule rule, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.Date;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<RuleRun>().AnyAsync(r => r.RuleId == rule.Id && r.ItemId == null && r.Outcome == RuleRunOutcome.Skipped
                && r.FiredAtUtc >= since, cancellationToken))
        {
            _log.LogDebug("Date rule {RuleId} is still skipped: {Reason}", rule.Id, reason);
            return;
        }

        _log.LogWarning("Skipped date rule {RuleId} (\"{RuleName}\") on sheet {SheetId}: {Reason}", rule.Id, rule.Name, rule.SheetId, reason);
        db.Set<RuleRun>().Add(new RuleRun
        {
            RuleId = rule.Id,
            SheetId = rule.SheetId,
            FiredAtUtc = now,
            Outcome = RuleRunOutcome.Skipped,
            Error = Truncate(reason),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EvaluateAsync(IItemService items, Guid sheetId, ItemDto item, Func<Rule, SheetContext, bool> triggered)
    {
        try
        {
            var sheet = await LoadSheetAsync(sheetId, CancellationToken.None);
            if (sheet is null)
            {
                return;
            }

            foreach (var rule in sheet.Rules.Where(r => triggered(r, sheet) && RuleLogic.ConditionsHold(r, item)))
            {
                await RunAsync(items, sheet, rule, item, null);
            }
        }
        catch (Exception ex)
        {
            // Rules must never fail the change a person made.
            _log.LogError(ex, "Evaluating rules for item {ItemId} on sheet {SheetId} failed.", item.Id, sheetId);
        }
    }

    private async Task RunAsync(IItemService items, SheetContext sheet, Rule rule, ItemDto item, DateOnly? triggerDate)
    {
        var run = new RuleRun
        {
            RuleId = rule.Id,
            SheetId = rule.SheetId,
            ItemId = item.Id,
            FiredAtUtc = clock.GetUtcNow().UtcDateTime,
            TriggerDate = triggerDate,
        };

        if (AutomationContext.Depth >= MaxDepth.MaxChainDepth)
        {
            run.Outcome = RuleRunOutcome.Skipped;
            run.Error = $"Stopped: rules triggering rules can only go {MaxDepth.MaxChainDepth} levels deep.";
        }
        else
        {
            var details = new List<string>();
            try
            {
                using (AutomationContext.Enter(rule.Name, rule.CreatedBy))
                {
                    foreach (var action in rule.Actions)
                    {
                        details.Add(await ExecuteAsync(items, sheet, rule, action, item));
                    }
                }

                run.Outcome = RuleRunOutcome.Succeeded;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Rule {RuleId} failed on item {ItemId}.", rule.Id, item.Id);
                run.Outcome = RuleRunOutcome.Failed;
                run.Error = Truncate(ex.Message);
            }

            run.Detail = details.Count == 0 ? null : Truncate(string.Join("; ", details));
        }

        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        db.Set<RuleRun>().Add(run);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<string> ExecuteAsync(IItemService items, SheetContext sheet, Rule rule, RuleAction action, ItemDto snapshot)
    {
        // Re-read the item so each action sees the previous one's changes. Formula values aren't stored, so keep
        // the computed ones from the snapshot for templates and conditions.
        Item stored;
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            stored = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == snapshot.Id && i.SheetId == sheet.Id)
                ?? throw new InvalidOperationException("The item was deleted.");
        }

        var current = stored.ToDto();
        var values = new Dictionary<string, string?>(current.Values);
        foreach (var id in sheet.FormulaColumnIds)
        {
            values[id] = snapshot.Values.GetValueOrDefault(id);
        }

        current = current with { Values = values };
        var today = Today();

        switch (action.Kind)
        {
            case RuleActionKind.Notify:
            {
                var recipients = action.Emails
                    .Concat(action.PersonFields.SelectMany(f => RuleLogic.People(RuleLogic.Read(current, f))))
                    .ToList();
                var title = action.Message is null
                    ? $"{rule.Name}: {current.Title}"
                    : RuleLogic.Render(action.Message, current, sheet.Columns, today);
                var notified = await notifier.NotifyAsync(recipients, new NotificationRequest(
                    NotificationKinds.Rule,
                    title,
                    $"Rule \"{rule.Name}\" ran on \"{current.Title}\" in {sheet.Name}.",
                    sheet.Id,
                    current.Id));
                return notified.Count == 0 ? "No one to notify" : $"Notified {string.Join(", ", notified)}";
            }

            case RuleActionKind.SetValue:
            {
                var value = RuleLogic.Render(action.Value, current, sheet.Columns, today);
                await items.UpdateAsync(sheet.Id, current.Id, WithField(stored, action.Field!, value, sheet));
                var name = RuleLogic.FieldName(action.Field, sheet.Columns);
                return RuleLogic.IsBlank(value) ? $"Cleared {name}" : $"Set {name} to \"{value}\"";
            }

            case RuleActionKind.AssignPerson:
            {
                var email = Notifier.Normalize(RuleLogic.Render(action.Value, current, sheet.Columns, today))
                    ?? throw new ArgumentException("The person to assign isn't a valid email.");
                await items.UpdateAsync(sheet.Id, current.Id, WithField(stored, action.Field!, email, sheet));
                return $"Assigned {RuleLogic.FieldName(action.Field, sheet.Columns)} to {email}";
            }

            case RuleActionKind.CreateChildItem:
            {
                var title = RuleLogic.Render(action.Value, current, sheet.Columns, today).Trim();
                if (title.Length == 0)
                {
                    throw new ArgumentException("The subitem title came out empty.");
                }

                await items.CreateAsync(sheet.Id, new CreateItemRequest(title, ParentId: current.Id));
                return $"Added subitem \"{title}\"";
            }

            default:
                throw new InvalidOperationException($"Unknown action {action.Kind}.");
        }
    }

    private static UpdateItemRequest WithField(Item item, string field, string value, SheetContext sheet)
    {
        var blank = RuleLogic.IsBlank(value);
        var request = new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId, new Dictionary<string, string?>(item.Values));
        switch (field)
        {
            case Workbench.Domain.Automation.RuleFields.Title:
                return blank ? throw new ArgumentException("Title can't be empty.") : request with { Title = value.Trim() };
            case Workbench.Domain.Automation.RuleFields.Start:
                return request with { Start = blank ? null : ParseDate(value) };
            case Workbench.Domain.Automation.RuleFields.Finish:
                return request with { Finish = blank ? null : ParseDate(value) };
        }

        if (sheet.FormulaColumnIds.Contains(field))
        {
            throw new ArgumentException("A formula column can't be set by a rule.");
        }

        if (sheet.Columns.All(c => c.Id.ToString() != field))
        {
            throw new ArgumentException("The column no longer exists.");
        }

        var values = new Dictionary<string, string?>(item.Values);
        if (blank)
        {
            values.Remove(field);
        }
        else
        {
            values[field] = value.Trim();
        }

        return request with { Values = values };
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", out var date) ? date : throw new ArgumentException($"\"{value}\" isn't a yyyy-MM-dd date.");

    private async Task<SheetContext?> LoadSheetAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var name = await db.Sheets.Where(s => s.Id == sheetId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken);
        if (name is null)
        {
            return null;
        }

        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var rules = await db.Set<Rule>().AsNoTracking().Where(r => r.SheetId == sheetId && r.Enabled)
            .OrderBy(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        return new SheetContext(sheetId, name, columns, rules,
            columns.Where(c => c.Formula is not null).Select(c => c.Id.ToString()).ToHashSet());
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private static string Truncate(string value) => value.Length > 2_000 ? value[..1_999] + "…" : value;

    private sealed record SheetContext(Guid Id, string Name, IReadOnlyList<Column> Columns, IReadOnlyList<Rule> Rules, HashSet<string> FormulaColumnIds);
}

/// <summary>
/// Whose date rules a run fires, and the run-time check that the author may still change a sheet: returns why not
/// (deactivated, no edit access), or null when the rules may run.
/// </summary>
public sealed record DateRuleAuthor(string Email, Func<Guid, CancellationToken, Task<string?>> WhyNotAsync);
