using Workbench.Contracts.Automation;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Automation;

public sealed class DateTriggerTests
{
    private static readonly RuleActionDto NotifyPm = new(RuleActionKind.Notify, Emails: ["pm@example.com"]);

    [Fact]
    public async Task Fires_once_per_item_and_date()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.DateReached, RuleFields.Finish, OffsetDays: -2), NotifyPm);
        var due = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Due soon", Finish: host.Clock.Today.AddDays(2)));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Later", Finish: host.Clock.Today.AddDays(3)));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("No date"));

        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));
        Assert.Equal(0, await host.Engine.RunDateTriggersAsync(host.Items));
        host.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, await host.Engine.RunDateTriggersAsync(host.Items));

        var run = Assert.Single(await host.RunsAsync(sheetId));
        Assert.Equal(due.Id, run.ItemId);

        // The next day "Later" is due; "Due soon" doesn't fire again.
        host.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));
        Assert.Equal(2, (await host.RunsAsync(sheetId)).Count);
    }

    [Fact]
    public async Task Fires_again_when_the_date_moves()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.DateReached, RuleFields.Finish), NotifyPm);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Due", Finish: host.Clock.Today));
        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));

        await host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest("Due", null, host.Clock.Today.AddDays(1), null, new Dictionary<string, string?>()));
        host.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));
    }

    [Fact]
    public async Task Uses_date_columns_offsets_after_and_conditions()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var review = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Review", ColumnKind.Date));
        var status = await host.StatusColumnAsync(sheetId);
        await host.Rules.CreateAsync(sheetId, new SaveRuleRequest("Overdue review", true,
            new RuleTriggerDto(RuleTriggerKind.DateReached, review.Id.ToString(), OffsetDays: 1),
            [new RuleConditionDto(status.Id.ToString(), RuleConditionOperator.NotEquals, "Done")],
            [NotifyPm]));
        var yesterday = host.Clock.Today.AddDays(-1).ToString("yyyy-MM-dd");
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Open", Values: new Dictionary<string, string?> { [review.Id.ToString()] = yesterday }));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Done", Values: new Dictionary<string, string?>
        {
            [review.Id.ToString()] = yesterday,
            [status.Id.ToString()] = "Done",
        }));

        Assert.Equal(1, await host.Engine.RunDateTriggersAsync(host.Items));
        Assert.Equal("Open", Assert.Single(await host.RunsAsync(sheetId)).ItemTitle);
    }

    [Fact]
    public async Task Ignores_dates_long_past()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        await host.RuleAsync(sheetId, new RuleTriggerDto(RuleTriggerKind.DateReached, RuleFields.Start), NotifyPm);
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Ancient", Start: host.Clock.Today.AddDays(-30)));

        Assert.Equal(0, await host.Engine.RunDateTriggersAsync(host.Items));
    }
}
