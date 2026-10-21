using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Accessibility;
using Workbench.Tests.Mobile;
using Workbench.Ui.Customization;
using Workbench.Ui.Pages;
using Workbench.Ui.Setup;
using static Workbench.Tests.MyTodo.MyTodoKit;
using E = Workbench.Contracts.Customization.CustomizableEntities;

namespace Workbench.Tests.Customization;

public sealed class ConfigurableComponentTests
{
    private static readonly string[] EmployeeLabels =
        ["Employee number", "Name", "Email", "Org unit", "Supervisor", "Default labor category", "Standard hours per week", "Hire date", "Termination date", "Exempt (salaried, FLSA)"];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<EmployeeEditor> Editor(BunitContext ctx) =>
        ctx.Render<EmployeeEditor>(p => p
            .Add(e => e.Employee, new EmployeeDto(Guid.NewGuid(), "E1150", "val@example.com", "Val Chen", null, null, null, new DateOnly(2024, 1, 1), null, 40m, true))
            .Add(e => e.Save, _ => Task.CompletedTask));

    private static string[] FieldKeys(IRenderedComponent<EmployeeEditor> editor) =>
        editor.FindAll("[data-form-field]").Select(e => e.GetAttribute("data-form-field")!).ToArray();

    [Fact]
    public async Task Without_a_layout_the_form_renders_exactly_as_built()
    {
        await using var bare = NewContext();
        var asBuilt = Editor(bare);
        Assert.Equal(FormLayoutTests.PagesAsBuilt.Single(r => (string)r[0] == E.Employee)[1], FieldKeys(asBuilt));

        // With the built-in layout from the server, nothing moves either.
        await using var ctx = NewContext();
        ctx.Services.AddSingleton<IFormLayoutService>(new FakeLayouts(BuiltIn(E.Employee)));
        var editor = Editor(ctx);
        editor.WaitForAssertion(() => Assert.Equal(FieldKeys(asBuilt), FieldKeys(editor)));
        Assert.Equal(EmployeeLabels, editor.FindAll("[data-form-field] label").Select(l => l.TextContent.Trim()));
        Assert.Empty(editor.FindAll("[data-form-section]"));
        A11yAssert.Check(editor);
    }

    [Fact]
    public async Task A_layout_reorders_renames_hides_and_requires_fields()
    {
        await using var ctx = NewContext();
        var layout = new EffectiveFormLayout(E.Employee, Guid.NewGuid(), "Finance",
        [
            new("dates", "Employment dates", [Field("hireDate", "Hire date", domain: true), Field("terminationDate", "Termination date")]),
            new("details", "The person", [Field("displayName", "Name", domain: true), Field("employeeNumber", "Employee number", domain: true), Field("email", "Email", domain: true),
                Field("orgUnitId", "Org unit", required: true), Field("isExempt", "Exempt (salaried, FLSA)")]),
        ], ["supervisorId", "defaultLaborCategoryId", "standardHoursPerWeek"]);
        ctx.Services.AddSingleton<IFormLayoutService>(new FakeLayouts(layout));

        var editor = Editor(ctx);

        editor.WaitForAssertion(() => Assert.Equal(["hireDate", "terminationDate", "displayName", "employeeNumber", "email", "orgUnitId", "isExempt"], FieldKeys(editor)));
        Assert.Equal(["Employment dates", "The person"], editor.FindAll("[data-form-section]").Select(h => h.TextContent.Trim()));
        Assert.Empty(editor.FindAll("[data-testid=employee-supervisor]"));
        Assert.Equal("true", editor.Find("[data-form-field=orgUnitId] input").GetAttribute("aria-required")); // made required by the layout
        Assert.NotEqual("true", editor.Find("[data-form-field=terminationDate] input").GetAttribute("aria-required"));
        A11yAssert.Check(editor);
    }

    [Fact]
    public async Task Custom_fields_render_and_save_through_the_field_catalog()
    {
        await using var ctx = NewContext();
        var layout = BuiltIn(E.Employee) with
        {
            Sections = [.. BuiltIn(E.Employee).Sections.Select(s => s with { Fields = [.. s.Fields, new EffectiveFormField("cf_badge", "Badge number", "Text", true, false, true, null)] })],
        };
        var catalog = new FakeCatalog();
        ctx.Services.AddSingleton<IFormLayoutService>(new FakeLayouts(layout));
        ctx.Services.AddSingleton<IEntityFieldCatalog>(catalog);
        var editor = Editor(ctx);

        editor.WaitForAssertion(() => Assert.NotNull(editor.Find("[data-form-field=cf_badge] input")));
        Assert.Equal("cf_badge", FieldKeys(editor)[^1]); // after the built-in fields
        editor.Find("[data-form-field=cf_badge] input").Change("B-1234");
        await editor.InvokeAsync(() => editor.Instance.SaveCustomFieldsAsync(Guid.Empty));
        Assert.Equal(("cf_badge", "B-1234"), catalog.Saved.Single());
    }

    private sealed record Row(Guid Id, string Code, decimal Cost);

    private static readonly ListDefinition<Row> Definition = new()
    {
        ListKey = CustomizableLists.Projects,
        EntityType = E.Project,
        Title = "Projects",
        Id = r => r.Id,
        Columns =
        [
            new() { Key = "code", Title = "Code", Value = r => r.Code },
            new() { Key = "cost", Title = "Labor cost", Value = r => r.Cost, Kind = ListColumnKind.Money, PermissionKey = "labor.cost.view" },
        ],
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_list_offers_only_the_columns_the_viewer_may_see(bool canSeeCost)
    {
        await using var ctx = NewContext();
        ctx.Services.AddSingleton<ICustomizationAccess>(new FakeAccess(canSeeCost ? ["labor.cost.view"] : []));
        var views = new FakeViews();
        ctx.Services.AddSingleton<IListViewService>(views);
        await ctx.Render<MudBlazor.MudPopoverProvider>().InvokeAsync(() => { });

        var list = ctx.Render<ConfigurableList<Row>>(p => p
            .Add(l => l.Definition, Definition)
            .Add(l => l.Items, [new Row(Guid.NewGuid(), "1002", 20m), new Row(Guid.NewGuid(), "1001", 10m)])
            .Add(l => l.TestId, "list"));

        list.WaitForAssertion(() => Assert.Equal(2, list.FindAll("[data-testid=list] tbody tr").Count));
        Assert.Equal(canSeeCost, list.Markup.Contains("Labor cost"));
        Assert.Equal(canSeeCost, list.Markup.Contains("$20"));
        Assert.Equal(canSeeCost ? ["code", "cost"] : ["code"], list.Instance.VisibleColumns.Select(c => c.Key));

        // Sorting from the header reorders the rows the screen (and the export) shows.
        list.Find("[data-sort=code]").Click();
        Assert.Equal(["1001", "1002"], list.Instance.Rows.Select(r => r.Code));
        A11yAssert.Check(list);
    }

    [Fact]
    public async Task The_home_page_shows_the_role_layout_and_the_built_in_page_otherwise()
    {
        await using var ctx = NewContext();
        var (todo, todoFake) = Fake<IMyTodoService>.Create();
        todoFake.On(nameof(IMyTodoService.GetAsync), _ => new MyTodoDto("Pat Morgan", Today, [], null, null, []));
        ctx.Services.AddSingleton(todo);
        var (myWork, mf) = Fake<IMyWorkService>.Create();
        mf.Returns(nameof(IMyWorkService.GetAsync), new MyWorkDto("pm@example.com", Today, Today.AddDays(4), new MyWorkCounts(0, 0, 0, 0), []));
        ctx.Services.AddSingleton(myWork);
        ctx.Services.AddSingleton(Fake<IItemService>.Create().Service);

        var dashboard = Guid.NewGuid();
        WidgetDto Widget(WidgetType type, string title, int order, WidgetConfig? config = null) =>
            new(Guid.NewGuid(), dashboard, type, title, order, type == WidgetType.Kpi ? 3 : 12, WidgetHeight.Small, config ?? new WidgetConfig());
        var kpi = Widget(WidgetType.Kpi, "AR aging", 1, new WidgetConfig(Kpi: KpiKind.ArAging));
        var page = new HomePageDto(dashboard, "Finance", "Finance",
        [
            new(Widget(WidgetType.Announcement, "Month-end close", 0, new WidgetConfig(Markdown: "Close on **Friday**")), true, false),
            new(kpi, false, false),
            new(Widget(WidgetType.Shortcuts, "Shortcuts", 2, new WidgetConfig(Shortcuts: [new WidgetShortcut("New bill", "purchasing/bills/new")])), false, false),
            new(Widget(WidgetType.MySheetItems, "My sheet items", 3), false, true),
        ], false, false);
        var homes = new FakeHomes(page, new WidgetDataDto(kpi.Id, WidgetType.Kpi, null, null, null,
            Kpi: new KpiData(KpiKind.ArAging, "Open receivables", 125000m, true, false, "8% more than 90 days past due", "billing/aging")));
        ctx.Services.AddSingleton<IHomePageService>(homes);

        var home = ctx.Render<Home>();
        home.WaitForAssertion(() => Assert.Contains("$125,000", home.Markup));
        Assert.Equal(["Month-end close", "AR aging", "Shortcuts"], home.FindAll("[data-home-widget]").Select(w => w.GetAttribute("data-home-widget")));
        Assert.Contains("<strong>Friday</strong>", home.Markup);
        Assert.Contains("purchasing/bills/new", home.Markup);
        Assert.DoesNotContain("My sheet items", home.Markup); // hidden by this person

        // Built-in and untouched: the page as it always was.
        homes.Page = new HomePageDto(null, null, "Built-in", [], false, false);
        var builtIn = ctx.Render<Home>();
        builtIn.WaitForAssertion(() => Assert.Contains("To do", builtIn.Markup));
        Assert.Empty(builtIn.FindAll("[data-testid=home-widgets]"));
        Assert.Contains("My sheet items", builtIn.Markup);
    }

    private static EffectiveFormField Field(string key, string label, bool domain = false, bool required = false) =>
        new(key, label, "Text", false, domain, domain || required, null);

    private static EffectiveFormLayout BuiltIn(string entityType)
    {
        var form = Workbench.Application.Customization.BuiltInForms.Get(entityType);
        return new EffectiveFormLayout(entityType, null, "Built-in",
            form.Sections.Select(s => new EffectiveFormSection(s.Key, s.Title, s.Fields.Select(f => new EffectiveFormField(f.Key, f.Label, f.Type, false, f.Required, f.Required, null)).ToList())).ToList(),
            []);
    }

    private sealed class FakeLayouts(EffectiveFormLayout layout) : IFormLayoutService
    {
        public Task<EffectiveFormLayout> GetEffectiveAsync(string entityType, CancellationToken ct = default) => Task.FromResult(layout);

        public Task<IReadOnlyList<FormEntityTypeDto>> ListEntityTypesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntityFieldDto>> ListFieldsAsync(string entityType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FormLayoutDto> GetBuiltInAsync(string entityType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FormLayoutDto>> ListAsync(string entityType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EffectiveFormLayout> PreviewAsync(PreviewFormLayoutRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FormLayoutDto> SaveAsync(SaveFormLayoutRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid layoutId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeCatalog : IEntityFieldCatalog
    {
        public List<(string Key, object? Value)> Saved { get; } = [];

        public IReadOnlyList<string> EntityTypes => [E.Employee];

        public Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EntityFieldDto>>([new(entityType, "cf_badge", "Badge number", "Text", true, false, null, null)]);

        public Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default) => Task.FromResult<object?>(null);

        public Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default)
        {
            Saved.Add((fieldKey, value));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccess(IReadOnlyList<string> granted) : ICustomizationAccess
    {
        public Task<CustomizationCaller> GetCallerAsync(CancellationToken ct = default) =>
            Task.FromResult(new CustomizationCaller("val@example.com", [CustomizationRoles.Everyone], null, false, false));

        public Task<IReadOnlyList<CustomizationRole>> ListRolesAsync(CancellationToken ct = default) => Task.FromResult(CustomizationRoles.BuiltInRoles);

        public Task<IReadOnlySet<string>> GrantedAsync(IEnumerable<string> keys, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(keys.Where(granted.Contains).ToHashSet());

        public Task<IReadOnlySet<string>> GrantedToRoleAsync(string roleKey, IEnumerable<string> keys, CancellationToken ct = default) => GrantedAsync(keys, ct);
    }

    private sealed class FakeViews : IListViewService
    {
        public Task<ListViewsDto> ListAsync(string listKey, CancellationToken ct = default) => Task.FromResult(new ListViewsDto(listKey, [], null, false));

        public Task<IReadOnlyList<SavedListViewDto>> ListAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SavedListViewDto>>([]);
        public Task<SavedListViewDto> GetAsync(Guid viewId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedListViewDto> CreateAsync(SaveListViewRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedListViewDto> UpdateAsync(Guid viewId, SaveListViewRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid viewId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetDefaultAsync(string listKey, SetDefaultListViewRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReportFileDto> ExportAsync(ListExportRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeHomes(HomePageDto page, WidgetDataDto data) : IHomePageService
    {
        public HomePageDto Page { get; set; } = page;

        public Task<HomePageDto> GetMineAsync(CancellationToken ct = default) => Task.FromResult(Page);

        public Task<WidgetDataDto> GetWidgetDataAsync(Guid widgetId, CancellationToken ct = default) => Task.FromResult(data);

        public Task<HomePageDto> SavePreferencesAsync(SaveHomePreferencesRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePageDto> ResetPreferencesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RoleHomePageSummary>> ListRoleHomePagesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePageDto> GetRoleHomePageAsync(string roleKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePageDto> PreviewAsync(string roleKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePageDto> CreateRoleHomePageAsync(string roleKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRoleHomePageAsync(string roleKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WidgetDto> AddWidgetAsync(string roleKey, SaveWidgetRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WidgetDto> UpdateWidgetAsync(string roleKey, Guid widgetId, SaveWidgetRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteWidgetAsync(string roleKey, Guid widgetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePageDto> ReorderWidgetsAsync(string roleKey, ReorderWidgetsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
