using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Customization;
using Workbench.Domain.Customization;
using Dash = Workbench.Contracts.Dashboards;
using Model = Workbench.Domain.Dashboards;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for screen customization: a "My open projects" view on the Projects list shared with everyone
/// (projects you're on that aren't closed), and a Finance home page (KPI tiles, the to-dos, shortcuts, an announcement and
/// a chart of that view). Dana Lee (dev@, Admin) owns both. Form layouts are left as built.
/// </summary>
internal static class CustomizationSeed
{
    public const string OpenProjectsView = "My open projects";
    private const string Owner = "dev@workbench.local";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<SavedListView>().AnyAsync(v => v.Name == OpenProjectsView, cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var view = new SavedListView
        {
            ListKey = CustomizableLists.Projects,
            Name = OpenProjectsView,
            OwnerEmail = Owner,
            Visibility = Domain.Customization.ListViewVisibility.Everyone,
            DefinitionJson = JsonSerializer.Serialize(new ListViewDefinition(
                ["project", "manager", "contract", "status", "period"],
                [new ListSort("project")],
                [new ListFilterCondition("mine", ListFilterOperator.Equals, "true"), new ListFilterCondition("status", ListFilterOperator.NotEquals, "Closed")],
                null), Json),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Add(view);

        var home = new Model.Dashboard
        {
            Name = "Home · Finance",
            Description = "Role home page",
            OwnerEmail = Owner,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        void Add(Model.WidgetType type, string title, int width, Dash.WidgetConfig config) =>
            home.Widgets.Add(new Model.Widget
            {
                DashboardId = home.Id, Type = type, Title = title, Order = home.Widgets.Count, Width = width,
                Height = Model.WidgetHeight.Medium, ConfigJson = JsonSerializer.Serialize(config, Json),
            });
        Add(Model.WidgetType.Announcement, "Month-end close", 12, new(Markdown: "October close starts on the **3rd business day**: approve timesheets and AP bills by then. Questions? Ask Finance.", Pinned: true));
        Add(Model.WidgetType.Kpi, "Funding remaining", 3, new(Kpi: Dash.KpiKind.FundingRemaining, Pinned: true));
        Add(Model.WidgetType.Kpi, "AR aging", 3, new(Kpi: Dash.KpiKind.ArAging));
        Add(Model.WidgetType.Kpi, "Open POs", 3, new(Kpi: Dash.KpiKind.OpenPurchaseOrders));
        Add(Model.WidgetType.Kpi, "Utilization", 3, new(Kpi: Dash.KpiKind.Utilization));
        Add(Model.WidgetType.MyTodo, "To do", 8, new(Pinned: true));
        Add(Model.WidgetType.WaitingOnYou, "Waiting on you", 4, new());
        Add(Model.WidgetType.Shortcuts, "Shortcuts", 8, new(Shortcuts:
        [
            new("New bill", "purchasing/bills/new", "add"),
            new("New invoice", "billing/new", "add"),
            new("Period close", "accounting/close"),
            new("Approvals", "approvals", "approve"),
            new("Reports", "reports", "report"),
        ]));
        Add(Model.WidgetType.ThisWeek, "This week", 4, new());
        Add(Model.WidgetType.ListViewChart, "My open projects by manager", 6, new(Chart: Dash.ChartKind.Bar, ListViewId: view.Id));
        db.Add(home);
        db.Add(new RoleHomePage { RoleKey = "Finance", DashboardId = home.Id });
        await db.SaveChangesAsync(cancellationToken);
    }
}
