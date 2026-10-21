using Workbench.ApiClient;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Reports;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Customization;

/// <summary>The customization endpoints and HTTP clients end to end on the Development web host and its seed.</summary>
public sealed class CustomizationApiTests
{
    [Fact]
    public async Task Layouts_views_and_home_pages_round_trip_through_the_api_client()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var admin = factory.CreateClientAs("dev@workbench.local");
        using var finance = factory.CreateClientAs("pm@example.com");
        using var employee = factory.CreateClientAs("analyst@example.com");

        // Who is asking, and the field permission seam.
        Assert.True((await new HttpCustomizationAccess(admin).GetCallerAsync()).CanManageLayouts);
        var pat = await new HttpCustomizationAccess(finance).GetCallerAsync();
        Assert.Equal(["Finance", "ProjectManager", CustomizationRoles.Everyone], pat.RoleKeys);
        Assert.Equal(["labor.cost.view"], await new HttpCustomizationAccess(finance).GrantedAsync(["labor.cost.view", "unknown.key"]));

        // Form layouts: everyone reads, only managers save; the server then enforces the requirement.
        var layouts = new HttpFormLayoutService(admin);
        var builtIn = await layouts.GetBuiltInAsync(CustomizableEntities.Customer);
        await layouts.SaveAsync(new SaveFormLayoutRequest(CustomizableEntities.Customer, null, null,
            [builtIn.Sections[0] with { Title = "Customer", Fields = [new("code"), new("name"), new("category", Required: true)] }]));
        var effective = await new HttpFormLayoutService(employee).GetEffectiveAsync(CustomizableEntities.Customer);
        Assert.Equal("Customer", effective.Sections[0].Title);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => new HttpFormLayoutService(finance).SaveAsync(new SaveFormLayoutRequest(CustomizableEntities.Customer, null, null, builtIn.Sections)));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => new HttpContractClientCustomer(finance).CreateAsync("NOCAT", "No category"));
        Assert.Contains("Category is required", refused.Message);

        // The seeded shared view, a personal one, the default and an export.
        var views = new HttpListViewService(employee);
        var projects = await views.ListAsync(CustomizableLists.Projects);
        var open = Assert.Single(projects.Views, v => v.Name == "My open projects");
        Assert.Equal(ListViewVisibility.Everyone, open.Visibility);
        var mine = await views.CreateAsync(new SaveListViewRequest(CustomizableLists.Projects, "Alex's", ListViewVisibility.Personal, null, new ListViewDefinition(["project"])));
        await views.SetDefaultAsync(CustomizableLists.Projects, new SetDefaultListViewRequest(mine.Id));
        Assert.Equal(mine.Id, (await views.ListAsync(CustomizableLists.Projects)).DefaultViewId);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => views.CreateAsync(new SaveListViewRequest(CustomizableLists.Projects, "Shared", ListViewVisibility.Everyone, null, new ListViewDefinition(["project"]))));
        var file = await views.ExportAsync(new ListExportRequest(CustomizableLists.Projects, "Projects", [new ReportColumnDto("project", "Project", ReportColumnType.Text)], [["1001"]], ReportFileFormat.Csv));
        Assert.Equal("text/csv", file.ContentType);

        // Home pages: the seeded Finance page for Pat, the built-in one for Alex.
        var patHome = await new HttpHomePageService(finance).GetMineAsync();
        Assert.Equal("Finance", patHome.RoleKey);
        var kpi = patHome.Widgets.First(w => w.Widget.Type == WidgetType.Kpi).Widget;
        Assert.NotNull((await new HttpHomePageService(finance).GetWidgetDataAsync(kpi.Id)).Kpi);
        Assert.True((await new HttpHomePageService(employee).GetMineAsync()).IsBuiltIn);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => new HttpHomePageService(employee).ListRoleHomePagesAsync());
        Assert.Contains(await new HttpHomePageService(admin).ListRoleHomePagesAsync(), h => h.RoleKey == "Finance");
    }

    /// <summary>Customers through the regular contract client (the layout's requirement is checked on the server).</summary>
    private sealed class HttpContractClientCustomer(HttpClient http)
    {
        public Task CreateAsync(string code, string name) =>
            new HttpContractService(http).CreateCustomerAsync(new Workbench.Contracts.ProjectAccounting.SaveCustomerRequest(code, name));
    }
}
