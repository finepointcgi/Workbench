using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Dashboards;
using Model = Workbench.Domain.Dashboards;

namespace Workbench.Application.Dashboards;

/// <summary>
/// Dashboards and their widgets. Every dashboard is listed for everyone for now.
/// TODO: once sharing exists, list only dashboards the user owns or that are shared with them. Widget data is already
/// limited to the user's sheets because <see cref="WidgetDataService"/> reads through the sheet contracts.
/// </summary>
public sealed class DashboardService(IWorkbenchDbContextFactory dbFactory, ICurrentUser user, TimeProvider clock) : IDashboardService
{
    internal static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        // Role home pages are dashboards too, but they're managed from Admin › Customization, not listed here.
        var dashboards = await db.Set<Model.Dashboard>()
            .AsNoTracking()
            .Where(d => !db.Set<Domain.Customization.RoleHomePage>().Any(h => h.DashboardId == d.Id))
            .OrderByDescending(d => d.UpdatedAtUtc)
            .Select(d => new DashboardSummary(d.Id, d.Name, d.Description, d.OwnerEmail, d.WorkspaceId, d.Widgets.Count,
                d.CreatedAtUtc, d.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        return dashboards;
    }

    public async Task<DashboardDto> GetAsync(Guid dashboardId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await db.Set<Model.Dashboard>()
            .AsNoTracking()
            .Include(d => d.Widgets)
            .FirstOrDefaultAsync(d => d.Id == dashboardId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Dashboard", dashboardId);
        return ToDto(dashboard);
    }

    public async Task<DashboardSummary> CreateAsync(CreateDashboardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = Now();
        var dashboard = new Model.Dashboard
        {
            Name = request.Name.Trim(),
            Description = Clean(request.Description),
            OwnerEmail = user.Email,
            WorkspaceId = request.WorkspaceId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Set<Model.Dashboard>().Add(dashboard);
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(dashboard);
    }

    public async Task<DashboardSummary> UpdateAsync(Guid dashboardId, UpdateDashboardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await FindAsync(db, dashboardId, cancellationToken);
        dashboard.Name = request.Name.Trim();
        dashboard.Description = Clean(request.Description);
        dashboard.UpdatedAtUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(dashboard);
    }

    public async Task DeleteAsync(Guid dashboardId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await FindAsync(db, dashboardId, cancellationToken);
        db.Set<Model.Widget>().RemoveRange(dashboard.Widgets);
        db.Set<Model.Dashboard>().Remove(dashboard);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WidgetDto> AddWidgetAsync(Guid dashboardId, SaveWidgetRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await FindAsync(db, dashboardId, cancellationToken);
        var widget = new Model.Widget
        {
            DashboardId = dashboardId,
            Title = request.Title.Trim(),
            Order = dashboard.Widgets.Count == 0 ? 0 : dashboard.Widgets.Max(w => w.Order) + 1,
        };
        Apply(widget, request);
        db.Set<Model.Widget>().Add(widget);
        dashboard.UpdatedAtUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(widget);
    }

    public async Task<WidgetDto> UpdateWidgetAsync(Guid dashboardId, Guid widgetId, SaveWidgetRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await FindAsync(db, dashboardId, cancellationToken);
        var widget = FindWidget(dashboard, widgetId);
        Apply(widget, request);
        dashboard.UpdatedAtUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(widget);
    }

    public async Task DeleteWidgetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await FindAsync(db, dashboardId, cancellationToken);
        var widget = FindWidget(dashboard, widgetId);
        db.Set<Model.Widget>().Remove(widget);
        var order = 0;
        foreach (var remaining in dashboard.Widgets.Where(w => w.Id != widgetId).OrderBy(w => w.Order))
        {
            remaining.Order = order++;
        }

        dashboard.UpdatedAtUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DashboardDto> ReorderWidgetsAsync(Guid dashboardId, ReorderWidgetsRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dashboard = await FindAsync(db, dashboardId, cancellationToken);
        var ids = request.WidgetIds ?? [];
        if (ids.Count != dashboard.Widgets.Count || ids.Distinct().Count() != ids.Count
            || !ids.All(id => dashboard.Widgets.Any(w => w.Id == id)))
        {
            throw new ArgumentException("List every widget on the dashboard exactly once.", nameof(request));
        }

        for (var i = 0; i < ids.Count; i++)
        {
            dashboard.Widgets.First(w => w.Id == ids[i]).Order = i;
        }

        dashboard.UpdatedAtUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(dashboard);
    }

    internal static WidgetConfig ReadConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WidgetConfig>(json, ConfigJson) ?? new WidgetConfig();
        }
        catch (JsonException)
        {
            return new WidgetConfig();
        }
    }

    internal static string WriteConfig(WidgetConfig config) => JsonSerializer.Serialize(config, ConfigJson);

    private static void Validate(SaveWidgetRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentNullException.ThrowIfNull(request.Config);
        if (!Enum.IsDefined(request.Type) || !Enum.IsDefined(request.Height))
        {
            throw new ArgumentException("Unknown widget type or height.", nameof(request));
        }

        if (request.Width is < 1 or > 12)
        {
            throw new ArgumentException("Widget width must be between 1 and 12 columns.", nameof(request));
        }

        switch (request.Type)
        {
            case WidgetType.RichText:
            case WidgetType.MyTodo:
            case WidgetType.ThisWeek:
            case WidgetType.WaitingOnYou:
            case WidgetType.MySheetItems:
            case WidgetType.Announcement:
                break;
            case WidgetType.Kpi when !(request.Config.Kpi is { } kpi && Enum.IsDefined(kpi)):
                throw new ArgumentException("A KPI widget needs the figure to show.", nameof(request));
            case WidgetType.ReportChart when request.Config.SavedReportId is null:
                throw new ArgumentException("A report chart needs a saved report.", nameof(request));
            case WidgetType.ListViewChart when request.Config.ListViewId is null:
                throw new ArgumentException("A list view chart needs a saved list view.", nameof(request));
            case WidgetType.Shortcuts:
                ValidateShortcuts(request.Config.Shortcuts);
                break;
            case WidgetType.Kpi or WidgetType.ReportChart or WidgetType.ListViewChart:
                break;
            case WidgetType.ContractFunding when request.Config.ContractId is null:
                throw new ArgumentException("A contracts and funding widget needs a contract.", nameof(request));
            case WidgetType.ContractFunding:
                break;
            case var _ when request.Config.SheetId is null:
                throw new ArgumentException($"A {request.Type} widget needs a sheet.", nameof(request));
        }

        if (request.Config.MaxItems is < 1 or > 100)
        {
            throw new ArgumentException("An item list shows between 1 and 100 items.", nameof(request));
        }
    }

    private static void ValidateShortcuts(IReadOnlyList<WidgetShortcut>? shortcuts)
    {
        if (shortcuts is null || shortcuts.Count is 0 or > 20)
        {
            throw new ArgumentException("A shortcuts widget has between 1 and 20 links.", nameof(shortcuts));
        }

        foreach (var link in shortcuts)
        {
            var href = link.Href?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(link.Label) || href.Contains(':', StringComparison.Ordinal) || href.StartsWith("//", StringComparison.Ordinal))
            {
                throw new ArgumentException("Each shortcut needs a label and a Workbench page (like time or purchasing/bills/new).", nameof(shortcuts));
            }
        }
    }

    private static void Apply(Model.Widget widget, SaveWidgetRequest request)
    {
        widget.Title = request.Title.Trim();
        widget.Type = (Model.WidgetType)request.Type;
        widget.Width = request.Width;
        widget.Height = (Model.WidgetHeight)request.Height;
        widget.ConfigJson = WriteConfig(request.Config);
    }

    private static async Task<Model.Dashboard> FindAsync(IWorkbenchDbContext db, Guid dashboardId, CancellationToken cancellationToken) =>
        await db.Set<Model.Dashboard>().Include(d => d.Widgets).FirstOrDefaultAsync(d => d.Id == dashboardId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Dashboard", dashboardId);

    private static Model.Widget FindWidget(Model.Dashboard dashboard, Guid widgetId) =>
        dashboard.Widgets.FirstOrDefault(w => w.Id == widgetId) ?? throw WorkbenchNotFoundException.For("Widget", widgetId);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static DashboardSummary ToSummary(Model.Dashboard d) =>
        new(d.Id, d.Name, d.Description, d.OwnerEmail, d.WorkspaceId, d.Widgets.Count, d.CreatedAtUtc, d.UpdatedAtUtc);

    private static DashboardDto ToDto(Model.Dashboard d) =>
        new(ToSummary(d), d.Widgets.OrderBy(w => w.Order).Select(ToDto).ToList());

    internal static WidgetDto ToDto(Model.Widget w) =>
        new(w.Id, w.DashboardId, (WidgetType)w.Type, w.Title, w.Order, w.Width, (WidgetHeight)w.Height, ReadConfig(w.ConfigJson));
}
