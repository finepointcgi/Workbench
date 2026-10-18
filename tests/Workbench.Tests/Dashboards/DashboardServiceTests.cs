using Workbench.Application;
using Workbench.Application.Dashboards;
using Workbench.Application.Financials;
using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Collaboration;
using static Workbench.Tests.MyWork.MyWorkServiceTests;

namespace Workbench.Tests.Dashboards;

public sealed class DashboardServiceTests
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly IWorkbenchDbContextFactory _db = TestDb.CreateFactory();

    private DashboardService Service(string email = "pm@example.com") => new(_db, new TestUser { Email = email }, _clock);

    private static SaveWidgetRequest Text(string title, int width = 6) =>
        new(title, WidgetType.RichText, width, WidgetHeight.Small, new WidgetConfig(Markdown: "# Hi"));

    [Fact]
    public async Task Creates_renames_lists_and_deletes()
    {
        var service = Service();
        var created = await service.CreateAsync(new CreateDashboardRequest("  Ops  ", " Weekly "));
        Assert.Equal(("Ops", "Weekly", "pm@example.com"), (created.Name, created.Description, created.OwnerEmail));

        await service.UpdateAsync(created.Id, new UpdateDashboardRequest("Operations", null));
        await service.AddWidgetAsync(created.Id, Text("Intro"));

        var listed = Assert.Single(await service.ListAsync());
        Assert.Equal(("Operations", null, 1), (listed.Name, listed.Description, listed.WidgetCount));

        await service.DeleteAsync(created.Id);
        Assert.Empty(await service.ListAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.GetAsync(created.Id));
    }

    [Fact]
    public async Task Adds_edits_reorders_and_deletes_widgets()
    {
        var service = Service();
        var dashboard = await service.CreateAsync(new CreateDashboardRequest("Ops"));
        var sheetId = Guid.NewGuid();
        var a = await service.AddWidgetAsync(dashboard.Id, Text("A"));
        var b = await service.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("B", WidgetType.Metric, 3, WidgetHeight.Medium,
            new WidgetConfig(sheetId, MetricAggregate.Sum, Guid.NewGuid(), Guid.NewGuid(), "Done")));
        var c = await service.AddWidgetAsync(dashboard.Id, Text("C"));
        Assert.Equal([0, 1, 2], new[] { a.Order, b.Order, c.Order });

        var loaded = await service.GetAsync(dashboard.Id);
        Assert.Equal(b.Config, loaded.Widgets[1].Config); // config survives the JSON round trip

        await service.UpdateWidgetAsync(dashboard.Id, a.Id, Text("A2", width: 12));
        var reordered = await service.ReorderWidgetsAsync(dashboard.Id, new ReorderWidgetsRequest([c.Id, a.Id, b.Id]));
        Assert.Equal(["C", "A2", "B"], reordered.Widgets.Select(w => w.Title));
        Assert.Equal(12, reordered.Widgets[1].Width);

        await service.DeleteWidgetAsync(dashboard.Id, c.Id);
        var after = await service.GetAsync(dashboard.Id);
        Assert.Equal([("A2", 0), ("B", 1)], after.Widgets.Select(w => (w.Title, w.Order)));
    }

    [Fact]
    public async Task Rejects_invalid_widgets_and_partial_reorders()
    {
        var service = Service();
        var dashboard = await service.CreateAsync(new CreateDashboardRequest("Ops"));
        var a = await service.AddWidgetAsync(dashboard.Id, Text("A"));
        await service.AddWidgetAsync(dashboard.Id, Text("B"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddWidgetAsync(dashboard.Id, Text("Wide", width: 13)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddWidgetAsync(dashboard.Id, Text("Narrow", width: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddWidgetAsync(dashboard.Id,
            new SaveWidgetRequest("No sheet", WidgetType.Chart, 6, WidgetHeight.Medium, new WidgetConfig())));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReorderWidgetsAsync(dashboard.Id, new ReorderWidgetsRequest([a.Id])));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReorderWidgetsAsync(dashboard.Id, new ReorderWidgetsRequest([a.Id, a.Id])));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddWidgetAsync(dashboard.Id,
            new SaveWidgetRequest("No contract", WidgetType.ContractFunding, 6, WidgetHeight.Medium, new WidgetConfig())));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddWidgetAsync(dashboard.Id,
            new SaveWidgetRequest("Financials", WidgetType.ProjectFinancials, 6, WidgetHeight.Medium, new WidgetConfig())));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.DeleteWidgetAsync(dashboard.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Widget_data_goes_through_the_sheet_contracts()
    {
        var sheets = new SheetService(_db, _clock);
        var columns = new ColumnService(_db, _clock);
        var items = new ItemService(_db, _clock);
        var tracker = await sheets.CreateAsync(new CreateSheetRequest("Tracker", null, false));
        var status = await columns.CreateAsync(tracker.Id, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Done"]));
        await items.CreateAsync(tracker.Id, new CreateItemRequest("One", Values: new Dictionary<string, string?> { [status.Id.ToString()] = "Open" }));
        await items.CreateAsync(tracker.Id, new CreateItemRequest("Two", Values: new Dictionary<string, string?> { [status.Id.ToString()] = "Done" }));
        var hidden = await sheets.CreateAsync(new CreateSheetRequest("Hidden", null, false));

        var dashboards = Service();
        var dashboard = await dashboards.CreateAsync(new CreateDashboardRequest("Ops"));
        var open = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Open", WidgetType.Metric, 3, WidgetHeight.Small,
            new WidgetConfig(tracker.Id, FilterColumnId: status.Id, FilterValue: "Open")));
        var health = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Health", WidgetType.ScheduleHealth, 3, WidgetHeight.Small,
            new WidgetConfig(tracker.Id)));
        var secret = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Secret", WidgetType.Metric, 3, WidgetHeight.Small,
            new WidgetConfig(hidden.Id)));
        var text = await dashboards.AddWidgetAsync(dashboard.Id, Text("Text"));

        var audit = new AuditRecorder(new TestUser(), _clock);
        var contracts = new ContractService(_db, audit);
        var projects = new ProjectService(_db, audit);
        var customer = await contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "Army"));
        var contract = await contracts.CreateContractAsync(new SaveContractRequest(customer.Id, "W56-0001", "Support", ContractKind.LaborHour,
            ContractState.Active, true, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 30), 900_000m, 300_000m));
        await projects.CreateProjectAsync(new SaveProjectRequest("P-1", "Support", contract.Id, null, ProjectState.Active, null, null));
        var funding = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Funding", WidgetType.ContractFunding, 6, WidgetHeight.Medium,
            new WidgetConfig(ContractId: contract.Id)));

        var data = new WidgetDataService(dashboards, new FilteringSheetService(sheets, hidden.Id), items,
            new DependencyService(_db), contracts, projects, new ProjectFinancialsService(_db, _clock), new AllowAllFinancialAccess(), _clock);

        var metric = await data.GetAsync(dashboard.Id, open.Id);
        Assert.Equal((1m, "Tracker", tracker.Id), (metric.Metric!.Value, metric.SheetName, metric.SheetId));
        Assert.Contains("scheduling", (await data.GetAsync(dashboard.Id, health.Id)).Error);
        Assert.Contains("isn't shared with you", (await data.GetAsync(dashboard.Id, secret.Id)).Error);
        Assert.Equal("# Hi", (await data.GetAsync(dashboard.Id, text.Id)).Markdown);
        var f = (await data.GetAsync(dashboard.Id, funding.Id)).ContractFunding!;
        Assert.Equal((900_000m, 300_000m, 77, "Active"), (f.CeilingValue, f.FundedValue, f.DaysLeft, f.Status)); // today is 14 Oct
        Assert.Equal(["P-1"], f.Projects);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => data.GetAsync(dashboard.Id, Guid.NewGuid()));
    }
}

internal sealed class AllowAllFinancialAccess : IFinancialAccessService
{
    public Task<bool> CanReadProjectFinancialsAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> CanReadContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default) => Task.FromResult(true);
}
