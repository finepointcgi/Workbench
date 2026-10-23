using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Approvals;
using Workbench.Application.Automation;
using Workbench.Application.Notifications;
using Workbench.Application.Sheets;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Automations;
using Workbench.Domain.Identity;

namespace Workbench.Application.Automations;

/// <summary>
/// Runs sheet automations (the Admin › Automation model, scoped to a sheet with <see cref="AutomationTrigger.SheetId"/>). Row triggers
/// come from <see cref="AutomationItemService"/> as the change is made, so they finish before the person's request returns; dates and
/// recurring schedules from the hourly <see cref="DateTriggerBackgroundService"/>; Excel imports through <see cref="SheetAutomations"/>.
/// <para>
/// Actions go through the <see cref="IItemService"/> passed in (the decorated chain inside the permission checks), so they record
/// activity as "Workbench Automation" naming the automation and its author, publish live changes and can trigger further
/// automations. Loop protection: chains stop at <see cref="AutomationSheets.MaxChainDepth"/> levels (or the tenant's lower limit),
/// one automation runs at most the tenant's limit per row and hour, and "pause every automation" stops them all. Actions that
/// reach another sheet are only done when the author can edit it. Every run is logged in the automation run log.
/// </para>
/// </summary>
public sealed class SheetAutomationRuntime(IWorkbenchDbContextFactory dbFactory, Notifier notifier, IEmailSender email, ApprovalEngine approvals, TimeProvider clock,
    ILogger<SheetAutomationRuntime>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>A date trigger missed (e.g. while the server was down) still fires up to this many days late.</summary>
    public const int DateCatchUpDays = 7;

    public Task OnItemCreatedAsync(IItemService items, Guid sheetId, ItemDto item, string? formName = null) =>
        EvaluateAsync(items, sheetId, item, (automation, _) =>
        {
            var t = automation.Definition.Trigger;
            if (t.Kind != AutomationTriggerKind.RecordEvent)
            {
                return null;
            }

            var events = t.Events ?? [];
            if (formName is not null && events.Contains(RecordEventKind.AddedByForm))
            {
                return $"Added by the form \"{formName}\"";
            }

            return events.Contains(RecordEventKind.Created) ? "Item added" : null;
        });

    public Task OnItemUpdatedAsync(IItemService items, Guid sheetId, ItemDto before, ItemDto after) =>
        EvaluateAsync(items, sheetId, after, (automation, sheet) =>
        {
            var t = automation.Definition.Trigger;
            if (t.Kind != AutomationTriggerKind.FieldChanged || t.FieldKey is not { } field
                // Formula values aren't stored, so "before" has none to compare; formula columns can't trigger on change.
                || sheet.FormulaColumnIds.Contains(field))
            {
                return null;
            }

            var from = RuleLogic.Read(before, field);
            var to = RuleLogic.Read(after, field);
            if (RuleLogic.Same(from, to) || (t.FromValue is not null && !RuleLogic.Same(from, t.FromValue)) || (t.ToValue is not null && !RuleLogic.Same(to, t.ToValue)))
            {
                return null;
            }

            return $"{RuleLogic.FieldName(field, sheet.Columns)} changed to {Quote(to)}";
        });

    /// <summary>
    /// Fires sheet automations whose date has arrived (or was missed in the last few days) and recurring schedules that are due,
    /// through <paramref name="items"/>, as whoever that service checks. Returns how many ran. The hourly job passes a
    /// <see cref="DateRuleAuthor"/> so each automation runs as its author.
    /// </summary>
    public Task<int> RunDateTriggersAsync(IItemService items, CancellationToken cancellationToken = default) =>
        RunDateTriggersAsync(items, null, cancellationToken);

    /// <summary>The distinct authors (lower-case emails) of enabled date and schedule automations, for running each author's in their own scope.</summary>
    public async Task<IReadOnlyList<string>> ListDateRuleAuthorsAsync(CancellationToken cancellationToken = default) =>
        (await LoadTimedAsync(cancellationToken)).Select(a => a.Author).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Fires the due date and schedule automations published by <paramref name="author"/> (all of them when null). Before a sheet's
    /// run, <see cref="DateRuleAuthor.WhyNotAsync"/> is asked whether the author may still change that sheet; when not, they are
    /// skipped and the reason logged and recorded as a skipped run (once per automation and day).
    /// </summary>
    public async Task<int> RunDateTriggersAsync(IItemService items, DateRuleAuthor? author, CancellationToken cancellationToken = default)
    {
        if (await PausedAsync(cancellationToken))
        {
            return 0;
        }

        var local = clock.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(local);
        var automations = (await LoadTimedAsync(cancellationToken)).Where(a => author is null || a.Author == author.Email).ToList();
        var fired = 0;
        foreach (var bySheet in automations.GroupBy(a => a.SheetId))
        {
            var sheet = await LoadSheetAsync(bySheet.Key, cancellationToken);
            if (sheet is null)
            {
                continue;
            }

            if (author is not null && await author.WhyNotAsync(sheet.Id, cancellationToken) is { } reason)
            {
                foreach (var automation in bySheet)
                {
                    await RecordSkippedAsync(automation, reason, cancellationToken);
                }

                continue;
            }

            var sheetItems = await items.ListAsync(bySheet.Key, cancellationToken);
            foreach (var automation in bySheet)
            {
                var trigger = automation.Definition.Trigger;
                var fired0 = fired;
                if (trigger.Kind == AutomationTriggerKind.Schedule)
                {
                    if (!ScheduleDue(trigger, local))
                    {
                        continue;
                    }

                    var states = await FiredKeysAsync(automation.Id, $"schedule:{today:yyyyMMdd}:", cancellationToken);
                    foreach (var item in sheetItems.Where(i => !states.Contains(ScheduleKey(today, i.Id)) && SheetConditions.Holds(automation.Definition.Conditions, i, sheet.Columns, null)))
                    {
                        await RunAsync(items, sheet, automation, item, AutomationTriggerKind.Schedule, $"{AutomationText.Trigger(trigger)} ({today:yyyy-MM-dd})", ScheduleKey(today, item.Id));
                        fired++;
                    }
                }
                else
                {
                    var field = trigger.FieldKey!;
                    var states = await FiredKeysAsync(automation.Id, "date:", cancellationToken);
                    foreach (var item in sheetItems)
                    {
                        if (RuleLogic.ReadDate(item, field) is not { } date)
                        {
                            continue;
                        }

                        var fireOn = date.AddDays(trigger.OffsetDays ?? 0);
                        if (fireOn > today || today.DayNumber - fireOn.DayNumber > DateCatchUpDays
                            || (fireOn == today && trigger.Hour is { } hour && local.Hour < hour)
                            || states.Contains(DateKey(item.Id, date))
                            || !SheetConditions.Holds(automation.Definition.Conditions, item, sheet.Columns, null))
                        {
                            continue;
                        }

                        await RunAsync(items, sheet, automation, item, AutomationTriggerKind.DateField,
                            $"{RuleLogic.FieldName(field, sheet.Columns)} is {date:yyyy-MM-dd}", DateKey(item.Id, date));
                        fired++;
                    }
                }

                if (fired > fired0)
                {
                    _log.LogDebug("Sheet automation {AutomationId} fired {Count} times", automation.Id, fired - fired0);
                }
            }
        }

        return fired;
    }

    /// <summary>Tests a (possibly unsaved) sheet automation against one row. Nothing changes; the run is logged as a dry run when it's saved.</summary>
    public async Task<AutomationRunDto> DryRunAsync(Guid automationId, string name, AutomationDefinition definition, Guid sheetId, Guid itemId, string actor, CancellationToken ct)
    {
        var sheet = await LoadSheetAsync(sheetId, ct) ?? throw Contracts.WorkbenchNotFoundException.For("Sheet", sheetId);
        ItemDto item;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            item = (await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, ct))?.ToDto()
                ?? throw Contracts.WorkbenchNotFoundException.For("Item", itemId);
        }

        var today = Today();
        var t = definition.Trigger;
        var results = new List<AutomationConditionResultDto>();
        var field = RuleLogic.FieldName(t.FieldKey, sheet.Columns);
        switch (t.Kind)
        {
            case AutomationTriggerKind.FieldChanged:
                var current = RuleLogic.Read(item, t.FieldKey ?? "");
                results.Add(new($"{field} changes{(t.ToValue is null ? "" : $" to {Quote(t.ToValue)}")}", t.ToValue is null || RuleLogic.Same(current, t.ToValue), current));
                break;
            case AutomationTriggerKind.DateField:
                var date = RuleLogic.ReadDate(item, t.FieldKey ?? "");
                results.Add(new(date is { } d ? $"{field} is {d:yyyy-MM-dd}, so it fires on {d.AddDays(t.OffsetDays ?? 0):yyyy-MM-dd}" : $"{field} is empty, so it never fires",
                    date is not null, date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                break;
        }

        var matched = results.All(r => r.Matched) & SheetConditions.Holds(definition.Conditions, item, sheet.Columns, results);
        var steps = matched
            ? definition.Actions.Select((a, i) => new AutomationStepDto(i, a.Kind, AutomationStepOutcome.WouldDo, Plan(a, item, sheet, today))).ToList()
            : [];
        var now = clock.GetUtcNow().UtcDateTime;
        var run = new AutomationRun
        {
            AutomationId = automationId, AutomationName = AutomationExecutor.Clip(name, 200)!, TriggerKind = t.Kind.ToString(), TriggerDetail = "Dry run",
            EntityType = AutomationSheets.EntityType, RecordId = item.Id, RecordLabel = AutomationExecutor.Clip(item.Title, 300), Actor = AutomationExecutor.Clip(actor, 320),
            Principal = AutomationExecutor.Clip(actor, 320)!, IsDryRun = true, Status = nameof(AutomationRunStatus.DryRun), StartedAtUtc = now, FinishedAtUtc = now,
            StepsJson = AutomationJson.Serialize(steps), ConditionsJson = AutomationJson.Serialize(results),
            InputsJson = AutomationJson.Serialize(new Dictionary<string, string?> { ["sheet"] = sheet.Name, ["row"] = item.Title }),
        };
        if (automationId != Guid.Empty)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            run.Version = await db.Set<AutomationRecord>().Where(a => a.Id == automationId).Select(a => a.CurrentVersion).FirstOrDefaultAsync(ct);
            db.Set<AutomationRun>().Add(run);
            await db.SaveChangesAsync(ct);
        }

        return AutomationMapping.ToDto(run);
    }

    private string Plan(AutomationAction a, ItemDto item, SheetContext sheet, DateOnly today)
    {
        string R(string? template) => RuleLogic.Render(template, item, sheet.Columns, today);
        string People() => string.Join(", ", Recipients(a, item).Select(Notifier.Normalize).OfType<string>().Distinct().DefaultIfEmpty("no one"));
        return a.Kind switch
        {
            AutomationActionKind.Notify => $"Would notify {People()}",
            AutomationActionKind.SendEmail => $"Would email {People()}: {Quote(R(a.Title))}",
            AutomationActionKind.CreateTask => $"Would give {People()} the to-do {Quote(R(a.Title))}",
            AutomationActionKind.SetField => $"Would set {RuleLogic.FieldName(a.FieldKey, sheet.Columns)} to {Quote(R(a.Value))}",
            AutomationActionKind.AssignPerson => $"Would assign {RuleLogic.FieldName(a.FieldKey, sheet.Columns)} to {R(a.Value)}",
            AutomationActionKind.CreateSubItem => $"Would add the sub-item {Quote(R(a.Value))}",
            AutomationActionKind.CreateItem => $"Would add {Quote(R(a.Value))} to {(a.TargetSheetId is null || a.TargetSheetId == sheet.Id ? "this sheet" : "another sheet")}",
            AutomationActionKind.MoveRow => "Would move the row (and its sub-items) to another sheet",
            AutomationActionKind.CopyRow => "Would copy the row (and its sub-items) to another sheet",
            AutomationActionKind.RequireApproval => $"Would send it for approval ({a.WorkflowKey})",
            _ => $"Would {a.Kind}",
        };
    }

    private async Task EvaluateAsync(IItemService items, Guid sheetId, ItemDto item, Func<SheetAutomation, SheetContext, string?> triggered)
    {
        try
        {
            if (await PausedAsync(CancellationToken.None))
            {
                return;
            }

            var sheet = await LoadSheetAsync(sheetId, CancellationToken.None);
            if (sheet is null)
            {
                return;
            }

            foreach (var automation in sheet.Automations.Where(a => a.Definition.Trigger.Kind is AutomationTriggerKind.RecordEvent or AutomationTriggerKind.FieldChanged))
            {
                if (triggered(automation, sheet) is { } detail && SheetConditions.Holds(automation.Definition.Conditions, item, sheet.Columns, null))
                {
                    await RunAsync(items, sheet, automation, item, automation.Definition.Trigger.Kind, detail, null);
                }
            }
        }
        catch (Exception ex)
        {
            // Automations must never fail the change a person made.
            _log.LogError(ex, "Evaluating sheet automations for item {ItemId} on sheet {SheetId} failed.", item.Id, sheetId);
        }
    }

    private async Task RunAsync(IItemService items, SheetContext sheet, SheetAutomation automation, ItemDto item, AutomationTriggerKind kind, string detail, string? firedKey)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var run = new AutomationRun
        {
            AutomationId = automation.Id,
            Version = automation.Version,
            AutomationName = AutomationExecutor.Clip(automation.Name, 200)!,
            TriggerKind = kind.ToString(),
            TriggerDetail = AutomationExecutor.Clip(detail, 200),
            EntityType = AutomationSheets.EntityType,
            RecordId = item.Id,
            RecordLabel = AutomationExecutor.Clip(item.Title, 300),
            Actor = AutomationExecutor.Clip(Person(changeActor.Value), 320),
            Principal = AutomationExecutor.Clip(automation.Author, 320)!,
            Depth = AutomationContext.Depth,
            Status = nameof(AutomationRunStatus.Succeeded),
            StartedAtUtc = now,
        };
        var steps = new List<AutomationStepDto>();
        var limit = await ChainLimitAsync();
        if (AutomationContext.Depth >= limit.Depth)
        {
            run.Status = nameof(AutomationRunStatus.Skipped);
            run.Error = $"Loop protection: automations triggering automations can only go {limit.Depth} levels deep.";
        }
        else if (await RunsThisHourAsync(automation.Id, item.Id) >= limit.PerHour)
        {
            run.Status = nameof(AutomationRunStatus.Skipped);
            run.Error = $"Loop protection: it already ran {limit.PerHour} times on this row in the last hour.";
        }
        else
        {
            var actions = automation.Definition.Actions;
            try
            {
                using (AutomationContext.Enter(automation.Name, automation.Author))
                {
                    for (var index = 0; index < actions.Count; index++)
                    {
                        var action = actions[index];
                        steps.Add(new AutomationStepDto(index, action.Kind, AutomationStepOutcome.Done, await ExecuteAsync(items, sheet, automation, action, item, run.Id)));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sheet automation {AutomationId} failed on item {ItemId}.", automation.Id, item.Id);
                run.Status = nameof(AutomationRunStatus.Failed);
                run.Error = AutomationExecutor.Clip(ex.Message, 2000);
                steps.Add(new AutomationStepDto(steps.Count, actions[steps.Count].Kind,
                    ex is UnauthorizedAccessException or Contracts.WorkbenchForbiddenException ? AutomationStepOutcome.Denied : AutomationStepOutcome.Failed,
                    AutomationExecutor.Clip(ex.Message, 1000)!));
            }
        }

        run.StepsJson = AutomationJson.Serialize(steps);
        run.InputsJson = AutomationJson.Serialize(new Dictionary<string, string?> { ["sheet"] = sheet.Name, ["row"] = item.Title, ["trigger"] = detail });
        run.ConditionsJson = "[]";
        run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        db.Set<AutomationRun>().Add(run);
        if (firedKey is not null)
        {
            db.Set<AutomationTriggerState>().Add(new AutomationTriggerState { AutomationId = automation.Id, Key = firedKey, FiredAtUtc = now });
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The person whose change is being handled (outside automation chains), for the run log.</summary>
    private static readonly AsyncLocal<string?> changeActor = new();

    /// <summary>
    /// Records who made the change the following automations react to (the run log's "actor", an approval's submitter). Inside an
    /// automation's actions the person whose change started the chain stays the actor.
    /// </summary>
    internal static IDisposable ForActor(string? actor)
    {
        if (AutomationContext.IsActive)
        {
            return new Restore(() => { });
        }

        var previous = changeActor.Value;
        changeActor.Value = actor;
        return new Restore(() => changeActor.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static string? Person(string? email) => email is not null && email.Contains('@') ? email.Trim().ToLowerInvariant() : null;

    private async Task<string> ExecuteAsync(IItemService items, SheetContext sheet, SheetAutomation automation, AutomationAction action, ItemDto snapshot, Guid runId)
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
        string R(string? template) => RuleLogic.Render(template, current, sheet.Columns, today);

        switch (action.Kind)
        {
            case AutomationActionKind.Notify:
            {
                var title = action.Title is null ? $"{automation.Name}: {current.Title}" : R(action.Title);
                var body = action.Message is null ? $"Automation \"{automation.Name}\" ran on \"{current.Title}\" in {sheet.Name}." : R(action.Message);
                var request = new NotificationRequest(NotificationKinds.Rule, title, body, sheet.Id, current.Id);
                var recipients = Recipients(action, current).ToList();
                if (SheetAutomationBatch.Current is { } batch)
                {
                    var queued = batch.Notify(automation.Id, automation.Name, sheet.Id, sheet.Name, recipients, request, current.Title);
                    return queued.Count == 0 ? "No one to notify" : $"Will notify {string.Join(", ", queued)} (one summary for the batch)";
                }

                var notified = await notifier.NotifyAsync(recipients, request);
                return notified.Count == 0 ? "No one to notify" : $"Notified {string.Join(", ", notified)}";
            }

            case AutomationActionKind.SendEmail:
            {
                var to = Recipients(action, current).Select(Notifier.Normalize).OfType<string>().Distinct().ToList();
                if (to.Count == 0)
                {
                    return "No one to email";
                }

                var subject = action.Title is null ? $"{automation.Name}: {current.Title}" : R(action.Title);
                var text = (action.Message is null ? $"Automation \"{automation.Name}\" ran on \"{current.Title}\" in {sheet.Name}." : R(action.Message))
                    + $"\n\nOpen it in Workbench: sheets/{sheet.Id}?item={current.Id}";
                if (SheetAutomationBatch.Current is { } batch)
                {
                    batch.Email(automation.Id, automation.Name, sheet.Name, to, subject, text, current.Title);
                    return $"Will email {string.Join(", ", to)} (one summary for the batch)";
                }

                foreach (var address in to)
                {
                    await email.SendAsync(new EmailMessage(address, subject, text, System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>")));
                }

                return $"Emailed {string.Join(", ", to)}";
            }

            case AutomationActionKind.CreateTask:
            {
                var to = Recipients(action, current).Select(Notifier.Normalize).OfType<string>().Distinct().ToList();
                if (to.Count == 0)
                {
                    return "No one to give the to-do to";
                }

                var title = action.Title is null ? current.Title : R(action.Title);
                var due = action.DueInDays is { } days ? today.AddDays(days) : (DateOnly?)null;
                await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
                {
                    foreach (var address in to)
                    {
                        db.Set<AutomationTask>().Add(new AutomationTask
                        {
                            AutomationId = automation.Id, RunId = runId, AssigneeEmail = address, Title = AutomationExecutor.Clip(title, 300)!,
                            Notes = AutomationExecutor.Clip(action.Message is null ? null : R(action.Message), 2000), DueOn = due, EntityType = AutomationSheets.EntityType,
                            RecordId = current.Id, Link = $"sheets/{sheet.Id}?item={current.Id}", CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                        });
                    }

                    await db.SaveChangesAsync(CancellationToken.None);
                }

                return $"Gave {string.Join(", ", to)} the to-do \"{title}\"";
            }

            case AutomationActionKind.SetField:
            {
                var value = R(action.Value);
                await items.UpdateAsync(sheet.Id, current.Id, WithField(stored, action.FieldKey!, value, sheet));
                var name = RuleLogic.FieldName(action.FieldKey, sheet.Columns);
                return RuleLogic.IsBlank(value) ? $"Cleared {name}" : $"Set {name} to \"{value}\"";
            }

            case AutomationActionKind.AssignPerson:
            {
                var address = Notifier.Normalize(R(action.Value)) ?? throw new ArgumentException("The person to assign isn't a valid email.");
                await items.UpdateAsync(sheet.Id, current.Id, WithField(stored, action.FieldKey!, address, sheet));
                return $"Assigned {RuleLogic.FieldName(action.FieldKey, sheet.Columns)} to {address}";
            }

            case AutomationActionKind.CreateSubItem:
            {
                var title = R(action.Value).Trim();
                if (title.Length == 0)
                {
                    throw new ArgumentException("The subitem title came out empty.");
                }

                await items.CreateAsync(sheet.Id, new CreateItemRequest(title, ParentId: current.Id));
                return $"Added subitem \"{title}\"";
            }

            case AutomationActionKind.CreateItem:
            {
                var title = R(action.Value).Trim();
                if (title.Length == 0)
                {
                    throw new ArgumentException("The new item's title came out empty.");
                }

                (Guid Id, string Name, List<Column> Columns)? target = action.TargetSheetId is { } other && other != sheet.Id ? await TargetAsync(automation, other) : null;
                await items.CreateAsync(target?.Id ?? sheet.Id, new CreateItemRequest(title));
                return target is null ? $"Added \"{title}\"" : $"Added \"{title}\" to {target.Value.Name}";
            }

            case AutomationActionKind.CopyRow or AutomationActionKind.MoveRow:
            {
                var target = await TargetAsync(automation, action.TargetSheetId ?? throw new ArgumentException("Choose the sheet to send the row to."));
                if (target.Id == sheet.Id)
                {
                    throw new ArgumentException("Choose another sheet: the row is already on this one.");
                }

                var copied = await CopyTreeAsync(items, sheet, target, stored);
                if (action.Kind == AutomationActionKind.MoveRow)
                {
                    await items.DeleteAsync(sheet.Id, current.Id);
                    return $"Moved it{(copied > 1 ? $" and {copied - 1} sub-item(s)" : "")} to {target.Name}";
                }

                return $"Copied it{(copied > 1 ? $" and {copied - 1} sub-item(s)" : "")} to {target.Name}";
            }

            case AutomationActionKind.RequireApproval:
            {
                var key = action.WorkflowKey ?? throw new ArgumentException("Choose the approval workflow.");
                await using var db = await dbFactory.CreateAsync(CancellationToken.None);
                if (!Contracts.Approvals.ApprovalDocumentTypes.IsCustom(key) || !await db.Set<Domain.Approvals.ApprovalWorkflow>().AnyAsync(w => w.DocumentType == key))
                {
                    throw new ArgumentException($"\"{key}\" isn't a record approval workflow (Setup › Approval workflows).");
                }

                var link = $"sheets/{sheet.Id}?item={current.Id}";
                var subject = new ApprovalSubject(key, current.Id, AutomationExecutor.Clip($"{sheet.Name}: {current.Title}", 300)!, Person(changeActor.Value) ?? automation.Author, link,
                    Summary: $"Sent for approval by the automation \"{automation.Name}\"");
                var route = await approvals.StartAsync(db, subject, CancellationToken.None);
                db.Set<AutomationApprovalRequest>().Add(new AutomationApprovalRequest
                {
                    ApprovalInstanceId = route.Instance.Id, DocumentType = key, EntityType = AutomationSheets.EntityType, RecordId = current.Id,
                    AutomationId = automation.Id, RunId = runId, CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                });
                await db.SaveChangesAsync(CancellationToken.None);
                await notifier.NotifyAsync(route.NotifyEmails, new NotificationRequest("approval.requested", $"Approval needed: {subject.Title}", subject.Summary!, null, null, "approvals"));
                return $"Sent it for approval; waiting on {(route.NotifyEmails.Count == 0 ? "an administrator" : string.Join(", ", route.NotifyEmails))}";
            }

            default:
                throw new InvalidOperationException($"Sheet automations can't {action.Kind}.");
        }
    }

    /// <summary>Recipients of notify, email and to-do actions: fixed emails and the people in Person columns.</summary>
    private static IEnumerable<string> Recipients(AutomationAction action, ItemDto item) =>
        (action.Recipients ?? []).SelectMany(r => r.Kind switch
        {
            RecipientKind.Person => [r.Value],
            RecipientKind.Field => RuleLogic.People(RuleLogic.Read(item, r.Value)),
            _ => [],
        });

    /// <summary>Another sheet an action writes to: it must exist and the automation's author must be able to edit it.</summary>
    private async Task<(Guid Id, string Name, List<Column> Columns)> TargetAsync(SheetAutomation automation, Guid sheetId) =>
        await TargetOrNullAsync(automation, sheetId) is { } target ? target : throw new InvalidOperationException("The sheet to send it to no longer exists.");

    private async Task<(Guid Id, string Name, List<Column> Columns)?> TargetOrNullAsync(SheetAutomation automation, Guid sheetId)
    {
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        var sheet = await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => new { s.Id, s.Name, s.WorkspaceId }).FirstOrDefaultAsync();
        if (sheet is null)
        {
            return null;
        }

        if (await RoleOfAsync(db, automation.Author, sheet.WorkspaceId) is not (WorkspaceRole.Owner or WorkspaceRole.Editor))
        {
            throw new Contracts.WorkbenchForbiddenException($"The automation's author, {automation.Author}, can't edit \"{sheet.Name}\", so the automation can't either.");
        }

        return (sheet.Id, sheet.Name, await db.Columns.AsNoTracking().Where(c => c.SheetId == sheet.Id).ToListAsync());
    }

    /// <summary>A person's role in a workspace (Owner when they see every workspace), or null; deactivated people have none.</summary>
    internal static async Task<WorkspaceRole?> RoleOfAsync(IWorkbenchDbContext db, string email, Guid workspaceId)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.Email == normalized && !u.IsActive))
        {
            return null;
        }

        if ((await Identity.RoleStore.ResolveAsync(db, normalized, CancellationToken.None)).Permissions.Has(Contracts.Identity.Permission.AdminAllWorkspaces))
        {
            return WorkspaceRole.Owner;
        }

        return await db.Set<WorkspaceMember>().AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.UserEmail == normalized)
            .Select(m => (WorkspaceRole?)m.Role).FirstOrDefaultAsync();
    }

    /// <summary>Copies an item and its sub-items to another sheet, carrying values into columns of the same name and kind. Returns how many were copied.</summary>
    private async Task<int> CopyTreeAsync(IItemService items, SheetContext from, (Guid Id, string Name, List<Column> Columns) to, Item root)
    {
        List<Item> all;
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            all = await db.Items.AsNoTracking().Where(i => i.SheetId == from.Id).ToListAsync();
        }

        var map = new Dictionary<string, string>();
        foreach (var column in from.Columns.Where(c => c.Formula is null))
        {
            if (to.Columns.FirstOrDefault(c => c.Formula is null && c.Type == column.Type && c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                map[column.Id.ToString()] = match.Id.ToString();
            }
        }

        var count = 0;
        async Task CopyAsync(Item item, Guid? parentId)
        {
            var values = item.Values.Where(v => map.ContainsKey(v.Key) && v.Value is not null).ToDictionary(v => map[v.Key], v => v.Value);
            var copy = await items.CreateAsync(to.Id, new CreateItemRequest(item.Title, parentId, Start: item.Start, Finish: item.Finish, Values: values, IsMilestone: item.IsMilestone));
            count++;
            foreach (var child in all.Where(i => i.ParentId == item.Id).OrderBy(i => i.Order))
            {
                await CopyAsync(child, copy.Id);
            }
        }

        await CopyAsync(root, null);
        return count;
    }

    private static UpdateItemRequest WithField(Item item, string field, string value, SheetContext sheet)
    {
        var blank = RuleLogic.IsBlank(value);
        var request = new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId, new Dictionary<string, string?>(item.Values));
        switch (field)
        {
            case AutomationSheets.Title:
                return blank ? throw new ArgumentException("Title can't be empty.") : request with { Title = value.Trim() };
            case AutomationSheets.Start:
                return request with { Start = blank ? null : ParseDate(value) };
            case AutomationSheets.Finish:
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

    private static bool ScheduleDue(AutomationTrigger t, DateTime local) =>
        local.Hour >= (t.Hour ?? 7) && t.Frequency switch
        {
            ScheduleFrequency.Weekly => local.DayOfWeek == (t.DayOfWeek ?? DayOfWeek.Monday),
            ScheduleFrequency.Monthly => local.Day == Math.Min(t.DayOfMonth ?? 1, DateTime.DaysInMonth(local.Year, local.Month)),
            _ => true,
        };

    internal static string DateKey(Guid itemId, DateOnly date) => $"date:{itemId:N}:{date:yyyyMMdd}";

    private static string ScheduleKey(DateOnly day, Guid itemId) => $"schedule:{day:yyyyMMdd}:{itemId:N}";

    private async Task<HashSet<string>> FiredKeysAsync(Guid automationId, string prefix, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return (await db.Set<AutomationTriggerState>().AsNoTracking().Where(s => s.AutomationId == automationId && s.Key.StartsWith(prefix)).Select(s => s.Key).ToListAsync(ct))
            .ToHashSet();
    }

    private async Task<bool> PausedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<AutomationSettings>().AsNoTracking().AnyAsync(s => s.PausedAll, ct);
    }

    private async Task<(int Depth, int PerHour)> ChainLimitAsync()
    {
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        var settings = await db.Set<AutomationSettings>().AsNoTracking().FirstOrDefaultAsync() ?? new AutomationSettings();
        return (Math.Min(AutomationSheets.MaxChainDepth, settings.MaxChainDepth), settings.MaxRunsPerRecordPerHour);
    }

    private async Task<int> RunsThisHourAsync(Guid automationId, Guid itemId)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        var skipped = nameof(AutomationRunStatus.Skipped);
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        return await db.Set<AutomationRun>().AsNoTracking()
            .CountAsync(r => r.AutomationId == automationId && r.RecordId == itemId && !r.IsDryRun && r.Status != skipped && r.StartedAtUtc >= since);
    }

    /// <summary>Logs why a timed automation didn't run and records it as a skipped run, at most once per automation and day.</summary>
    private async Task RecordSkippedAsync(SheetAutomation automation, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.Date;
        var skipped = nameof(AutomationRunStatus.Skipped);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<AutomationRun>().AnyAsync(r => r.AutomationId == automation.Id && r.RecordId == null && r.Status == skipped && r.StartedAtUtc >= since, cancellationToken))
        {
            _log.LogDebug("Sheet automation {AutomationId} is still skipped: {Reason}", automation.Id, reason);
            return;
        }

        _log.LogWarning("Skipped sheet automation {AutomationId} (\"{Name}\") on sheet {SheetId}: {Reason}", automation.Id, automation.Name, automation.SheetId, reason);
        db.Set<AutomationRun>().Add(new AutomationRun
        {
            AutomationId = automation.Id, Version = automation.Version, AutomationName = AutomationExecutor.Clip(automation.Name, 200)!,
            TriggerKind = automation.Definition.Trigger.Kind.ToString(), EntityType = AutomationSheets.EntityType, Principal = AutomationExecutor.Clip(automation.Author, 320)!,
            Status = skipped, Error = AutomationExecutor.Clip(reason, 2000), StartedAtUtc = now, FinishedAtUtc = now, StepsJson = "[]", ConditionsJson = "[]",
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<SheetAutomation>> LoadTimedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var kinds = new[] { nameof(AutomationTriggerKind.DateField), nameof(AutomationTriggerKind.Schedule) };
        var rows = await db.Set<AutomationRecord>().AsNoTracking()
            .Where(a => a.SheetId != null && a.IsEnabled && a.DeletedAtUtc == null && kinds.Contains(a.TriggerKind))
            .OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
        return (await DefinitionsAsync(db, rows, ct))
            .Where(a => a.Definition.Trigger.Kind == AutomationTriggerKind.Schedule || a.Definition.Trigger.FieldKey is not null)
            .ToList();
    }

    private static async Task<List<SheetAutomation>> DefinitionsAsync(IWorkbenchDbContext db, List<AutomationRecord> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.CurrentVersionId).ToList();
        var versions = await db.Set<AutomationVersion>().AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.DefinitionJson, ct);
        return rows.Where(r => r.CurrentVersionId is { } v && versions.ContainsKey(v))
            .Select(r => new SheetAutomation(r.Id, r.SheetId!.Value, r.Name, (r.UpdatedBy ?? r.CreatedBy).Trim().ToLowerInvariant(), r.CurrentVersion,
                AutomationJson.Definition(versions[r.CurrentVersionId!.Value])))
            .ToList();
    }

    private async Task<SheetContext?> LoadSheetAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var name = await db.Sheets.Where(s => s.Id == sheetId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken);
        if (name is null)
        {
            return null;
        }

        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var rows = await db.Set<AutomationRecord>().AsNoTracking().Where(a => a.SheetId == sheetId && a.IsEnabled && a.DeletedAtUtc == null)
            .OrderBy(a => a.CreatedAtUtc).ToListAsync(cancellationToken);
        return new SheetContext(sheetId, name, columns, await DefinitionsAsync(db, rows, cancellationToken),
            columns.Where(c => c.Formula is not null).Select(c => c.Id.ToString()).ToHashSet());
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private static string Quote(string? value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : $"\"{value}\"";

    internal sealed record SheetAutomation(Guid Id, Guid SheetId, string Name, string Author, int Version, AutomationDefinition Definition);

    private sealed record SheetContext(Guid Id, string Name, IReadOnlyList<Column> Columns, IReadOnlyList<SheetAutomation> Automations, HashSet<string> FormulaColumnIds);
}

/// <summary>
/// Whose timed automations a run fires, and the run-time check that the author may still change a sheet: returns why not
/// (deactivated, no edit access), or null when they may run.
/// </summary>
public sealed record DateRuleAuthor(string Email, Func<Guid, CancellationToken, Task<string?>> WhyNotAsync);

/// <summary>Sheet conditions: a row's values compared as the sheet rules always did (trimmed, ignoring case; numbers and dates by value).</summary>
public static class SheetConditions
{
    public static bool Holds(AutomationConditionGroup? group, ItemDto item, IReadOnlyList<Column> columns, List<AutomationConditionResultDto>? results)
    {
        if (group is null || group.IsEmpty)
        {
            return true;
        }

        var outcomes = new List<bool>();
        foreach (var c in group.Conditions ?? [])
        {
            var actual = RuleLogic.Read(item, c.FieldKey ?? "");
            var holds = Compare(c.Operator, actual, c.Value, c.Values);
            results?.Add(new AutomationConditionResultDto(
                $"{RuleLogic.FieldName(c.FieldKey, columns)} {AutomationConditions.Describe(c with { FieldKey = "" }).Trim()}", holds, actual));
            outcomes.Add(holds);
        }

        foreach (var child in (group.Groups ?? []).Where(g => !g.IsEmpty))
        {
            outcomes.Add(Holds(child, item, columns, results));
        }

        return outcomes.Count == 0 || (group.Logic == GroupLogic.Any ? outcomes.Any(x => x) : outcomes.All(x => x));
    }

    public static bool Compare(ConditionOperator op, string? actual, string? expected, IReadOnlyList<string>? values = null) => op switch
    {
        ConditionOperator.Equals => RuleLogic.Same(actual, expected) || AutomationConditions.Compare(op, actual?.Trim(), expected?.Trim()),
        ConditionOperator.NotEquals => !(RuleLogic.Same(actual, expected) || AutomationConditions.Compare(ConditionOperator.Equals, actual?.Trim(), expected?.Trim())),
        ConditionOperator.IsEmpty => RuleLogic.IsBlank(actual),
        ConditionOperator.IsNotEmpty => !RuleLogic.IsBlank(actual),
        ConditionOperator.Contains => !RuleLogic.IsBlank(expected) && (actual ?? "").Contains(expected!.Trim(), StringComparison.OrdinalIgnoreCase),
        ConditionOperator.NotContains => RuleLogic.IsBlank(expected) || !(actual ?? "").Contains(expected!.Trim(), StringComparison.OrdinalIgnoreCase),
        ConditionOperator.In or ConditionOperator.NotIn =>
            (values ?? (expected ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Any(v => RuleLogic.Same(actual, v)) == (op == ConditionOperator.In),
        _ => AutomationConditions.Compare(op, actual?.Trim(), expected?.Trim(), values),
    };
}
