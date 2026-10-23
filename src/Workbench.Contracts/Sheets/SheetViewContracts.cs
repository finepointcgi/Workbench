using Workbench.Contracts.Customization;
using Workbench.Contracts.Reports;

namespace Workbench.Contracts.Sheets;

// Saved views of a sheet's grid: sort, filter and hidden columns, personal or shared with everyone who can open the sheet,
// with a default view per sheet. Like list views (ListViewContracts) they hold field keys, never data: rows always come
// from the permission-checked item service, so a view can't show a row or a restricted column its viewer can't see.

/// <summary>
/// The built-in fields a view can sort or filter on; every other field key is a column id (<c>Guid.ToString()</c>).
/// They match the grid's column keys.
/// </summary>
public static class SheetFields
{
    public const string Title = "title";
    public const string Start = "start";
    public const string Finish = "finish";
    public const string Wbs = "wbs";
}

/// <summary>
/// What a view shows. Sorts are applied in order within each level of the hierarchy (children stay under their parent);
/// every filter must match (AND), and a matching row keeps its parents for context. Equals and NotEquals take several
/// values separated by new lines ("is any of" / "is none of"). <see cref="HiddenColumns"/> are field keys.
/// </summary>
public sealed record SheetViewDefinition(
    IReadOnlyList<ListSort>? Sort = null,
    IReadOnlyList<ListFilterCondition>? Filters = null,
    IReadOnlyList<string>? HiddenColumns = null)
{
    public static SheetViewDefinition Standard { get; } = new();

    public bool HasSort => Sort is { Count: > 0 };

    public bool HasFilters => Filters is { Count: > 0 };

    public bool IsStandard => !HasSort && !HasFilters && HiddenColumns is not { Count: > 0 };

    public bool IsHidden(string field) => HiddenColumns?.Contains(field) == true;
}

/// <param name="IsShared">Everyone who can open the sheet sees it; only the sheet's editors change it.</param>
/// <param name="CanEdit">The caller may change or delete it: their own personal view, or a shared view when they edit the sheet.</param>
public sealed record SheetViewDto(
    Guid Id,
    Guid SheetId,
    string Name,
    bool IsShared,
    string OwnerEmail,
    SheetViewDefinition Definition,
    bool IsMine,
    bool CanEdit,
    DateTime UpdatedAtUtc);

/// <summary>
/// The views of a sheet the caller can use. The sheet's default (a shared view, set by an editor) opens for everyone
/// who hasn't picked their own default.
/// </summary>
/// <param name="CanShare">The caller edits the sheet, so they may save shared views and set the sheet's default.</param>
public sealed record SheetViewsDto(Guid SheetId, IReadOnlyList<SheetViewDto> Views, Guid? SheetDefaultViewId, Guid? MyDefaultViewId, bool CanShare)
{
    /// <summary>The view the grid opens with: the caller's own default, else the sheet's (null: the standard view).</summary>
    public SheetViewDto? DefaultView => Views.FirstOrDefault(v => v.Id == (MyDefaultViewId ?? SheetDefaultViewId));
}

public sealed record SaveSheetViewRequest(string Name, bool IsShared, SheetViewDefinition Definition);

/// <summary>A view id, or null to clear the default.</summary>
public sealed record SetDefaultSheetViewRequest(Guid? ViewId);

public enum SheetExportFormat
{
    Xlsx,
    Pdf,
}

/// <summary>
/// Exports the sheet as the caller may see it. <paramref name="View"/> applies a sort, filter and hidden columns (the grid
/// sends what's on screen); null exports every row and column.
/// </summary>
public sealed record SheetExportRequest(SheetExportFormat Format, SheetViewDefinition? View = null, string? ViewName = null);

public interface ISheetViewService
{
    Task<SheetViewsDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<SheetViewDto> CreateAsync(Guid sheetId, SaveSheetViewRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the view's name, sharing and definition. Throws not found for a view the caller can't use.</summary>
    Task<SheetViewDto> UpdateAsync(Guid sheetId, Guid viewId, SaveSheetViewRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sheetId, Guid viewId, CancellationToken cancellationToken = default);

    /// <summary>The caller's own default view of the sheet; null goes back to the sheet's default.</summary>
    Task SetMyDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The view everyone opens the sheet with (a shared view); null goes back to the standard view. Editors only.</summary>
    Task SetSheetDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The sheet (or a view of it) as .xlsx or .pdf; records an <c>export.sheet</c> security event.</summary>
    Task<ReportFileDto> ExportAsync(Guid sheetId, SheetExportRequest request, CancellationToken cancellationToken = default);
}
