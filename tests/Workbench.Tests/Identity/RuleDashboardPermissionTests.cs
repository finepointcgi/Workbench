using Workbench.Contracts;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Dashboards;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>Automation rules, dashboards and widget data behind the real permission decorators.</summary>
public sealed class RuleDashboardPermissionTests
{
    private static SaveRuleRequest Rule(string name = "Ping") =>
        new(name, true, new RuleTriggerDto(RuleTriggerKind.ItemCreated), [], [new RuleActionDto(RuleActionKind.Notify, Emails: [Owner])]);

    [Fact]
    public async Task Rules_need_editor_to_change_and_viewer_to_see()
    {
        await using var seeded = await CreateAsync();
        var rule = await seeded.As(Editor).Host.Get<IRuleService>().CreateAsync(seeded.SheetId, Rule());

        var viewer = seeded.As(Viewer).Host.Get<IRuleService>();
        Assert.Single(await viewer.ListAsync(seeded.SheetId));
        Assert.Equal(rule.Id, (await viewer.GetAsync(seeded.SheetId, rule.Id)).Id);
        await viewer.ListRunsAsync(seeded.SheetId);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.CreateAsync(seeded.SheetId, Rule("Nope")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.UpdateAsync(seeded.SheetId, rule.Id, Rule("Renamed")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.SetEnabledAsync(seeded.SheetId, rule.Id, false));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.DeleteAsync(seeded.SheetId, rule.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Commenter).Host.Get<IRuleService>().SetEnabledAsync(seeded.SheetId, rule.Id, false));

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(Outsider).Host.Get<IRuleService>().ListAsync(seeded.SheetId));

        var editor = seeded.As(Editor).Host.Get<IRuleService>();
        Assert.False((await editor.SetEnabledAsync(seeded.SheetId, rule.Id, false)).Enabled);
        await editor.DeleteAsync(seeded.SheetId, rule.Id);
        Assert.Empty(await editor.ListAsync(seeded.SheetId));
    }

    [Fact]
    public async Task Owners_edit_dashboards_and_workspace_members_view_workspace_ones()
    {
        await using var seeded = await CreateAsync();
        var owner = seeded.As(Owner).Host.Get<IDashboardService>();
        var shared = await owner.CreateAsync(new CreateDashboardRequest("Program", null, seeded.ProgramOfficeId));
        var personal = await owner.CreateAsync(new CreateDashboardRequest("Mine"));

        var viewer = seeded.As(Viewer).Host.Get<IDashboardService>();
        Assert.Equal([shared.Id], (await viewer.ListAsync()).Select(d => d.Id));
        Assert.Equal("Program", (await viewer.GetAsync(shared.Id)).Summary.Name);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => viewer.GetAsync(personal.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.UpdateAsync(shared.Id, new UpdateDashboardRequest("Mine now", null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.AddWidgetAsync(shared.Id,
            new SaveWidgetRequest("Hi", WidgetType.RichText, 6, WidgetHeight.Small, new WidgetConfig(Markdown: "hi"))));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.DeleteAsync(shared.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.CreateAsync(new CreateDashboardRequest("Team", null, seeded.ProgramOfficeId)));

        // An editor who isn't the owner still only views it.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Editor).Host.Get<IDashboardService>().DeleteAsync(shared.Id));

        var outsider = seeded.As(Outsider).Host.Get<IDashboardService>();
        Assert.Empty(await outsider.ListAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => outsider.GetAsync(shared.Id));

        Assert.Equal(2, (await seeded.As(Admin).Host.Get<IDashboardService>().ListAsync()).Count);
        await seeded.As(Owner).Host.Get<IDashboardService>().UpdateAsync(shared.Id, new UpdateDashboardRequest("Program office", null));
    }

    [Fact]
    public async Task Widget_data_hides_sheets_financials_and_funding_the_viewer_may_not_see()
    {
        await using var seeded = await CreateAsync();
        var f = await ProjectAccountingFixture.SeedAsync(seeded);
        var dashboards = seeded.As(Admin).Host.Get<IDashboardService>();
        var dashboard = await dashboards.CreateAsync(new CreateDashboardRequest("Program", null, seeded.ProgramOfficeId));
        var hidden = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Budget sheet", WidgetType.Metric, 3, WidgetHeight.Small, new WidgetConfig(seeded.FinanceSheetId)));
        var schedule = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Tasks", WidgetType.Metric, 3, WidgetHeight.Small, new WidgetConfig(seeded.SheetId)));
        var money = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Money", WidgetType.ProjectFinancials, 6, WidgetHeight.Medium, new WidgetConfig(seeded.SheetId)));
        var funding = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Funding", WidgetType.ContractFunding, 6, WidgetHeight.Medium, new WidgetConfig(ContractId: f.Contract1)));

        var viewer = seeded.As(Viewer).Host.Get<IWidgetDataService>();
        Assert.Contains("isn't shared with you", (await viewer.GetAsync(dashboard.Id, hidden.Id)).Error);
        Assert.Equal(1m, (await viewer.GetAsync(dashboard.Id, schedule.Id)).Metric!.Value);
        var noMoney = await viewer.GetAsync(dashboard.Id, money.Id);
        Assert.Null(noMoney.ProjectFinancials);
        Assert.Contains("Only Finance", noMoney.Error);
        var noFunding = await viewer.GetAsync(dashboard.Id, funding.Id);
        Assert.Null(noFunding.ContractFunding);
        Assert.Contains("Only Finance", noFunding.Error);

        // pm@ manages 1001 (whose plan sheet is the schedule) on contract C1.
        var pm = seeded.As(Owner).Host.Get<IWidgetDataService>();
        Assert.Equal("1001", (await pm.GetAsync(dashboard.Id, money.Id)).ProjectFinancials!.ProjectCode);
        Assert.Equal(400_000m, (await pm.GetAsync(dashboard.Id, funding.Id)).ContractFunding!.FundedValue);

        // Someone outside the workspace can't open the dashboard at all.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(Outsider).Host.Get<IWidgetDataService>().GetAsync(dashboard.Id, money.Id));
    }
}
