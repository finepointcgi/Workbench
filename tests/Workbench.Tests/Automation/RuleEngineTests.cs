using Workbench.Application.Automation;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Automation;

public sealed class RuleEngineTests
{
    private static readonly RuleActionDto NotifyPm = new(RuleActionKind.Notify, Emails: ["pm@example.com"]);

    [Fact]
    public async Task Item_service_is_wrapped_by_the_automation_decorator()
    {
        await using var host = new AutomationHost();

        // Permission checks are the outermost decorator; automation runs inside them (the trigger tests below prove it fires).
        Assert.IsType<Workbench.Application.Identity.PermissionCheckingItemService>(host.Items);
        Assert.IsType<AutomationAwareCurrentUser>(host.Get<ICurrentUser>());
    }

    [Fact]
    public async Task Item_created_trigger_runs_and_is_logged()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ItemCreated), NotifyPm);

        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Kickoff"));

        var run = Assert.Single(await host.RunsAsync(sheetId));
        Assert.Equal(RuleRunOutcome.Succeeded, run.Outcome);
        Assert.Equal(item.Id, run.ItemId);
        Assert.Equal("Kickoff", run.ItemTitle);
        Assert.Equal("Notified pm@example.com", run.Detail);
        var notification = Assert.Single(await host.AllNotificationsAsync());
        Assert.Equal("pm@example.com", notification.RecipientEmail);
        Assert.Equal(NotificationKinds.Rule, notification.Kind);
        Assert.Equal($"sheets/{sheetId}?item={item.Id}", notification.Link);
    }

    [Fact]
    public async Task Column_changed_trigger_fires_on_any_change_but_not_without_one()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString()), NotifyPm);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Design"));

        await host.SetAsync(sheetId, item, (status, "Open"));
        await host.SetAsync(sheetId, item, (status, "Open"));
        await host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest("Renamed", null, null, null,
            new Dictionary<string, string?> { [status.Id.ToString()] = "Open" }));

        Assert.Single(await host.RunsAsync(sheetId));
    }

    [Fact]
    public async Task Column_changed_trigger_with_a_target_value_only_fires_on_that_value()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"), NotifyPm);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Design"));

        await host.SetAsync(sheetId, item, (status, "Open"));
        Assert.Empty(await host.RunsAsync(sheetId));

        await host.SetAsync(sheetId, item, (status, "blocked"));
        Assert.Single(await host.RunsAsync(sheetId));
    }

    [Fact]
    public async Task Title_is_a_trigger_field_too()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, RuleFields.Title), NotifyPm);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("A"));

        await host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest("B", null, null, null, new Dictionary<string, string?>()));

        Assert.Single(await host.RunsAsync(sheetId));
    }

    [Theory]
    [InlineData(RuleConditionOperator.Equals, "blocked", "Blocked", true)]
    [InlineData(RuleConditionOperator.Equals, "Open", "Blocked", false)]
    [InlineData(RuleConditionOperator.NotEquals, "Open", "Blocked", true)]
    [InlineData(RuleConditionOperator.NotEquals, "Blocked", "Blocked", false)]
    [InlineData(RuleConditionOperator.IsBlank, null, null, true)]
    [InlineData(RuleConditionOperator.IsBlank, null, "  ", true)]
    [InlineData(RuleConditionOperator.IsBlank, null, "x", false)]
    [InlineData(RuleConditionOperator.Contains, "vend", "Waiting on vendor", true)]
    [InlineData(RuleConditionOperator.Contains, "client", "Waiting on vendor", false)]
    [InlineData(RuleConditionOperator.Contains, "", "anything", false)]
    public void Conditions_compare_case_insensitively(RuleConditionOperator op, string? expected, string? actual, bool holds)
    {
        var item = new ItemDto(Guid.NewGuid(), null, 0, "T", null, null, null, new Dictionary<string, string?> { ["c"] = actual });

        Assert.Equal(holds, RuleLogic.ConditionHolds(new Domain.Automation.RuleCondition
        {
            Field = "c",
            Operator = (Domain.Automation.RuleConditionOperator)(int)op,
            Value = expected,
        }, item));
    }

    [Fact]
    public async Task Conditions_are_combined_with_and()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        await host.Rules.CreateAsync(sheetId, new SaveRuleRequest("Blocked and owned", true,
            new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString()),
            [
                new RuleConditionDto(status.Id.ToString(), RuleConditionOperator.Equals, "Blocked"),
                new RuleConditionDto(owner.Id.ToString(), RuleConditionOperator.IsBlank),
            ],
            [NotifyPm]));
        var a = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Unowned"));
        var b = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Owned"));
        await host.SetAsync(sheetId, b, (owner, "bob@example.com"));

        await host.SetAsync(sheetId, a, (status, "Blocked"));
        await host.SetAsync(sheetId, b, (status, "Blocked"));
        await host.SetAsync(sheetId, a, (status, "Open"));

        var run = Assert.Single(await host.RunsAsync(sheetId));
        Assert.Equal(a.Id, run.ItemId);
    }

    [Fact]
    public async Task Set_value_action_runs_as_workbench_automation()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var note = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Note", ColumnKind.Text));
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Done"),
            new RuleActionDto(RuleActionKind.SetValue, note.Id.ToString(), "{Title} closed {Today}"),
            new RuleActionDto(RuleActionKind.SetValue, RuleFields.Finish, "{Today}"));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Ship"));

        await host.SetAsync(sheetId, item, (status, "Done"));

        var after = (await host.Items.ListAsync(sheetId)).Single();
        Assert.Equal("Ship closed 2026-10-17", after.Values[note.Id.ToString()]);
        Assert.Equal("Done", after.Values[status.Id.ToString()]);
        Assert.Equal(host.Clock.Today, after.Finish);
        var activity = await host.Activity.ListAsync(sheetId, item.Id);
        Assert.Equal(RuleFields.AutomationActor, activity[0].Actor);
        Assert.Contains(activity, a => a.Actor == "alice@example.com" && a.Summary.Contains("Status"));
        Assert.Equal("Set Note to \"Ship closed 2026-10-17\"; Set Finish to \"2026-10-17\"", Assert.Single(await host.RunsAsync(sheetId)).Detail);
    }

    [Fact]
    public async Task Assign_person_action_sets_the_column_and_notifies_the_assignee()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"),
            new RuleActionDto(RuleActionKind.AssignPerson, owner.Id.ToString(), "Lead@Example.com"));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Fix"));

        await host.SetAsync(sheetId, item, (status, "Blocked"));

        Assert.Equal("lead@example.com", (await host.Items.ListAsync(sheetId)).Single().Values[owner.Id.ToString()]);
        var notification = Assert.Single(await host.AllNotificationsAsync());
        Assert.Equal(NotificationKinds.Assignment, notification.Kind);
        Assert.Equal("lead@example.com", notification.RecipientEmail);
        Assert.Equal("Workbench Automation assigned you to \"Fix\"", notification.Title);
    }

    [Fact]
    public async Task Create_child_action_adds_a_subitem_from_the_template()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"),
            new RuleActionDto(RuleActionKind.CreateChildItem, Value: "Unblock {title} ({Status})"));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Install"));

        await host.SetAsync(sheetId, item, (status, "Blocked"));

        var child = Assert.Single(await host.Items.ListAsync(sheetId), i => i.ParentId == item.Id);
        Assert.Equal("Unblock Install (Blocked)", child.Title);
    }

    [Fact]
    public async Task Notify_action_reaches_fixed_emails_and_person_columns_once_each()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        var reviewer = await host.PersonColumnAsync(sheetId, "Reviewer");
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"),
            new RuleActionDto(RuleActionKind.Notify, Emails: ["pm@example.com"], PersonFields: [owner.Id.ToString(), reviewer.Id.ToString()],
                Message: "{Title} is {Status}"));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Pour"));
        await host.SetAsync(sheetId, item, (owner, "bob@example.com"), (reviewer, "pm@example.com"));
        var before = (await host.AllNotificationsAsync()).Count;

        await host.SetAsync(sheetId, item, (status, "Blocked"));

        var sent = (await host.AllNotificationsAsync()).Skip(before).ToList();
        Assert.Equal(["bob@example.com", "pm@example.com"], sent.Select(n => n.RecipientEmail).Order());
        Assert.All(sent, n => Assert.Equal("Pour is Blocked", n.Title));
    }

    [Fact]
    public async Task Rule_chains_stop_at_depth_three()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var counter = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Counter", ColumnKind.Text));

        // Every change to Counter changes Counter again: an endless loop without the cap.
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, counter.Id.ToString()),
            new RuleActionDto(RuleActionKind.SetValue, counter.Id.ToString(), "{Counter}+"));
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Loop"));

        await host.SetAsync(sheetId, item, (counter, "x"));

        Assert.Equal("x+++", (await host.Items.ListAsync(sheetId)).Single().Values[counter.Id.ToString()]);
        var runs = await host.RunsAsync(sheetId);
        Assert.Equal(3, runs.Count(r => r.Outcome == RuleRunOutcome.Succeeded));
        var skipped = Assert.Single(runs, r => r.Outcome == RuleRunOutcome.Skipped);
        Assert.Contains("3 levels", skipped.Error);
        Assert.Equal(0, AutomationContext.Depth);
    }

    [Fact]
    public async Task A_failing_action_is_logged_and_does_not_fail_the_change()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ItemCreated),
            new RuleActionDto(RuleActionKind.SetValue, RuleFields.Start, "not a date"));

        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Still saved"));

        Assert.Equal("Still saved", (await host.Items.ListAsync(sheetId)).Single().Title);
        var run = Assert.Single(await host.RunsAsync(sheetId));
        Assert.Equal(RuleRunOutcome.Failed, run.Outcome);
        Assert.Contains("isn't a yyyy-MM-dd date", run.Error);
        Assert.Equal(item.Id, run.ItemId);
    }

    [Fact]
    public async Task Disabled_rules_do_not_run()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var rule = await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ItemCreated), NotifyPm);
        await host.Rules.SetEnabledAsync(sheetId, rule.Id, false);

        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Quiet"));

        Assert.Empty(await host.RunsAsync(sheetId));
    }

    [Fact]
    public async Task Live_subscribers_do_not_run_as_the_automation()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var owner = await host.PersonColumnAsync(sheetId);
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.ItemCreated),
            new RuleActionDto(RuleActionKind.AssignPerson, owner.Id.ToString(), "alice@example.com"));
        var depths = new List<int>();
        await using var feed = await host.Get<INotificationFeed>().SubscribeAsync(_ =>
        {
            depths.Add(AutomationContext.Depth);
            return Task.CompletedTask;
        });
        await using var live = await host.Get<ISheetLiveUpdates>().SubscribeAsync(sheetId, _ =>
        {
            depths.Add(AutomationContext.Depth);
            return Task.CompletedTask;
        });

        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Mine"));

        // One assignment notification and two sheet changes (the create and the rule's update), all at depth 0.
        Assert.Equal([0, 0, 0], depths);
    }
}
