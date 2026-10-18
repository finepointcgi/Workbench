using Workbench.Contracts.Financials;

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

/// <param name="DaysLeft">Calendar days left in the period of performance: the whole period before it starts, 0 once it has ended.</param>
/// <param name="PeriodElapsed">Fraction of the period of performance that has passed, 0–1.</param>
/// <param name="Projects">Codes of the projects charged against the contract.</param>
public sealed record ContractFundingData(
    Guid ContractId,
    string ContractNumber,
    string Name,
    string CustomerName,
    string Status,
    decimal CeilingValue,
    decimal FundedValue,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int DaysLeft,
    double PeriodElapsed,
    IReadOnlyList<string> Projects)
{
    /// <summary>Funded value as a fraction of the ceiling, or null when there is no ceiling.</summary>
    public decimal? FundedShare => CeilingValue == 0 ? null : FundedValue / CeilingValue;
}

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
    string? Markdown = null,
    ContractFundingData? ContractFunding = null,
    ProjectFinancialSummaryDto? ProjectFinancials = null);

/// <summary>
/// Computes widget data on the server. Sheet data is read through <c>ISheetService</c>/<c>IItemService</c>, the
/// scheduling contract and the project-accounting contracts, so it's limited to what the current user can see.
/// Project financials and contract funding follow <c>IFinancialAccessService</c>: a widget the viewer may not read
/// comes back with an <c>Error</c> instead of figures.
/// </summary>
public interface IWidgetDataService
{
    Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default);
}
