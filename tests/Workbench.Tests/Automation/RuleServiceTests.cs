using Workbench.Contracts;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Automation;

public sealed class RuleServiceTests
{
    private static readonly RuleActionDto NotifyPm = new(RuleActionKind.Notify, Emails: ["PM@example.com "]);

    [Fact]
    public async Task Crud_round_trips_and_records_the_author()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);

        var created = await host.Rules.CreateAsync(sheetId, new SaveRuleRequest(" Blocked ", true,
            new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"),
            [new RuleConditionDto(RuleFields.Title, RuleConditionOperator.Contains, "pour")],
            [NotifyPm]));
        Assert.Equal("Blocked", created.Name);
        Assert.Equal("alice@example.com", created.CreatedBy);
        Assert.Equal(["pm@example.com"], created.Actions[0].Emails);

        var updated = await host.Rules.UpdateAsync(sheetId, created.Id, new SaveRuleRequest("Renamed", false, created.Trigger, [], created.Actions));
        Assert.Equal("Renamed", updated.Name);
        Assert.False(updated.Enabled);
        Assert.Empty((await host.Rules.GetAsync(sheetId, created.Id)).Conditions);

        Assert.True((await host.Rules.SetEnabledAsync(sheetId, created.Id, true)).Enabled);
        await host.Rules.DeleteAsync(sheetId, created.Id);
        Assert.Empty(await host.Rules.ListAsync(sheetId));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Rules.GetAsync(sheetId, created.Id));
    }

    [Fact]
    public async Task Invalid_rules_are_rejected()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var text = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Notes", ColumnKind.Text));
        SaveRuleRequest Make(RuleTriggerDto trigger, params RuleActionDto[] actions) => new("R", true, trigger, [], actions);
        var created = new RuleTriggerDto(RuleTriggerKind.ItemCreated);

        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(created)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(new RuleTriggerDto(RuleTriggerKind.ColumnChanged, Guid.NewGuid().ToString()), NotifyPm)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(new RuleTriggerDto(RuleTriggerKind.DateReached, text.Id.ToString()), NotifyPm)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(created, new RuleActionDto(RuleActionKind.Notify, Emails: ["nope"]))));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(created, new RuleActionDto(RuleActionKind.Notify))));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(created, new RuleActionDto(RuleActionKind.AssignPerson, text.Id.ToString(), "a@b.co"))));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Rules.CreateAsync(sheetId, Make(created, new RuleActionDto(RuleActionKind.CreateChildItem, Value: " "))));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Rules.CreateAsync(Guid.NewGuid(), Make(created, NotifyPm)));
    }

    [Fact]
    public async Task Dry_run_explains_without_changing_anything()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var owner = await host.PersonColumnAsync(sheetId);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Pour", Values: new Dictionary<string, string?>
        {
            [status.Id.ToString()] = "Blocked",
        }));
        var rule = new SaveRuleRequest("Escalate", true,
            new RuleTriggerDto(RuleTriggerKind.ColumnChanged, status.Id.ToString(), "Blocked"),
            [new RuleConditionDto(owner.Id.ToString(), RuleConditionOperator.IsBlank)],
            [NotifyPm, new RuleActionDto(RuleActionKind.AssignPerson, owner.Id.ToString(), "lead@example.com")]);

        var result = await host.Rules.TestAsync(sheetId, new TestRuleRequest(rule, item.Id));

        Assert.True(result.WouldRun);
        Assert.Contains(result.Steps, s => s.StartsWith("✓ Owner is blank"));
        Assert.Equal(["Notify pm@example.com", "Assign Owner to lead@example.com"], result.PlannedActions);
        Assert.Empty(await host.RunsAsync(sheetId));
        Assert.Empty(await host.AllNotificationsAsync());
        Assert.Empty(await host.Rules.ListAsync(sheetId));

        var open = await host.SetAsync(sheetId, item, (status, "Open"));
        Assert.False((await host.Rules.TestAsync(sheetId, new TestRuleRequest(rule, open.Id))).TriggerMatches);
    }
}
