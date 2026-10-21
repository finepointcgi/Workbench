using Workbench.Contracts.Reports;

namespace Workbench.Contracts.Customization;

// Saved list views: the columns, sort, filters and grouping of a list page (projects, bills, invoices, ...). Anyone can
// save personal views; sharing a view with a role or everyone takes CustomizationPermissions.ShareViews. Rows always come
// from the page's permission-checked services, and columns a person can't see are never offered, whatever a view names.

/// <summary>List keys (one per list page).</summary>
public static class CustomizableLists
{
    public const string Projects = "projects";
    public const string Contracts = "contracts";
    public const string People = "people";
    public const string Vendors = "vendors";
    public const string PurchaseOrders = "purchase-orders";
    public const string Bills = "bills";
    public const string Invoices = "invoices";
    public const string Timesheets = "timesheets";
    public const string ExpenseReports = "expense-reports";
    public const string Opportunities = "opportunities";
    public const string Parts = "parts";
    public const string SalesOrders = "sales-orders";

    public static IReadOnlyList<string> All { get; } =
        [Projects, Contracts, People, Vendors, PurchaseOrders, Bills, Invoices, Timesheets, ExpenseReports, Opportunities, Parts, SalesOrders];
}

public enum ListViewVisibility
{
    /// <summary>Only the owner sees it.</summary>
    Personal,

    /// <summary>Everyone holding <c>RoleKey</c>.</summary>
    Role,

    /// <summary>Every signed-in person.</summary>
    Everyone,
}

public enum ListFilterOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>A filter condition on a column. <see cref="Value"/> is an invariant string (numbers <c>1234.5</c>, dates <c>yyyy-MM-dd</c>).</summary>
public sealed record ListFilterCondition(string Field, ListFilterOperator Operator, string? Value = null);

public sealed record ListSort(string Field, bool Descending = false);

/// <summary>
/// What a view shows. Columns in order; sorts applied in order; every filter must match (AND). <see cref="GroupBy"/> is a
/// column key or null. <see cref="Search"/> is the quick search text.
/// </summary>
public sealed record ListViewDefinition(
    IReadOnlyList<string> Columns,
    IReadOnlyList<ListSort>? Sort = null,
    IReadOnlyList<ListFilterCondition>? Filters = null,
    string? GroupBy = null,
    string? Search = null);

/// <param name="CanEdit">The owner, or (for shared views) someone who may share views.</param>
public sealed record SavedListViewDto(
    Guid Id,
    string ListKey,
    string Name,
    string OwnerEmail,
    ListViewVisibility Visibility,
    string? RoleKey,
    ListViewDefinition Definition,
    bool IsMine,
    bool CanEdit,
    DateTime UpdatedAtUtc);

/// <summary>The views of one list the caller can use, and the one they picked as their default (null: the standard view).</summary>
public sealed record ListViewsDto(string ListKey, IReadOnlyList<SavedListViewDto> Views, Guid? DefaultViewId, bool CanShare);

public sealed record SaveListViewRequest(string ListKey, string Name, ListViewVisibility Visibility, string? RoleKey, ListViewDefinition Definition);

public sealed record SetDefaultListViewRequest(Guid? ViewId);

/// <summary>
/// The current view as the person sees it, to save as a file. The page builds it from the same rows and columns it shows,
/// so the file matches the screen.
/// </summary>
public sealed record ListExportRequest(
    string ListKey,
    string Title,
    IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    ReportFileFormat Format);

public interface IListViewService
{
    Task<ListViewsDto> ListAsync(string listKey, CancellationToken ct = default);

    /// <summary>Every view the caller can use, across lists (for home page chart widgets).</summary>
    Task<IReadOnlyList<SavedListViewDto>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Throws not found for a view the caller can't use.</summary>
    Task<SavedListViewDto> GetAsync(Guid viewId, CancellationToken ct = default);

    Task<SavedListViewDto> CreateAsync(SaveListViewRequest request, CancellationToken ct = default);

    Task<SavedListViewDto> UpdateAsync(Guid viewId, SaveListViewRequest request, CancellationToken ct = default);

    Task DeleteAsync(Guid viewId, CancellationToken ct = default);

    /// <summary>The caller's default view for the list (null: the standard view).</summary>
    Task SetDefaultAsync(string listKey, SetDefaultListViewRequest request, CancellationToken ct = default);

    /// <summary>Renders the view as .xlsx or .csv (formula injection neutralized) and records an <c>export.list_view</c> event.</summary>
    Task<ReportFileDto> ExportAsync(ListExportRequest request, CancellationToken ct = default);
}
