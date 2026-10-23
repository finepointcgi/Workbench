using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Identity;

namespace Workbench.Tests.SheetExtras;

public sealed class SheetReportTests
{
    /// <summary>Two Program Office sheets with Status and Owner, and the admins-only Budget sheet with Status too.</summary>
    private static async Task<(SeededHost Host, Guid Risks, Guid Issues)> SheetsAsync()
    {
        var host = await SeededHost.CreateAsync();
        var risks = await host.NewProgramSheetAsync("Risks");
        var riskStatus = await host.AddColumnAsync(risks, "Status", ColumnKind.Dropdown, "Open", "Closed");
        var riskOwner = await host.AddColumnAsync(risks, "Owner", ColumnKind.Person);
        await host.AddItemAsync(risks, "Supplier delay", (riskStatus, "Open"), (riskOwner, "sam@example.com"));
        await host.AddItemAsync(risks, "Old risk", (riskStatus, "Closed"));
        var issues = await host.NewProgramSheetAsync("Issues");
        var issueStatus = await host.AddColumnAsync(issues, "status", ColumnKind.Text);
        var due = await host.AddColumnAsync(issues, "Due", ColumnKind.Date);
        await host.AddItemAsync(issues, "Login fails", (issueStatus, "open"), (due, DateTime.Today.AddDays(3).ToString("yyyy-MM-dd")));
        await host.AddItemAsync(issues, "Slow report", (issueStatus, "Open"), (due, DateTime.Today.AddDays(30).ToString("yyyy-MM-dd")));
        host.As(SeededHost.Admin);
        var budgetStatus = await host.AddColumnAsync(host.FinanceSheetId, "Status", ColumnKind.Text);
        await host.AddItemAsync(host.FinanceSheetId, "Secret line", (budgetStatus, "Open"));
        host.As(SeededHost.Owner);
        return (host, risks, issues);
    }

    private static SaveSheetReportRequest OpenItems(params Guid[] sheets) =>
        new("Open items", sheets, ["Sheet", "Title", "Status", "Owner"], [new SheetReportFilterDto("Status", SheetReportOperator.Is, "Open")]);

    [Fact]
    public async Task Combines_rows_from_several_sheets_matching_columns_by_name()
    {
        var (host, risks, issues) = await SheetsAsync();
        await using var _ = host;

        var report = await host.Reports().CreateAsync(OpenItems(risks, issues));
        var result = await host.Reports().RunAsync(report.Id);

        Assert.Equal(["Supplier delay", "Login fails", "Slow report"], result.Rows.Select(r => r.Title));
        Assert.Equal(["Risks", "Supplier delay", "Open", "sam@example.com"], result.Rows[0].Cells);
        Assert.Equal(ColumnKind.Person, result.Columns.Single(c => c.Name == "Owner").Type);
        Assert.Null(result.Rows[1].Cells[3]); // Issues has no Owner column
        Assert.All(result.Rows, r => Assert.Contains(r.SheetId, new[] { risks, issues }));
        var item = await host.ItemAsync(result.Rows[1].SheetId, result.Rows[1].ItemId);
        Assert.Equal("Login fails", item.Title);
    }

    [Fact]
    public async Task Sheets_the_viewer_cannot_see_are_left_out_and_counted()
    {
        var (host, risks, _) = await SheetsAsync();
        await using var _ = host;
        host.As(SeededHost.Admin);
        var report = await host.Reports().CreateAsync(OpenItems(risks, host.FinanceSheetId) with { SharedWithWorkspaceId = host.ProgramOfficeId });
        Assert.Contains((await host.Reports().RunAsync(report.Id)).Rows, r => r.Title == "Secret line");

        host.As(SeededHost.Viewer);
        var result = await host.Reports().RunAsync(report.Id);

        Assert.Equal(1, result.HiddenSheets);
        Assert.DoesNotContain(result.Rows, r => r.Title == "Secret line");
        Assert.Contains(result.Rows, r => r.Title == "Supplier delay");
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Reports().UpdateAsync(report.Id, OpenItems(risks)));
    }

    [Fact]
    public async Task Private_reports_are_only_for_their_owner()
    {
        var (host, risks, _) = await SheetsAsync();
        await using var _ = host;
        var report = await host.Reports().CreateAsync(OpenItems(risks));

        host.As(SeededHost.Editor);
        Assert.Null(await host.Reports().GetAsync(report.Id));
        Assert.Empty(await host.Reports().ListAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Reports().RunAsync(report.Id));

        host.As(SeededHost.Owner);
        await host.Reports().UpdateAsync(report.Id, OpenItems(risks) with { SharedWithWorkspaceId = host.ProgramOfficeId });
        host.As(SeededHost.Editor);
        Assert.Single(await host.Reports().ListAsync());
        host.As(SeededHost.Outsider);
        Assert.Empty(await host.Reports().ListAsync());
    }

    [Fact]
    public async Task Restricted_columns_are_blank_for_those_who_cannot_see_them()
    {
        var (host, risks, _) = await SheetsAsync();
        await using var _ = host;
        var cost = await host.Host.Columns.CreateAsync(risks, new CreateColumnRequest("Cost", ColumnKind.Currency, IsRestricted: true));
        var supplier = (await host.Host.Items.ListAsync(risks)).Single(i => i.Title == "Supplier delay");
        await host.SetAsync(risks, supplier, cost, "9000");
        var report = await host.Reports().CreateAsync(new SaveSheetReportRequest("Costs", [risks], ["Title", "Cost"], SharedWithWorkspaceId: host.ProgramOfficeId));
        Assert.Equal("9000", (await host.Reports().RunAsync(report.Id)).Rows.Single(r => r.Title == "Supplier delay").Cells[1]);

        host.As(SeededHost.Editor);
        var result = await host.Reports().RunAsync(report.Id);

        Assert.All(result.Rows, r => Assert.Null(r.Cells[1]));
        var filtered = await host.Reports().PreviewAsync(new SaveSheetReportRequest("", [risks], ["Title"], [new SheetReportFilterDto("Cost", SheetReportOperator.GreaterThan, "1")]));
        Assert.Empty(filtered.Rows); // can't filter on what you can't see either
    }

    [Fact]
    public async Task Date_filters_understand_today_and_sorting_works()
    {
        var (host, _, issues) = await SheetsAsync();
        await using var _ = host;

        var soon = await host.Reports().PreviewAsync(new SaveSheetReportRequest("", [issues], ["Title", "Due"],
            [new SheetReportFilterDto("Due", SheetReportOperator.OnOrBefore, "today+7"), new SheetReportFilterDto("Due", SheetReportOperator.OnOrAfter, "today")]));
        Assert.Equal(["Login fails"], soon.Rows.Select(r => r.Title));

        var sorted = await host.Reports().PreviewAsync(new SaveSheetReportRequest("", [issues], ["Title", "Due"], SortBy: "Due", SortDescending: true));
        Assert.Equal(["Slow report", "Login fails"], sorted.Rows.Select(r => r.Title));

        var any = await host.Reports().PreviewAsync(new SaveSheetReportRequest("", [issues], ["Title"],
            [new SheetReportFilterDto("Title", SheetReportOperator.Contains, "login"), new SheetReportFilterDto("Title", SheetReportOperator.Contains, "slow")], MatchAll: false));
        Assert.Equal(2, any.Rows.Count);
    }

    [Fact]
    public async Task Links_in_reports_show_the_linked_rows_titles()
    {
        var (host, risks, issues) = await SheetsAsync();
        await using var _ = host;
        var link = await host.AddColumnAsync(issues, "Related risk", ColumnKind.LinkedRow, risks.ToString());
        var supplier = (await host.Host.Items.ListAsync(risks)).Single(i => i.Title == "Supplier delay");
        var login = (await host.Host.Items.ListAsync(issues)).Single(i => i.Title == "Login fails");
        await host.SetAsync(issues, login, link, supplier.Id.ToString());

        var result = await host.Reports().PreviewAsync(new SaveSheetReportRequest("", [issues], ["Title", "Related risk"]));

        Assert.Equal("Supplier delay", result.Rows.Single(r => r.Title == "Login fails").Cells[1]);
    }

    [Fact]
    public async Task Reports_can_only_name_sheets_their_author_can_see()
    {
        var (host, risks, _) = await SheetsAsync();
        await using var _ = host;

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Reports().CreateAsync(OpenItems(risks, host.FinanceSheetId)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Reports().CreateAsync(OpenItems(risks) with { Name = " " }));
    }
}
