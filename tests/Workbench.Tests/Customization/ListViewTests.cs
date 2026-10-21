using System.Text;
using ClosedXML.Excel;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Reports;
using Workbench.Tests.Identity;
using Workbench.Ui.Customization;
using L = Workbench.Contracts.Customization.CustomizableLists;

namespace Workbench.Tests.Customization;

public sealed class ListViewTests
{
    private static SaveListViewRequest View(string name, ListViewVisibility visibility = ListViewVisibility.Personal, string? role = null, string list = L.Projects) =>
        new(list, name, visibility, role, new ListViewDefinition(["project", "manager"], [new ListSort("project")], [new ListFilterCondition("status", ListFilterOperator.NotEquals, "Closed")]));

    [Fact]
    public async Task Saved_views_are_personal_shared_with_a_role_or_shared_with_everyone()
    {
        await using var host = await SeededHost.CreateAsync();
        var admin = host.As(SeededHost.Admin).Host.Get<IListViewService>();
        await admin.CreateAsync(View("Admin's own"));
        await admin.CreateAsync(View("For Finance", ListViewVisibility.Role, "Finance"));
        await admin.CreateAsync(View("For everyone", ListViewVisibility.Everyone));
        await admin.CreateAsync(View("Other list", ListViewVisibility.Everyone, list: L.Bills));
        await host.As(SeededHost.Viewer).Host.Get<IListViewService>().CreateAsync(View("Val's own"));

        async Task<string[]> NamesFor(string email) =>
            (await host.As(email).Host.Get<IListViewService>().ListAsync(L.Projects)).Views.Select(v => v.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["Admin's own", "For Finance", "For everyone"], await NamesFor(SeededHost.Admin)); // Admin isn't Finance; they see it as its owner
        Assert.Equal(["For Finance", "For everyone"], await NamesFor(SeededHost.Finance));
        Assert.Equal(["For everyone", "Val's own"], await NamesFor(SeededHost.Viewer));
        Assert.Equal(["For everyone"], await NamesFor(SeededHost.Timekeeper));

        var shared = (await host.As(SeededHost.Viewer).Host.Get<IListViewService>().ListAsync(L.Projects)).Views.Single(v => v.Name == "For everyone");
        Assert.False(shared.CanEdit);
        Assert.Equal(["project", "manager"], shared.Definition.Columns);
        Assert.Equal(ListFilterOperator.NotEquals, Assert.Single(shared.Definition.Filters!).Operator);
    }

    [Fact]
    public async Task Sharing_takes_permission_and_only_owners_change_personal_views()
    {
        await using var host = await SeededHost.CreateAsync();
        var finance = host.As(SeededHost.Finance).Host.Get<IListViewService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.CreateAsync(View("Shared", ListViewVisibility.Everyone)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.CreateAsync(View("Shared", ListViewVisibility.Role, "Finance")));
        var mine = await finance.CreateAsync(View("Mine"));
        Assert.True(mine.CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.UpdateAsync(mine.Id, View("Now shared", ListViewVisibility.Everyone)));

        // Someone else's personal view isn't even there for them.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(SeededHost.Viewer).Host.Get<IListViewService>().DeleteAsync(mine.Id));

        // A shared view is changed by those who may share views, not by everyone who sees it.
        var everyone = await host.As(SeededHost.Admin).Host.Get<IListViewService>().CreateAsync(View("Everyone's", ListViewVisibility.Everyone));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(SeededHost.Viewer).Host.Get<IListViewService>().DeleteAsync(everyone.Id));
        var renamed = await host.As(SeededHost.Admin).Host.Get<IListViewService>().UpdateAsync(everyone.Id, View("Everyone's, renamed", ListViewVisibility.Everyone));
        Assert.Equal("Everyone's, renamed", renamed.Name);

        await Assert.ThrowsAsync<ArgumentException>(() => finance.CreateAsync(View("Bad list", list: "nope")));
        await Assert.ThrowsAsync<ArgumentException>(() => finance.CreateAsync(View(" ")));
    }

    [Fact]
    public async Task Each_person_picks_their_own_default_view()
    {
        await using var host = await SeededHost.CreateAsync();
        var shared = await host.As(SeededHost.Admin).Host.Get<IListViewService>().CreateAsync(View("Open", ListViewVisibility.Everyone));

        var viewer = host.As(SeededHost.Viewer).Host.Get<IListViewService>();
        await viewer.SetDefaultAsync(L.Projects, new SetDefaultListViewRequest(shared.Id));
        Assert.Equal(shared.Id, (await viewer.ListAsync(L.Projects)).DefaultViewId);
        Assert.Null((await host.As(SeededHost.Finance).Host.Get<IListViewService>().ListAsync(L.Projects)).DefaultViewId);

        await host.As(SeededHost.Viewer).Host.Get<IListViewService>().SetDefaultAsync(L.Projects, new SetDefaultListViewRequest(null));
        Assert.Null((await host.As(SeededHost.Viewer).Host.Get<IListViewService>().ListAsync(L.Projects)).DefaultViewId);

        var personal = await host.As(SeededHost.Finance).Host.Get<IListViewService>().CreateAsync(View("Fin only"));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(SeededHost.Viewer).Host.Get<IListViewService>().SetDefaultAsync(L.Projects, new SetDefaultListViewRequest(personal.Id)));
    }

    private sealed record Row(Guid Id, string Code, string? Manager, string Status, decimal Amount, DateOnly? Due, decimal Cost);

    private static readonly ListDefinition<Row> Definition = new()
    {
        ListKey = L.Projects,
        EntityType = CustomizableEntities.Project,
        Title = "Projects",
        Id = r => r.Id,
        Columns =
        [
            new() { Key = "code", Title = "Code", Value = r => r.Code },
            new() { Key = "manager", Title = "Manager", Value = r => r.Manager },
            new() { Key = "status", Title = "Status", Value = r => r.Status },
            new() { Key = "amount", Title = "Amount", Value = r => r.Amount, Kind = ListColumnKind.Money },
            new() { Key = "due", Title = "Due", Value = r => r.Due, Kind = ListColumnKind.Date, Standard = false },
            new() { Key = "cost", Title = "Cost", Value = r => r.Cost, Kind = ListColumnKind.Money, PermissionKey = "labor.cost.view" },
        ],
    };

    private static readonly Row[] Rows =
    [
        new(Guid.NewGuid(), "1003", "Pat", "Active", 300m, new DateOnly(2026, 12, 1), 10m),
        new(Guid.NewGuid(), "1001", "Sam", "Closed", 100m, null, 20m),
        new(Guid.NewGuid(), "1002", "Pat", "Active", 200m, new DateOnly(2026, 11, 1), 30m),
        new(Guid.NewGuid(), "1004", null, "Planning", 50m, new DateOnly(2027, 1, 1), 40m),
    ];

    [Fact]
    public void Views_filter_sort_and_group_rows()
    {
        var columns = Definition.Columns;
        string[] Codes(ListViewDefinition view) => ListViewEngine.Apply(Rows, columns, view).Select(r => r.Code).ToArray();

        Assert.Equal(["1003", "1001", "1002", "1004"], Codes(Definition.StandardView)); // no sort: the page's order
        Assert.Equal(["1001", "1002", "1003", "1004"], Codes(new(["code"], [new ListSort("code")])));
        Assert.Equal(["1003", "1002", "1001", "1004"], Codes(new(["code"], [new ListSort("amount", Descending: true)])));
        Assert.Equal(["1003", "1002", "1004"], Codes(new(["code"], [], [new ListFilterCondition("status", ListFilterOperator.NotEquals, "closed")])));
        Assert.Equal(["1003", "1002"], Codes(new(["code"], [], [new("manager", ListFilterOperator.Equals, "pat"), new("amount", ListFilterOperator.GreaterOrEqual, "200")])));
        Assert.Equal(["1004"], Codes(new(["code"], [], [new ListFilterCondition("manager", ListFilterOperator.IsEmpty)])));
        Assert.Equal(["1003", "1004"], Codes(new(["code"], [], [new ListFilterCondition("due", ListFilterOperator.GreaterThan, "2026-11-15")])));
        Assert.Equal(["1003", "1002"], Codes(new(["code", "manager"], [], [], Search: "Pat")));
        Assert.Empty(Codes(new(["code"], [], [], Search: "Pat"))); // the search looks at the columns shown

        // Grouped by manager (blank last), sorted by code within each group.
        Assert.Equal(["1002", "1003", "1001", "1004"], Codes(new(["code"], [new ListSort("code")], [], GroupBy: "manager")));

        // Columns, sorts and filters on columns that aren't offered are ignored.
        Assert.Equal(["1003", "1001", "1002", "1004"], Codes(new(["code"], [new ListSort("secret")], [new ListFilterCondition("secret", ListFilterOperator.IsEmpty)], "secret")));
        Assert.Equal(["code", "manager", "status", "amount", "cost"], ListViewEngine.Visible(columns, new(["nope"])).Select(c => c.Key)); // standard columns
    }

    [Fact]
    public async Task The_export_matches_the_view_exactly()
    {
        var view = new ListViewDefinition(["amount", "code", "due"], [new ListSort("amount", true)], [new ListFilterCondition("status", ListFilterOperator.Equals, "Active")]);
        var rows = ListViewEngine.Apply(Rows, Definition.Columns, view);
        var columns = ListViewEngine.Visible(Definition.Columns, view);
        var request = ListViewEngine.Export(L.Projects, "Projects - Active", rows, columns, ReportFileFormat.Csv);

        Assert.Equal(["Amount", "Code", "Due"], request.Columns.Select(c => c.Label));
        Assert.Equal([ReportColumnType.Money, ReportColumnType.Text, ReportColumnType.Date], request.Columns.Select(c => c.Type));
        Assert.Equal([["300", "1003", "2026-12-01"], ["200", "1002", "2026-11-01"]], request.Rows.Select(r => r.ToArray()));

        await using var host = await SeededHost.CreateAsync();
        var service = host.As(SeededHost.Viewer).Host.Get<IListViewService>();
        var csv = await service.ExportAsync(request);
        var lines = Encoding.UTF8.GetString(csv.Content).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Amount,Code,Due", lines[0]);
        Assert.Equal(["300,1003,2026-12-01", "200,1002,2026-11-01"], lines.Skip(1));

        var xlsx = await service.ExportAsync(request with { Format = ReportFileFormat.Xlsx });
        using var workbook = new XLWorkbook(new MemoryStream(xlsx.Content));
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed()!;
        Assert.Contains(used.Cells(), c => c.GetString() == "1003");
        Assert.Contains(used.Cells(), c => c.GetString() == "1002");
        Assert.DoesNotContain(used.Cells(), c => c.GetString() is "1001" or "1004");
    }

    [Fact]
    public async Task Columns_a_person_cannot_see_are_not_offered()
    {
        await using var host = await SeededHost.CreateAsync();
        var employee = await ListPermissions.AvailableAsync(host.As(SeededHost.Viewer).Host.Get<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().CreateScope().ServiceProvider, Definition);
        Assert.DoesNotContain(employee, c => c.Key == "cost");

        var finance = await ListPermissions.AvailableAsync(ScopeFor(host, SeededHost.Finance), Definition);
        Assert.Contains(finance, c => c.Key == "cost");

        // A saved view that names the column shows the rest of its columns to someone who can't see it.
        var view = new ListViewDefinition(["code", "cost"], [new ListSort("cost")]);
        Assert.Equal(["code"], ListViewEngine.Visible(employee, view).Select(c => c.Key));
        Assert.Empty(ListViewEngine.Restrict(employee, view).Sort!);
    }

    private static IServiceProvider ScopeFor(SeededHost host, string email)
    {
        host.As(email);
        return host.Host.Get<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().CreateScope().ServiceProvider;
    }
}
