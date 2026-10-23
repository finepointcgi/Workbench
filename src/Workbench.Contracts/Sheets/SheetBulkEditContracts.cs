namespace Workbench.Contracts.Sheets;

// Many cells at once (the grid's paste, fill down and clear, and their undo): one request, one save, one plan reflow, one
// activity entry and live refresh, and the sheet's automations run once as a batch. Every cell is still checked on its own:
// one that can't be set (read-only, restricted, not valid for its column) is skipped and reported, the rest are saved.

/// <summary>
/// One cell to set. <see cref="Field"/> is a built-in field (<see cref="SheetFields"/>) or a column id. <see cref="Value"/> is
/// the stored (invariant) form: numbers <c>1234.5</c>, dates <c>yyyy-MM-dd</c>, checkboxes <c>true</c>/<c>false</c>; null clears.
/// </summary>
public sealed record BulkCellEdit(string Field, string? Value);

public sealed record BulkRowEdit(Guid ItemId, IReadOnlyList<BulkCellEdit> Cells);

/// <summary>A row to add at the end of the sheet (top level), with its cells. A row without a title is called "New row".</summary>
public sealed record BulkNewRow(IReadOnlyList<BulkCellEdit> Cells);

/// <summary>
/// Changes to existing rows, rows to add, and rows to delete (with their subtasks; used to undo a paste that added rows).
/// <paramref name="Source"/> names the change in the activity feed and automation summaries ("Paste", "Fill down").
/// </summary>
public sealed record SheetBulkEditRequest(
    IReadOnlyList<BulkRowEdit> Rows,
    IReadOnlyList<BulkNewRow>? NewRows = null,
    IReadOnlyList<Guid>? DeleteItemIds = null,
    string? Source = null);

/// <summary>A cell that wasn't set, and why. <see cref="NewRowIndex"/> points into <see cref="SheetBulkEditRequest.NewRows"/>.</summary>
public sealed record BulkSkippedCell(Guid? ItemId, int? NewRowIndex, string Field, string Reason);

/// <summary>
/// A cell that changed: its stored value before and after (what undo puts back). On a plan this includes start and finish dates
/// the plan moved because of the edit (successors pushed by links), so putting every <c>Before</c> back undoes all of it.
/// </summary>
public sealed record BulkCellChange(Guid ItemId, string Field, string? Before, string? After);

/// <param name="Updated">Changed rows as saved (formula and lookup columns worked out).</param>
/// <param name="Created">Added rows, in the order of <see cref="SheetBulkEditRequest.NewRows"/>.</param>
public sealed record SheetBulkEditResult(
    IReadOnlyList<ItemDto> Updated,
    IReadOnlyList<ItemDto> Created,
    IReadOnlyList<BulkCellChange> Changes,
    IReadOnlyList<BulkSkippedCell> Skipped,
    int Deleted = 0);

public interface ISheetBulkEditService
{
    /// <summary>Applies every cell it can in one save (Editor). Throws only when the sheet is missing or the request is too big.</summary>
    Task<SheetBulkEditResult> ApplyAsync(Guid sheetId, SheetBulkEditRequest request, CancellationToken cancellationToken = default);
}
