namespace Workbench.Contracts.Dashboards;

/// <param name="Label">What was measured, e.g. "Sum of Budget".</param>
/// <param name="IsCurrency">True when the value column is a Currency column.</param>
public sealed record MetricData(decimal Value, string Label, int ItemCount, bool IsCurrency);

/// <summary>One value per label, in the group column's option order (blank values last as "(none)").</summary>
public sealed record ChartData(ChartKind Kind, IReadOnlyList<string> Labels, IReadOnlyList<double> Values, string SeriesName);

public sealed record WidgetItemRow(Guid ItemId, string Title, DateOnly? Finish, string? Status);

/// <param name="Total">Matching items before <c>MaxItems</c> was applied.</param>
public sealed record ItemListData(IReadOnlyList<WidgetItemRow> Items, int Total);

public sealed record MilestoneRow(Guid ItemId, string Title, DateOnly Date);

/// <param name="LateTasks">Leaf tasks whose finish is before today and that aren't done.</param>
/// <param name="NextMilestones">Up to five zero-duration tasks on or after today, soonest first.</param>
public sealed record ScheduleHealthData(
    int TaskCount,
    int CriticalTasks,
    int LateTasks,
    DateOnly? ProjectFinish,
    IReadOnlyList<MilestoneRow> NextMilestones);

public sealed record PlanVsActualSummary(
    string? ProjectId,
    string? ProjectName,
    decimal PlannedHours,
    decimal ActualHours,
    decimal PlannedBudget,
    decimal ActualCost,
    decimal? Burn,
    DateTime? LastSyncedAtUtc,
    IReadOnlyList<string> Notes);

/// <summary>
/// Computed data for one widget. Exactly one of the typed parts is set for the widget's type, unless
/// <paramref name="Error"/> explains why it couldn't be computed (e.g. the sheet or a column is gone).
/// </summary>
/// <param name="SheetId">The sheet the data came from, so clients know when to refresh.</param>
public sealed record WidgetDataDto(
    Guid WidgetId,
    WidgetType Type,
    Guid? SheetId,
    string? SheetName,
    string? Error,
    MetricData? Metric = null,
    ChartData? Chart = null,
    ItemListData? ItemList = null,
    ScheduleHealthData? Schedule = null,
    PlanVsActualSummary? PlanVsActual = null,
    string? Markdown = null);

/// <summary>
/// Computes widget data on the server. Sheet data is read through <c>ISheetService</c>/<c>IItemService</c> and the
/// scheduling and Costpoint contracts, so it's limited to what the current user can see.
/// </summary>
public interface IWidgetDataService
{
    Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default);
}
