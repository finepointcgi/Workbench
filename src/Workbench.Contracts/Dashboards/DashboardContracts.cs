namespace Workbench.Contracts.Dashboards;

public enum WidgetType
{
    /// <summary>Count, sum or average of a column over a sheet.</summary>
    Metric,

    /// <summary>Items grouped by a Dropdown or Person column, counted or summed.</summary>
    Chart,

    /// <summary>A sheet's items filtered by a column value, sorted by Finish.</summary>
    ItemList,

    /// <summary>Critical path, late tasks and upcoming milestones of a scheduling sheet.</summary>
    ScheduleHealth,

    /// <summary>
    /// Budget, actual cost, EAC and % spent for the project whose plan sheet is <c>Config.SheetId</c>.
    /// Only Finance, Admin and the project's manager see the figures.
    /// </summary>
    ProjectFinancials,

    /// <summary>Markdown text. Raw HTML is never rendered.</summary>
    RichText,

    /// <summary>Ceiling, funded value and days left in the period of performance for one contract.</summary>
    ContractFunding,

    /// <summary>CPI, SPI, EAC and the S-curve for the project whose plan sheet is <c>Config.SheetId</c> (Finance, Admin and its PM).</summary>
    EarnedValue,

    // Home page widgets (role home pages; they work on any dashboard). Each shows the viewer's own data.

    /// <summary>The viewer's to-do list (overdue, today, this week, coming up).</summary>
    MyTodo,

    /// <summary>The viewer's week: hours entered, timesheet status, leave balances, allocations.</summary>
    ThisWeek,

    /// <summary>The oldest approvals waiting on the viewer.</summary>
    WaitingOnYou,

    /// <summary>Sheet items assigned to the viewer.</summary>
    MySheetItems,

    /// <summary>One figure (<c>Config.Kpi</c>), computed with the viewer's permissions.</summary>
    Kpi,

    /// <summary>A saved report-writer report (<c>Config.SavedReportId</c>) charted: first text column by first numeric column.</summary>
    ReportChart,

    /// <summary>A saved list view (<c>Config.ListViewId</c>) charted: rows counted by its group-by column.</summary>
    ListViewChart,

    /// <summary>Links (<c>Config.Shortcuts</c>).</summary>
    Shortcuts,

    /// <summary>A message from the administrators (<c>Config.Markdown</c>), shown as a notice.</summary>
    Announcement,
}

/// <summary>The figures a KPI tile can show. Each comes from the permission-checked services, so a viewer sees only what their pages show.</summary>
public enum KpiKind
{
    /// <summary>Funded value not yet billed on active contracts the viewer can see.</summary>
    FundingRemaining,

    /// <summary>Direct utilization so far this month (the viewer's own, or everyone's for those who see utilization).</summary>
    Utilization,

    /// <summary>Open receivables and the share more than 90 days past due.</summary>
    ArAging,

    /// <summary>Open purchase orders and their value.</summary>
    OpenPurchaseOrders,

    /// <summary>Weighted open pipeline (Pwin × Pgo).</summary>
    Pipeline,
}

/// <summary>A home page shortcut. <see cref="Href"/> is app-relative (<c>time</c>, <c>purchasing/bills/new</c>).</summary>
public sealed record WidgetShortcut(string Label, string Href, string? Icon = null);

public enum WidgetHeight
{
    Small,
    Medium,
    Large,
}

public enum MetricAggregate
{
    Count,
    Sum,
    Average,
}

public enum ChartKind
{
    Bar,
    Pie,
    Donut,
    Line,
}

/// <summary>
/// Settings for every widget type in one record; each type reads the fields it needs.
/// Column ids are columns of <paramref name="SheetId"/>.
/// </summary>
/// <param name="ValueColumnId">Metric: the Number/Currency column to sum or average. Chart: the column to sum (null counts items).</param>
/// <param name="FilterColumnId">Metric and item list: only items whose value in this column equals <paramref name="FilterValue"/>.</param>
/// <param name="GroupColumnId">Chart: the Dropdown or Person column to group by.</param>
/// <param name="MaxItems">Item list: how many rows to show.</param>
/// <param name="Markdown">Rich text: the Markdown source.</param>
/// <param name="ContractId">Contracts and funding: the contract to show.</param>
/// <param name="Kpi">KPI tile: the figure to show.</param>
/// <param name="SavedReportId">Report chart: the saved report-writer report.</param>
/// <param name="ListViewId">List view chart: the saved list view.</param>
/// <param name="Shortcuts">Shortcuts: the links, in order.</param>
/// <param name="Pinned">Home pages: people can't hide the widget from their own home page.</param>
public sealed record WidgetConfig(
    Guid? SheetId = null,
    MetricAggregate Aggregate = MetricAggregate.Count,
    Guid? ValueColumnId = null,
    Guid? FilterColumnId = null,
    string? FilterValue = null,
    ChartKind Chart = ChartKind.Bar,
    Guid? GroupColumnId = null,
    int MaxItems = 10,
    string? Markdown = null,
    Guid? ContractId = null,
    KpiKind? Kpi = null,
    Guid? SavedReportId = null,
    Guid? ListViewId = null,
    IReadOnlyList<WidgetShortcut>? Shortcuts = null,
    bool Pinned = false);

public sealed record WidgetDto(
    Guid Id,
    Guid DashboardId,
    WidgetType Type,
    string Title,
    int Order,
    int Width,
    WidgetHeight Height,
    WidgetConfig Config);

public sealed record DashboardSummary(
    Guid Id,
    string Name,
    string? Description,
    string OwnerEmail,
    Guid? WorkspaceId,
    int WidgetCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>A dashboard with its widgets in display order.</summary>
public sealed record DashboardDto(DashboardSummary Summary, IReadOnlyList<WidgetDto> Widgets);

public sealed record CreateDashboardRequest(string Name, string? Description = null, Guid? WorkspaceId = null);

public sealed record UpdateDashboardRequest(string Name, string? Description);

/// <summary>Creates or replaces a widget. <paramref name="Width"/> is 1–12 grid columns.</summary>
public sealed record SaveWidgetRequest(string Title, WidgetType Type, int Width, WidgetHeight Height, WidgetConfig Config);

/// <summary>Widget ids in their new display order. It must list every widget on the dashboard exactly once.</summary>
public sealed record ReorderWidgetsRequest(IReadOnlyList<Guid> WidgetIds);

public interface IDashboardService
{
    /// <summary>Dashboards, most recently updated first.</summary>
    Task<IReadOnlyList<DashboardSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="WorkbenchNotFoundException"/> when the dashboard doesn't exist.</summary>
    Task<DashboardDto> GetAsync(Guid dashboardId, CancellationToken cancellationToken = default);

    /// <summary>Creates a dashboard owned by the current user.</summary>
    Task<DashboardSummary> CreateAsync(CreateDashboardRequest request, CancellationToken cancellationToken = default);

    Task<DashboardSummary> UpdateAsync(Guid dashboardId, UpdateDashboardRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the dashboard and its widgets.</summary>
    Task DeleteAsync(Guid dashboardId, CancellationToken cancellationToken = default);

    /// <summary>Adds a widget at the end of the dashboard.</summary>
    Task<WidgetDto> AddWidgetAsync(Guid dashboardId, SaveWidgetRequest request, CancellationToken cancellationToken = default);

    Task<WidgetDto> UpdateWidgetAsync(Guid dashboardId, Guid widgetId, SaveWidgetRequest request, CancellationToken cancellationToken = default);

    Task DeleteWidgetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default);

    /// <summary>Returns the dashboard with its widgets in the new order.</summary>
    Task<DashboardDto> ReorderWidgetsAsync(Guid dashboardId, ReorderWidgetsRequest request, CancellationToken cancellationToken = default);
}
