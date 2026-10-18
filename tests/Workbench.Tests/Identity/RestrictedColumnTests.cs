using Workbench.Contracts;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Sheets;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// "Finance and owners only" columns: Finance, admins and the workspace's owners see them; everyone else's reads leave
/// them out, including formulas reading them, activity, rules and dashboards, and their writes can't touch them.
/// </summary>
public sealed class RestrictedColumnTests
{
    private sealed record Fixture(SeededHost Seeded, ColumnDto Hours, ColumnDto Budget, ColumnDto CostPerHour, ColumnDto Status) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Seeded.DisposeAsync();
    }

    private static async Task<Fixture> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        await using (var db = await seeded.Host.Get<Workbench.Application.IWorkbenchDbContextFactory>().CreateAsync())
        {
            // Finance only views the workspace: its org role is what lets it see restricted columns.
            db.Set<Workbench.Domain.Identity.WorkspaceMember>().Add(new() { WorkspaceId = seeded.ProgramOfficeId, UserEmail = Finance, Role = Workbench.Domain.Identity.WorkspaceRole.Viewer });
            await db.SaveChangesAsync();
        }

        var columns = seeded.As(Owner).Host.Columns;
        var hours = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Planned hours", ColumnKind.Number));
        var budget = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Budget", ColumnKind.Currency, IsRestricted: true));
        var cost = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Cost per hour", ColumnKind.Currency, Formula: "[Budget] / [Planned hours]"));
        var status = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Status", ColumnKind.Text));
        var item = (await seeded.Host.Items.ListAsync(seeded.SheetId)).Single();
        await seeded.Host.Items.UpdateAsync(seeded.SheetId, item.Id, new UpdateItemRequest(item.Title, null, null, null,
            new Dictionary<string, string?> { [hours.Id.ToString()] = "8", [budget.Id.ToString()] = "1200", [status.Id.ToString()] = "Open" }));
        return new Fixture(seeded, hours, budget, cost, status);
    }

    [Fact]
    public async Task A_formula_reading_a_restricted_column_is_restricted_too()
    {
        await using var f = await CreateAsync();
        Assert.True(f.Budget.IsRestricted);
        Assert.True(f.CostPerHour is { IsRestricted: false, ReadsRestricted: true });
        Assert.False(f.Hours.IsEffectivelyRestricted);
    }

    [Theory]
    [InlineData(Owner, true)]
    [InlineData(Finance, true)]
    [InlineData(Admin, true)]
    [InlineData(Editor, false)]
    [InlineData(Viewer, false)]
    public async Task Only_finance_admins_and_owners_get_the_columns_and_their_values(string who, bool sees)
    {
        await using var f = await CreateAsync();
        var host = f.Seeded.As(who).Host;

        var sheet = (await host.Sheets.GetAsync(f.Seeded.SheetId))!;
        var item = (await host.Items.ListAsync(f.Seeded.SheetId)).Single();

        Assert.Equal(sees, sheet.CanSeeRestricted);
        Assert.Equal(sees, sheet.Columns.Any(c => c.Id == f.Budget.Id));
        Assert.Equal(sees, sheet.Columns.Any(c => c.Id == f.CostPerHour.Id));
        Assert.Contains(sheet.Columns, c => c.Id == f.Hours.Id);
        Assert.Equal(sees ? "1200" : null, item.Values.GetValueOrDefault(f.Budget.Id.ToString()));
        Assert.Equal(sees ? "150" : null, item.Values.GetValueOrDefault(f.CostPerHour.Id.ToString()));
        Assert.Equal("8", item.Values.GetValueOrDefault(f.Hours.Id.ToString()));
    }

    [Fact]
    public async Task An_editor_who_cant_see_them_keeps_them_intact_and_cant_write_them()
    {
        await using var f = await CreateAsync();
        var items = f.Seeded.As(Editor).Host.Items;
        var mine = (await items.ListAsync(f.Seeded.SheetId)).Single();

        // A full-set update of what they can see, plus an attempt to overwrite the budget.
        var values = new Dictionary<string, string?>(mine.Values) { [f.Status.Id.ToString()] = "Done", [f.Budget.Id.ToString()] = "1" };
        var saved = await items.UpdateAsync(f.Seeded.SheetId, mine.Id, new UpdateItemRequest(mine.Title, null, null, null, values));
        Assert.False(saved.Values.ContainsKey(f.Budget.Id.ToString()));

        var created = await items.CreateAsync(f.Seeded.SheetId, new CreateItemRequest("New", Values: new Dictionary<string, string?> { [f.Budget.Id.ToString()] = "99" }));

        var owner = (await f.Seeded.As(Owner).Host.Items.ListAsync(f.Seeded.SheetId)).ToDictionary(i => i.Id);
        Assert.Equal("1200", owner[mine.Id].Values[f.Budget.Id.ToString()]);
        Assert.Equal("Done", owner[mine.Id].Values[f.Status.Id.ToString()]);
        Assert.Null(owner[created.Id].Values.GetValueOrDefault(f.Budget.Id.ToString()));
    }

    [Fact]
    public async Task Restricting_changing_and_deleting_hidden_columns_is_for_those_who_can_see_them()
    {
        await using var f = await CreateAsync();
        var columns = f.Seeded.As(Editor).Host.Columns;
        var id = f.Seeded.SheetId;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => columns.CreateAsync(id, new CreateColumnRequest("Rate", ColumnKind.Currency, IsRestricted: true)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => columns.UpdateAsync(id, f.Hours.Id, new UpdateColumnRequest("Planned hours", [], null, f.Hours.Order, IsRestricted: true)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => columns.UpdateAsync(id, f.Budget.Id, new UpdateColumnRequest("Budget", [], null, f.Budget.Order)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => columns.DeleteAsync(id, f.Budget.Id));

        // The owner lifts the restriction: everyone sees the column (and the formula reading it) again.
        await f.Seeded.As(Owner).Host.Columns.UpdateAsync(id, f.Budget.Id, new UpdateColumnRequest("Budget", [], null, f.Budget.Order, IsRestricted: false));
        var sheet = (await f.Seeded.As(Editor).Host.Sheets.GetAsync(id))!;
        Assert.Contains(sheet.Columns, c => c.Id == f.Budget.Id);
        Assert.Contains(sheet.Columns, c => c.Id == f.CostPerHour.Id && !c.IsEffectivelyRestricted);
    }

    [Fact]
    public async Task Activity_never_names_or_quotes_a_restricted_column()
    {
        await using var f = await CreateAsync();
        var host = f.Seeded.As(Owner).Host;
        var item = (await host.Items.ListAsync(f.Seeded.SheetId)).Single();
        var values = new Dictionary<string, string?>(item.Values) { [f.Budget.Id.ToString()] = "4321" };
        await host.Items.UpdateAsync(f.Seeded.SheetId, item.Id, new UpdateItemRequest(item.Title, null, null, null, values));

        var activity = await f.Seeded.As(Viewer).Host.Get<IActivityService>().ListAsync(f.Seeded.SheetId);
        Assert.Contains(activity, a => a.Summary.Contains("Restricted column changed"));
        Assert.Contains(activity, a => a.Summary == "Added a restricted column");
        Assert.DoesNotContain(activity, a => a.Summary.Contains("4321") || a.Summary.Contains("1200") || a.Summary.Contains("Budget") || a.Summary.Contains("Cost per hour"));
    }

    [Fact]
    public async Task Rules_using_restricted_columns_are_hidden_from_and_closed_to_everyone_else()
    {
        await using var f = await CreateAsync();
        var id = f.Seeded.SheetId;
        var onBudget = new SaveRuleRequest("Budget watch", true, new RuleTriggerDto(RuleTriggerKind.ColumnChanged, f.Budget.Id.ToString()), [],
            [new RuleActionDto(RuleActionKind.Notify, Emails: [Owner])]);
        var rule = await f.Seeded.As(Owner).Host.Get<IRuleService>().CreateAsync(id, onBudget);

        var rules = f.Seeded.As(Editor).Host.Get<IRuleService>();
        Assert.Empty(await rules.ListAsync(id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => rules.GetAsync(id, rule.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => rules.DeleteAsync(id, rule.Id));
        var leaky = new SaveRuleRequest("Tell me", true, new RuleTriggerDto(RuleTriggerKind.ItemCreated), [],
            [new RuleActionDto(RuleActionKind.Notify, Emails: [Editor], Message: "Budget is {Budget}")]);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => rules.CreateAsync(id, leaky));

        Assert.Single(await f.Seeded.As(Owner).Host.Get<IRuleService>().ListAsync(id));
    }

    [Fact]
    public async Task A_dashboard_summing_a_restricted_column_shows_figures_only_to_those_who_can_see_it()
    {
        await using var f = await CreateAsync();
        var dashboards = f.Seeded.As(Owner).Host.Get<IDashboardService>();
        var dashboard = await dashboards.CreateAsync(new CreateDashboardRequest("Program", WorkspaceId: f.Seeded.ProgramOfficeId));
        var widget = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Total budget", WidgetType.Metric, 3, WidgetHeight.Small,
            new WidgetConfig(f.Seeded.SheetId, MetricAggregate.Sum, f.Budget.Id)));

        var owner = await f.Seeded.As(Owner).Host.Get<IWidgetDataService>().GetAsync(dashboard.Id, widget.Id);
        var viewer = await f.Seeded.As(Viewer).Host.Get<IWidgetDataService>().GetAsync(dashboard.Id, widget.Id);

        Assert.Null(owner.Error);
        Assert.Equal(1200m, owner.Metric!.Value);
        Assert.Null(viewer.Metric);
        Assert.Contains("restricted", viewer.Error);
    }
}
