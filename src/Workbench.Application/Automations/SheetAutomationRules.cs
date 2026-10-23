using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automation;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Domain;
using Workbench.Domain.Approvals;
using Workbench.Domain.Identity;

namespace Workbench.Application.Automations;

/// <summary>
/// What a sheet automation may be: triggers on rows (added, added by a form, a column changes, a date arrives, a recurring
/// schedule), conditions on the row's fields, and sheet actions (set a column, assign, add a sub-item or an item, move or copy
/// the row to another sheet, notify, email, a to-do, request an approval). It always acts as its author.
/// </summary>
public static class SheetAutomationRules
{
    public static readonly IReadOnlyList<AutomationActionKind> Actions =
    [
        AutomationActionKind.SetField, AutomationActionKind.AssignPerson, AutomationActionKind.Notify, AutomationActionKind.SendEmail,
        AutomationActionKind.CreateSubItem, AutomationActionKind.CreateItem, AutomationActionKind.MoveRow, AutomationActionKind.CopyRow,
        AutomationActionKind.RequireApproval, AutomationActionKind.CreateTask,
    ];

    public static readonly IReadOnlyList<AutomationTriggerKind> Triggers =
        [AutomationTriggerKind.RecordEvent, AutomationTriggerKind.FieldChanged, AutomationTriggerKind.DateField, AutomationTriggerKind.Schedule];

    /// <summary>Checks a sheet automation against its sheet and the saver's access; returns it with text trimmed and emails normalized.</summary>
    public static async Task<AutomationDefinition> ValidateAsync(IWorkbenchDbContext db, AutomationDefinition d, string saver, CancellationToken ct)
    {
        var sheetId = d.Trigger.SheetId!.Value;
        var sheet = await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => new { s.Id, s.WorkspaceId }).FirstOrDefaultAsync(ct)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(ct);
        var t = d.Trigger;
        AutomationTrigger trigger;
        switch (t.Kind)
        {
            case AutomationTriggerKind.RecordEvent:
                var events = (t.Events ?? []).Distinct().ToList();
                if (events.Count == 0 || events.Any(e => e is not (RecordEventKind.Created or RecordEventKind.AddedByForm)))
                {
                    throw new ArgumentException("Choose when a row is added, or added by a form.");
                }

                trigger = new AutomationTrigger(t.Kind, Events: events);
                break;
            case AutomationTriggerKind.FieldChanged:
                RequireField(t.FieldKey, columns, "The trigger");
                trigger = new AutomationTrigger(t.Kind, FieldKey: t.FieldKey!.Trim(), FromValue: Clean(t.FromValue), ToValue: Clean(t.ToValue));
                break;
            case AutomationTriggerKind.DateField:
                if (t.FieldKey is not (AutomationSheets.Start or AutomationSheets.Finish) && !columns.Any(c => c.Id.ToString() == t.FieldKey && c.Type == ColumnType.Date))
                {
                    throw new ArgumentException("A date trigger needs Start, Finish or a Date column.");
                }

                if (Math.Abs(t.OffsetDays ?? 0) > 365)
                {
                    throw new ArgumentException("The date offset can be at most 365 days.");
                }

                if (t.Hour is < 0 or > 23)
                {
                    throw new ArgumentException("The hour must be 0–23.");
                }

                trigger = new AutomationTrigger(t.Kind, FieldKey: t.FieldKey, OffsetDays: t.OffsetDays ?? 0, Hour: t.Hour);
                break;
            case AutomationTriggerKind.Schedule:
                if (t.Hour is < 0 or > 23 || t.DayOfMonth is < 1 or > 31)
                {
                    throw new ArgumentException("The hour must be 0–23 and the day of the month 1–31.");
                }

                var frequency = t.Frequency ?? ScheduleFrequency.Daily;
                trigger = new AutomationTrigger(t.Kind, Frequency: frequency, Hour: t.Hour ?? 7,
                    DayOfWeek: frequency == ScheduleFrequency.Weekly ? t.DayOfWeek ?? DayOfWeek.Monday : null,
                    DayOfMonth: frequency == ScheduleFrequency.Monthly ? t.DayOfMonth ?? 1 : null);
                break;
            default:
                throw new ArgumentException("Sheet automations run when a row is added (or added by a form), a column changes, a date arrives, or on a schedule.");
        }

        trigger = trigger with { EntityType = AutomationSheets.EntityType, SheetId = sheetId };
        ValidateGroup(d.Conditions, columns, 0);

        if (d.Actions is not { Count: > 0 } actions)
        {
            throw new ArgumentException("Add at least one action.");
        }

        if (actions.Count > 20)
        {
            throw new ArgumentException("An automation can have at most 20 actions.");
        }

        var cleaned = new List<AutomationAction>();
        foreach (var action in actions)
        {
            cleaned.Add(await ValidateActionAsync(db, action, sheetId, columns, saver, ct));
        }

        if (d.RunAs != AutomationRunAs.Actor)
        {
            throw new ArgumentException("Sheet automations act as their author; they can't use a service user.");
        }

        return new AutomationDefinition(trigger, d.Conditions is { IsEmpty: false } ? d.Conditions : null, cleaned);
    }

    private static void ValidateGroup(AutomationConditionGroup? group, IReadOnlyList<Column> columns, int depth)
    {
        if (group is null)
        {
            return;
        }

        if (depth > 4)
        {
            throw new ArgumentException("Condition groups can be nested at most four deep.");
        }

        foreach (var c in group.Conditions ?? [])
        {
            if (c.Subject != ConditionSubject.Field)
            {
                throw new ArgumentException("Sheet conditions compare the row's columns.");
            }

            RequireField(c.FieldKey, columns, "A condition");
            if (!Enum.IsDefined(c.Operator))
            {
                throw new ArgumentException("A condition has an unknown operator.");
            }
        }

        foreach (var child in group.Groups ?? [])
        {
            ValidateGroup(child, columns, depth + 1);
        }
    }

    private static async Task<AutomationAction> ValidateActionAsync(IWorkbenchDbContext db, AutomationAction a, Guid sheetId, IReadOnlyList<Column> columns, string saver, CancellationToken ct)
    {
        if (a.Title?.Length > 300 || a.Message?.Length > 2000 || a.Value?.Length > 2000)
        {
            throw new ArgumentException("Titles can be at most 300 characters and messages 2000.");
        }

        switch (a.Kind)
        {
            case AutomationActionKind.Notify or AutomationActionKind.SendEmail or AutomationActionKind.CreateTask:
                var recipients = new List<AutomationRecipient>();
                foreach (var r in a.Recipients ?? [])
                {
                    switch (r.Kind)
                    {
                        case RecipientKind.Person:
                            var address = r.Value?.Trim().ToLowerInvariant();
                            if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address)
                            {
                                throw new ArgumentException($"\"{r.Value}\" isn't a valid email.");
                            }

                            recipients.Add(new AutomationRecipient(RecipientKind.Person, address));
                            break;
                        case RecipientKind.Field:
                            RequirePerson(r.Value, columns);
                            recipients.Add(new AutomationRecipient(RecipientKind.Field, r.Value.Trim()));
                            break;
                        default:
                            throw new ArgumentException("On sheets, choose people by email or by a Person column.");
                    }
                }

                recipients = recipients.Distinct().ToList();
                if (recipients.Count == 0)
                {
                    throw new ArgumentException("Choose who to notify.");
                }

                if (a.DueInDays is < 0 or > 365)
                {
                    throw new ArgumentException("A to-do can be due at most a year out.");
                }

                return new AutomationAction(a.Kind, recipients, Clean(a.Title), Clean(a.Message), DueInDays: a.Kind == AutomationActionKind.CreateTask ? a.DueInDays : null);

            case AutomationActionKind.SetField:
                RequireField(a.FieldKey, columns, "The set-value action");
                if (columns.Any(c => c.Id.ToString() == a.FieldKey && c.Formula is not null))
                {
                    throw new ArgumentException("A formula column can't be set by an automation.");
                }

                return new AutomationAction(a.Kind, FieldKey: a.FieldKey!.Trim(), Value: a.Value);

            case AutomationActionKind.AssignPerson:
                RequirePerson(a.FieldKey, columns);
                if (string.IsNullOrWhiteSpace(a.Value) || (!a.Value.Contains('{') && Notifications.Notifier.Normalize(a.Value) is null))
                {
                    throw new ArgumentException("Enter the email of the person to assign.");
                }

                return new AutomationAction(a.Kind, FieldKey: a.FieldKey!.Trim(), Value: a.Value.Trim());

            case AutomationActionKind.CreateSubItem:
                if (string.IsNullOrWhiteSpace(a.Value))
                {
                    throw new ArgumentException("Enter a title for the subitem.");
                }

                return new AutomationAction(a.Kind, Value: a.Value);

            case AutomationActionKind.CreateItem:
                if (string.IsNullOrWhiteSpace(a.Value))
                {
                    throw new ArgumentException("Enter a title for the new item.");
                }

                if (a.TargetSheetId is { } other && other != sheetId)
                {
                    await RequireEditableAsync(db, other, saver, ct);
                }

                return new AutomationAction(a.Kind, Value: a.Value, TargetSheetId: a.TargetSheetId == sheetId ? null : a.TargetSheetId);

            case AutomationActionKind.MoveRow or AutomationActionKind.CopyRow:
                if (a.TargetSheetId is not { } target || target == sheetId)
                {
                    throw new ArgumentException("Choose another sheet to send the row to.");
                }

                await RequireEditableAsync(db, target, saver, ct);
                return new AutomationAction(a.Kind, TargetSheetId: target);

            case AutomationActionKind.RequireApproval:
                if (a.WorkflowKey is not { } key || !ApprovalDocumentTypes.IsCustom(key) || !await db.Set<ApprovalWorkflow>().AsNoTracking().AnyAsync(w => w.DocumentType == key, ct))
                {
                    throw new ArgumentException("Choose a record approval workflow (create one in Setup › Approval workflows).");
                }

                if (a.FieldKey is not null)
                {
                    RequireField(a.FieldKey, columns, "The approval's decision column");
                }

                return new AutomationAction(a.Kind, WorkflowKey: key, FieldKey: Clean(a.FieldKey));

            default:
                throw new ArgumentException($"Sheet automations can't use the \"{a.Kind}\" action.");
        }
    }

    private static async Task RequireEditableAsync(IWorkbenchDbContext db, Guid sheetId, string saver, CancellationToken ct)
    {
        var workspaceId = await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => (Guid?)s.WorkspaceId).FirstOrDefaultAsync(ct)
            ?? throw new ArgumentException("The sheet to send rows to doesn't exist.");
        if (await SheetAutomationRuntime.RoleOfAsync(db, saver, workspaceId) is not (WorkspaceRole.Owner or WorkspaceRole.Editor))
        {
            throw new WorkbenchForbiddenException("You can only send rows to sheets you can edit.");
        }
    }

    private static void RequireField(string? field, IReadOnlyList<Column> columns, string what)
    {
        if (AutomationSheets.IsBuiltInField(field) || columns.Any(c => c.Id.ToString() == field))
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The trigger in a sentence, with column names: "When Status changes to Done".</summary>
    public static string Describe(AutomationTrigger t, IReadOnlyList<Column> columns)
    {
        var field = RuleLogic.FieldName(t.FieldKey, columns);
        return t.Kind switch
        {
            AutomationTriggerKind.RecordEvent => (t.Events ?? []).Contains(RecordEventKind.AddedByForm)
                ? (t.Events!.Contains(RecordEventKind.Created) ? "When an item is added (by anyone or a form)" : "When an item is added by a form")
                : "When an item is added",
            AutomationTriggerKind.FieldChanged => $"When {field} changes{(t.FromValue is null ? "" : $" from {t.FromValue}")}{(t.ToValue is null ? "" : $" to {t.ToValue}")}",
            AutomationTriggerKind.DateField => t.OffsetDays switch
            {
                < 0 => $"{-t.OffsetDays} day{(t.OffsetDays == -1 ? "" : "s")} before {field}",
                > 0 => $"{t.OffsetDays} day{(t.OffsetDays == 1 ? "" : "s")} after {field}",
                _ => $"When {field} arrives",
            },
            AutomationTriggerKind.Schedule => AutomationText.Trigger(t with { EntityType = null }) + ", for each row",
            _ => AutomationText.Trigger(t),
        };
    }
}
