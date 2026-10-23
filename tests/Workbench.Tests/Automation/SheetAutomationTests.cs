using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Contracts;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Automations;
using Workbench.Domain.Identity;
using Legacy = Workbench.Domain.Automation;

namespace Workbench.Tests.Automation;

/// <summary>
/// Sheet automations in the one automation model: sheet rules migrated from the old tables fire as they did, the newer
/// triggers and actions, loop protection, and Excel imports running automations as one batch.
/// </summary>
public sealed class SheetAutomationTests
{
    private static AutomationRecipient Email(string address) => new(RecipientKind.Person, address);

    private static Task<AutomationDto> AutomateAsync(AutomationHost host, Guid sheetId, string name, AutomationTrigger trigger, params AutomationAction[] actions) =>
        AutomateAsync(host, sheetId, name, trigger, null, actions);

    private static Task<AutomationDto> AutomateAsync(AutomationHost host, Guid sheetId, string name, AutomationTrigger trigger, AutomationConditionGroup? conditions,
        params AutomationAction[] actions) =>
        host.Get<IAutomationService>().CreateAsync(new SaveAutomationRequest(name, new AutomationDefinition(trigger with { SheetId = sheetId }, conditions, actions)));

    private static async Task<List<AutomationRun>> RunsAsync(AutomationHost host, Guid automationId)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Set<AutomationRun>().AsNoTracking().Where(r => r.AutomationId == automationId && !r.IsDryRun).OrderBy(r => r.StartedAtUtc).ToListAsync();
    }

    // ---- The data migration ----

    [Fact]
    public async Task Migrated_sheet_rules_keep_their_ids_history_and_fire_the_same_way()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        var note = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Note", ColumnKind.Text));
        var fired = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Fired already", Finish: host.Clock.Today));
        var due = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Due today", Finish: host.Clock.Today));

        // Rules as the old engine stored them, with a run log and a date trigger that already fired for one row.
        var created = Rule(sheetId, "Tell the PM", new Legacy.RuleTrigger { Kind = Legacy.RuleTriggerKind.ItemCreated },
            new Legacy.RuleAction { Kind = Legacy.RuleActionKind.Notify, Emails = ["pm@example.com"], Message = "New: {Title}" });
        var blocked = Rule(sheetId, "Escalate", new Legacy.RuleTrigger { Kind = Legacy.RuleTriggerKind.ColumnChanged, Field = status.Id.ToString(), ToValue = "Blocked" },
            new Legacy.RuleAction { Kind = Legacy.RuleActionKind.AssignPerson, Field = owner.Id.ToString(), Value = "lead@example.com" });
        blocked.Conditions = [new Legacy.RuleCondition { Field = owner.Id.ToString(), Operator = Legacy.RuleConditionOperator.IsBlank }];
        var done = Rule(sheetId, "Close out", new Legacy.RuleTrigger { Kind = Legacy.RuleTriggerKind.ColumnChanged, Field = status.Id.ToString(), ToValue = "Done" },
            new Legacy.RuleAction { Kind = Legacy.RuleActionKind.SetValue, Field = note.Id.ToString(), Value = "{Title} closed {Today}" },
            new Legacy.RuleAction { Kind = Legacy.RuleActionKind.CreateChildItem, Value = "Archive {Title}" });
        var dated = Rule(sheetId, "Flag due", new Legacy.RuleTrigger { Kind = Legacy.RuleTriggerKind.DateReached, Field = Legacy.RuleFields.Finish },
            new Legacy.RuleAction { Kind = Legacy.RuleActionKind.SetValue, Field = Legacy.RuleFields.Title, Value = "{Title} [due]" });
        var off = Rule(sheetId, "Off", new Legacy.RuleTrigger { Kind = Legacy.RuleTriggerKind.ItemCreated }, new Legacy.RuleAction { Kind = Legacy.RuleActionKind.Notify, Emails = ["off@example.com"] });
        off.Enabled = false;
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<Legacy.Rule>().AddRange(created, blocked, done, dated, off);
            db.Set<Legacy.RuleRun>().AddRange(
                new Legacy.RuleRun { RuleId = created.Id, SheetId = sheetId, ItemId = fired.Id, FiredAtUtc = host.Clock.Now.UtcDateTime.AddDays(-3), Outcome = Legacy.RuleRunOutcome.Succeeded, Detail = "Notified pm@example.com" },
                new Legacy.RuleRun { RuleId = dated.Id, SheetId = sheetId, ItemId = fired.Id, FiredAtUtc = host.Clock.Now.UtcDateTime.AddHours(-2), Outcome = Legacy.RuleRunOutcome.Succeeded, TriggerDate = host.Clock.Today });
            await db.SaveChangesAsync();
            Assert.Equal(5, await SheetRuleMigration.MigrateAsync(db));
            Assert.Equal(0, await SheetRuleMigration.MigrateAsync(db)); // Idempotent.
            Assert.Empty(await db.Set<Legacy.Rule>().ToListAsync());
        }

        // Same ids, same shape through the rules API, listed with the sheet's automations and in Admin › Automation's model.
        var automations = await host.Get<IAutomationService>().ListForSheetAsync(sheetId);
        Assert.Equal([created.Id, blocked.Id, done.Id, dated.Id, off.Id], automations.Select(a => a.Id));
        Assert.Equal("When Status changes to Blocked", automations[1].TriggerSummary);
        Assert.False(automations[4].IsEnabled);
        var rule = await host.Rules.GetAsync(sheetId, blocked.Id);
        Assert.Equal(new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"), rule.Trigger);
        Assert.Equal(RuleConditionOperator.IsBlank, Assert.Single(rule.Conditions).Operator);
        Assert.Equal(RuleActionKind.AssignPerson, Assert.Single(rule.Actions).Kind);
        Assert.Equal("alice@example.com", rule.CreatedBy);
        Assert.Contains(await host.RunsAsync(sheetId), r => r.RuleId == created.Id && r.Detail == "Notified pm@example.com"); // History kept.

        // Item created → notify, with the rule's message as the title.
        var added = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Pour"));
        var notification = Assert.Single(await host.AllNotificationsAsync(), n => n.RecipientEmail == "pm@example.com");
        Assert.Equal("New: Pour", notification.Title);
        Assert.Equal(NotificationKinds.Rule, notification.Kind);
        Assert.DoesNotContain(await host.AllNotificationsAsync(), n => n.RecipientEmail == "off@example.com");

        // Column changed to a value, with a condition → assign.
        await host.SetAsync(sheetId, added, (status, "Blocked"));
        Assert.Equal("lead@example.com", (await host.Items.ListAsync(sheetId)).Single(i => i.Id == added.Id).Values[owner.Id.ToString()]);

        // Several actions in order, as the automation naming the rule and its author.
        await host.SetAsync(sheetId, added, (status, "Done"));
        var items = await host.Items.ListAsync(sheetId);
        Assert.Equal("Pour closed 2026-10-17", items.Single(i => i.Id == added.Id).Values[note.Id.ToString()]);
        Assert.Equal("Archive Pour", Assert.Single(items, i => i.ParentId == added.Id).Title);
        var activity = await host.Activity.ListAsync(sheetId, added.Id);
        Assert.Contains(activity, a => a.Actor == "Workbench Automation (rule \"Close out\", author alice@example.com)");

        // The date rule fires for the row it hadn't fired for, once. (The hourly job acts through the author's own permission
        // checks; here, through the automation decorator.)
        var unchecked_ = host.Get<AutomationItemServiceAccessor>().Instance!;
        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(unchecked_));
        Assert.Equal(0, await host.Engine.RunDateTriggersAsync(unchecked_));
        items = await host.Items.ListAsync(sheetId);
        Assert.Equal("Due today [due]", items.Single(i => i.Id == due.Id).Title);
        Assert.Equal("Fired already", items.Single(i => i.Id == fired.Id).Title);
    }

    private static int _created;

    /// <summary>A rule as the old engine stored it; each one a minute after the last, so they list in the order made.</summary>
    private static Legacy.Rule Rule(Guid sheetId, string name, Legacy.RuleTrigger trigger, params Legacy.RuleAction[] actions) => new()
    {
        SheetId = sheetId, Name = name, CreatedBy = "Alice@Example.com", CreatedAtUtc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _created)),
        UpdatedAtUtc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), Trigger = trigger, Actions = actions.ToList(),
    };

    // ---- New triggers ----

    [Fact]
    public async Task Added_by_a_form_fires_only_for_rows_a_form_adds()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var owner = await host.PersonColumnAsync(sheetId);
        var automation = await AutomateAsync(host, sheetId, "Triage", new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.AddedByForm]),
            new AutomationAction(AutomationActionKind.AssignPerson, FieldKey: owner.Id.ToString(), Value: "triage@example.com"));

        var typed = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Typed in"));
        ItemDto submitted;
        using (SheetAutomationEvents.AddedByForm("Intake form"))
        {
            submitted = await host.Items.CreateAsync(sheetId, new CreateItemRequest("From the form"));
        }

        var items = await host.Items.ListAsync(sheetId);
        Assert.False(items.Single(i => i.Id == typed.Id).Values.ContainsKey(owner.Id.ToString()));
        Assert.Equal("triage@example.com", items.Single(i => i.Id == submitted.Id).Values[owner.Id.ToString()]);
        var run = Assert.Single(await RunsAsync(host, automation.Id));
        Assert.Equal("Added by the form \"Intake form\"", run.TriggerDetail);
        Assert.Equal("alice@example.com", run.Actor);
    }

    [Fact]
    public async Task Column_change_triggers_can_require_the_old_value_too()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var automation = await AutomateAsync(host, sheetId, "Unblocked", new(AutomationTriggerKind.FieldChanged, FieldKey: status.Id.ToString(), FromValue: "Blocked", ToValue: "Open"),
            new AutomationAction(AutomationActionKind.Notify, [Email("pm@example.com")]));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Install"));

        await host.SetAsync(sheetId, item, (status, "Open"));
        await host.SetAsync(sheetId, item, (status, "Blocked"));
        Assert.Empty(await RunsAsync(host, automation.Id));
        await host.SetAsync(sheetId, item, (status, "open"));

        Assert.Equal(nameof(AutomationRunStatus.Succeeded), Assert.Single(await RunsAsync(host, automation.Id)).Status);
    }

    [Fact]
    public async Task A_recurring_schedule_runs_once_per_due_day_for_each_row_that_matches()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        host.Clock.Now = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero); // A Monday, after 8:00.
        var automation = await AutomateAsync(host, sheetId, "Monday nudge",
            new(AutomationTriggerKind.Schedule, Frequency: ScheduleFrequency.Weekly, DayOfWeek: DayOfWeek.Monday, Hour: 8),
            new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.NotEquals, status.Id.ToString(), "Done")]),
            new AutomationAction(AutomationActionKind.Notify, [new(RecipientKind.Field, owner.Id.ToString())], "Still open: {Title}"));
        var open = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Open one", Values: new Dictionary<string, string?> { [owner.Id.ToString()] = "bob@example.com" }));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Done one", Values: new Dictionary<string, string?>
        {
            [owner.Id.ToString()] = "bob@example.com", [status.Id.ToString()] = "Done",
        }));

        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));
        Assert.Equal(0, await host.Engine.RunDateTriggersAsync(host.Items));
        Assert.Equal("Still open: Open one", Assert.Single(await host.AllNotificationsAsync(), n => n.Kind == NotificationKinds.Rule).Title);

        host.Clock.Advance(TimeSpan.FromDays(1)); // Tuesday: not due.
        Assert.Equal(0, await host.Engine.RunDateTriggersAsync(host.Items));
        host.Clock.Advance(TimeSpan.FromDays(6)); // Next Monday.
        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));
        Assert.Equal([open.Id, open.Id], (await RunsAsync(host, automation.Id)).Select(r => r.RecordId!.Value));
    }

    // ---- New actions ----

    [Fact]
    public async Task Move_and_copy_send_the_row_and_its_sub_items_to_another_sheet_matching_columns_by_name()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var archive = (await host.Sheets.CreateAsync(new CreateSheetRequest("Archive", null, false))).Id;
        var archiveStatus = await host.StatusColumnAsync(archive);
        var rollup = (await host.Sheets.CreateAsync(new CreateSheetRequest("Portfolio", null, false))).Id;
        await AutomateAsync(host, sheetId, "Copy blocked", new(AutomationTriggerKind.FieldChanged, FieldKey: status.Id.ToString(), ToValue: "Blocked"),
            new AutomationAction(AutomationActionKind.CopyRow, TargetSheetId: rollup));
        await AutomateAsync(host, sheetId, "Archive done", new(AutomationTriggerKind.FieldChanged, FieldKey: status.Id.ToString(), ToValue: "Done"),
            new AutomationAction(AutomationActionKind.MoveRow, TargetSheetId: archive));
        var parent = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Phase", Finish: host.Clock.Today));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Task", parent.Id));

        await host.SetAsync(sheetId, parent, (status, "Blocked"));
        var copies = await host.Items.ListAsync(rollup);
        Assert.Equal(["Phase", "Task"], copies.OrderBy(i => i.ParentId is null ? 0 : 1).Select(i => i.Title));
        Assert.Equal(2, (await host.Items.ListAsync(sheetId)).Count);

        await host.SetAsync(sheetId, parent, (status, "Done"));
        Assert.Empty(await host.Items.ListAsync(sheetId));
        var moved = await host.Items.ListAsync(archive);
        var phase = Assert.Single(moved, i => i.ParentId is null);
        Assert.Equal("Done", phase.Values[archiveStatus.Id.ToString()]);
        Assert.Equal(host.Clock.Today, phase.Finish);
        Assert.Equal("Task", Assert.Single(moved, i => i.ParentId == phase.Id).Title);
    }

    [Fact]
    public async Task Actions_on_another_sheet_need_the_author_to_edit_it_when_saved_and_when_run()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var target = (await host.Sheets.CreateAsync(new CreateSheetRequest("Intake", null, false))).Id;
        var automation = await AutomateAsync(host, sheetId, "Log it elsewhere", new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.Created]),
            new AutomationAction(AutomationActionKind.CreateItem, Value: "Seen: {Title}", TargetSheetId: target));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("First"));
        Assert.Equal("Seen: First", Assert.Single(await host.Items.ListAsync(target)).Title);

        // The author loses edit access to the target sheet: the run is refused and logged, the person's row still saves.
        await using (var db = await host.Db.CreateAsync())
        {
            // The target moves to a workspace where the author is only a Viewer.
            var elsewhere = new Workbench.Domain.Workspace { Name = "Elsewhere" };
            db.Workspaces.Add(elsewhere);
            db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = elsewhere.Id, UserEmail = "alice@example.com", Role = WorkspaceRole.Viewer });
            (await db.Sheets.SingleAsync(s => s.Id == target)).WorkspaceId = elsewhere.Id;
            await db.SaveChangesAsync();
        }

        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Second"));
        var refused = (await RunsAsync(host, automation.Id)).Last();
        Assert.Equal(nameof(AutomationRunStatus.Failed), refused.Status);
        Assert.Contains("can't edit \"Intake\"", refused.Error);
        Assert.Contains("Denied", refused.StepsJson);
        Assert.Equal(2, (await host.Items.ListAsync(sheetId)).Count);
        Assert.Single(await host.Items.ListAsync(target));

        // And it can't be saved by someone who can't edit the target.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => AutomateAsync(host, sheetId, "Again", new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.Created]),
            new AutomationAction(AutomationActionKind.MoveRow, TargetSheetId: target)));
    }

    [Fact]
    public async Task Email_and_to_do_actions_reach_people_in_person_columns()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var owner = await host.PersonColumnAsync(sheetId);
        await AutomateAsync(host, sheetId, "Kickoff", new(AutomationTriggerKind.FieldChanged, FieldKey: owner.Id.ToString()),
            new AutomationAction(AutomationActionKind.SendEmail, [new(RecipientKind.Field, owner.Id.ToString()), Email("PM@Example.com ")], "Welcome to {Title}", "You own {Title}."),
            new AutomationAction(AutomationActionKind.CreateTask, [new(RecipientKind.Field, owner.Id.ToString())], "Plan {Title}", DueInDays: 3));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Launch"));

        await host.SetAsync(sheetId, item, (owner, "bob@example.com"));

        var emails = host.Emails.Sent.Where(m => m.Subject == "Welcome to Launch").Select(m => m.To).Order().ToList();
        Assert.Equal(["bob@example.com", "pm@example.com"], emails);
        await using var db = await host.Db.CreateAsync();
        var task = await db.Set<AutomationTask>().SingleAsync();
        Assert.Equal(("bob@example.com", "Plan Launch", host.Clock.Today.AddDays(3)), (task.AssigneeEmail, task.Title, task.DueOn!.Value));
        Assert.Equal($"sheets/{sheetId}?item={item.Id}", task.Link);
    }

    [Fact]
    public async Task Dry_run_explains_a_sheet_automation_without_changing_anything()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Pour", Values: new Dictionary<string, string?> { [status.Id.ToString()] = "Blocked" }));
        var definition = new AutomationDefinition(new(AutomationTriggerKind.FieldChanged, FieldKey: status.Id.ToString(), ToValue: "Blocked", SheetId: sheetId),
            new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.IsEmpty, owner.Id.ToString())]),
            [new(AutomationActionKind.AssignPerson, FieldKey: owner.Id.ToString(), Value: "lead@example.com")]);

        var result = await host.Get<IAutomationService>().DryRunAsync(new AutomationDryRunRequest(null, definition, item.Id, Name: "Escalate"));

        Assert.Equal(AutomationRunStatus.DryRun, result.Status);
        Assert.All(result.Conditions, c => Assert.True(c.Matched));
        Assert.Contains(result.Conditions, c => c.Description == "Owner is empty");
        Assert.Equal("Would assign Owner to lead@example.com", Assert.Single(result.Steps).Detail);
        Assert.False((await host.Items.ListAsync(sheetId)).Single().Values.ContainsKey(owner.Id.ToString()));
    }

    // ---- Loop protection ----

    [Fact]
    public async Task Automations_triggering_each_other_stop_at_three_levels_and_the_hourly_limit_and_pause_hold()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var a = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("A", ColumnKind.Text));
        var b = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("B", ColumnKind.Text));
        var ping = await AutomateAsync(host, sheetId, "Ping", new(AutomationTriggerKind.FieldChanged, FieldKey: a.Id.ToString()),
            new AutomationAction(AutomationActionKind.SetField, FieldKey: b.Id.ToString(), Value: "{A}b"));
        var pong = await AutomateAsync(host, sheetId, "Pong", new(AutomationTriggerKind.FieldChanged, FieldKey: b.Id.ToString()),
            new AutomationAction(AutomationActionKind.SetField, FieldKey: a.Id.ToString(), Value: "{B}a"));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Loop"));

        await host.SetAsync(sheetId, item, (a, "x"));

        var row = (await host.Items.ListAsync(sheetId)).Single();
        Assert.Equal("xbab", row.Values[b.Id.ToString()]);
        var runs = (await RunsAsync(host, ping.Id)).Concat(await RunsAsync(host, pong.Id)).ToList();
        Assert.Equal(3, runs.Count(r => r.Status == nameof(AutomationRunStatus.Succeeded)));
        Assert.Contains("3 levels deep", Assert.Single(runs, r => r.Status == nameof(AutomationRunStatus.Skipped)).Error);
        Assert.Equal(0, Workbench.Application.Automation.AutomationContext.Depth);

        // The tenant's per-row limit (here 2 an hour) stops a busy automation too.
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<AutomationSettings>().Add(new AutomationSettings { MaxRunsPerRecordPerHour = 2 });
            await db.SaveChangesAsync();
        }

        var title = await AutomateAsync(host, sheetId, "On rename", new(AutomationTriggerKind.FieldChanged, FieldKey: AutomationSheets.Title),
            new AutomationAction(AutomationActionKind.Notify, [Email("pm@example.com")]));
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            await host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(name, null, null, null, (await host.Items.ListAsync(sheetId)).Single().Values));
        }

        Assert.Equal([nameof(AutomationRunStatus.Succeeded), nameof(AutomationRunStatus.Succeeded), nameof(AutomationRunStatus.Skipped)],
            (await RunsAsync(host, title.Id)).Select(r => r.Status));

        // Pausing every automation stops sheet automations too.
        await using (var db = await host.Db.CreateAsync())
        {
            (await db.Set<AutomationSettings>().SingleAsync()).PausedAll = true;
            await db.SaveChangesAsync();
        }

        var before = (await RunsAsync(host, ping.Id)).Count;
        await host.SetAsync(sheetId, item, (a, "y"));
        Assert.Equal(before, (await RunsAsync(host, ping.Id)).Count);
    }

    // ---- Excel import ----

    [Fact]
    public async Task An_excel_import_runs_automations_on_each_row_it_adds_or_changes_with_one_notification_per_automation_and_person()
    {
        await using var host = new AutomationHost();
        var plan = host.Get<IPlanScheduleService>();
        var sheetId = (await host.Sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var notes = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Notes", ColumnKind.Text));
        foreach (var title in new[] { "Design", "Build", "Test" })
        {
            await host.Items.CreateAsync(sheetId, new CreateItemRequest(title, Start: host.Clock.Today, Finish: host.Clock.Today));
        }

        var noted = await AutomateAsync(host, sheetId, "Notes changed", new(AutomationTriggerKind.FieldChanged, FieldKey: notes.Id.ToString()),
            new AutomationAction(AutomationActionKind.Notify, [Email("pm@example.com")], "Note on {Title}"),
            new AutomationAction(AutomationActionKind.SendEmail, [Email("pm@example.com")], "Note on {Title}"));
        var added = await AutomateAsync(host, sheetId, "New task", new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.Created]),
            new AutomationAction(AutomationActionKind.Notify, [Email("pm@example.com"), Email("lead@example.com")], "New task {Title}"));
        var file = await plan.ExportAsync(sheetId);
        byte[] edited;
        using (var book = new XLWorkbook(new MemoryStream(file.Content)))
        {
            var sheet = book.Worksheet("Plan");
            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                row.Cell(12).Value = $"Note for {row.Cell(5).GetString()}";
            }

            foreach (var (title, at) in new[] { ("Deploy", 5), ("Train", 6) })
            {
                sheet.Row(at).Cell(3).Value = 1;
                sheet.Row(at).Cell(5).Value = title;
                sheet.Row(at).Cell(6).Value = 1;
            }

            using var stream = new MemoryStream();
            book.SaveAs(stream);
            edited = stream.ToArray();
        }

        var before = (await host.AllNotificationsAsync()).Count;
        await plan.ApplyImportAsync(sheetId, new PlanImportRequest("plan.xlsx", edited));

        // Every row ran (three changed notes, two added rows with no note).
        Assert.Equal(3, (await RunsAsync(host, noted.Id)).Count(r => r.Status == nameof(AutomationRunStatus.Succeeded)));
        Assert.Equal(2, (await RunsAsync(host, added.Id)).Count);
        // But each person heard once per automation.
        var sent = (await host.AllNotificationsAsync()).Skip(before).ToList();
        Assert.Equal(3, sent.Count);
        var summary = Assert.Single(sent, n => n.RecipientEmail == "pm@example.com" && n.Title.StartsWith("Notes changed", StringComparison.Ordinal));
        Assert.Equal("Notes changed: 3 rows from the Excel import (plan.xlsx)", summary.Title);
        Assert.Contains("\"Design\"", summary.Body);
        Assert.Equal($"sheets/{sheetId}", summary.Link);
        Assert.Single(sent, n => n.RecipientEmail == "lead@example.com");
        Assert.Single(host.Emails.Sent, m => m.Subject == "Notes changed: 3 rows from the Excel import (plan.xlsx)");
    }
}
