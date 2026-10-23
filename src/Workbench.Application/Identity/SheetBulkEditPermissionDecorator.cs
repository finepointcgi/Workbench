using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Identity;

/// <summary>
/// Bulk edits need Editor. Columns hidden from the caller (restricted) are treated as missing: their cells are skipped as
/// "no such column", and their values never come back.
/// </summary>
public sealed class PermissionCheckingSheetBulkEditService(ISheetBulkEditService inner, AccessControl access, RestrictedColumnAccess restricted) : ISheetBulkEditService
{
    public async Task<SheetBulkEditResult> ApplyAsync(Guid sheetId, SheetBulkEditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        if (hidden.Count == 0)
        {
            return await inner.ApplyAsync(sheetId, request, cancellationToken);
        }

        var skipped = new List<BulkSkippedCell>();
        bool Hidden(BulkCellEdit cell) => Guid.TryParse(cell.Field, out var id) && hidden.Contains(id);
        var rows = (request.Rows ?? []).Select(r =>
        {
            skipped.AddRange((r.Cells ?? []).Where(Hidden).Select(c => new BulkSkippedCell(r.ItemId, null, c.Field, "There's no such column on this sheet.")));
            return r with { Cells = (r.Cells ?? []).Where(c => !Hidden(c)).ToList() };
        }).ToList();
        var newRows = (request.NewRows ?? []).Select((r, i) =>
        {
            skipped.AddRange((r.Cells ?? []).Where(Hidden).Select(c => new BulkSkippedCell(null, i, c.Field, "There's no such column on this sheet.")));
            return r with { Cells = (r.Cells ?? []).Where(c => !Hidden(c)).ToList() };
        }).ToList();

        var result = await inner.ApplyAsync(sheetId, request with { Rows = rows, NewRows = newRows }, cancellationToken);
        return result with
        {
            Updated = result.Updated.Select(i => RestrictedColumnAccess.Strip(i, hidden)).ToList(),
            Created = result.Created.Select(i => RestrictedColumnAccess.Strip(i, hidden)).ToList(),
            Changes = result.Changes.Where(c => !(Guid.TryParse(c.Field, out var id) && hidden.Contains(id))).ToList(),
            Skipped = [.. skipped, .. result.Skipped],
        };
    }
}
