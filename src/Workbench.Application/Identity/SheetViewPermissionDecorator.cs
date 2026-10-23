using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Identity;

/// <summary>
/// Saved views and exports need the sheet to be visible (Viewer). Personal views are anyone's; saving, changing or deleting
/// a shared view and setting the sheet's default view need Editor.
/// </summary>
public sealed class PermissionCheckingSheetViewService(ISheetViewService inner, AccessControl access) : ISheetViewService
{
    public async Task<SheetViewsDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ListAsync(sheetId, cancellationToken);
    }

    public async Task<SheetViewDto> CreateAsync(Guid sheetId, SaveSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, request.IsShared ? WorkspaceRole.Editor : WorkspaceRole.Viewer, cancellationToken);
        return await inner.CreateAsync(sheetId, request, cancellationToken);
    }

    public async Task<SheetViewDto> UpdateAsync(Guid sheetId, Guid viewId, SaveSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        var shared = request.IsShared || (await FindAsync(sheetId, viewId, cancellationToken)).IsShared;
        await access.RequireSheetRoleAsync(sheetId, shared ? WorkspaceRole.Editor : WorkspaceRole.Viewer, cancellationToken);
        return await inner.UpdateAsync(sheetId, viewId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid viewId, CancellationToken cancellationToken = default)
    {
        var view = await FindAsync(sheetId, viewId, cancellationToken);
        await access.RequireSheetRoleAsync(sheetId, view.IsShared ? WorkspaceRole.Editor : WorkspaceRole.Viewer, cancellationToken);
        await inner.DeleteAsync(sheetId, viewId, cancellationToken);
    }

    public async Task SetMyDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        await inner.SetMyDefaultAsync(sheetId, request, cancellationToken);
    }

    public async Task SetSheetDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.SetSheetDefaultAsync(sheetId, request, cancellationToken);
    }

    public async Task<ReportFileDto> ExportAsync(Guid sheetId, SheetExportRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ExportAsync(sheetId, request, cancellationToken);
    }

    /// <summary>The view, or not found (which also covers someone else's personal view) after the Viewer check.</summary>
    private async Task<SheetViewDto> FindAsync(Guid sheetId, Guid viewId, CancellationToken cancellationToken)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return (await inner.ListAsync(sheetId, cancellationToken)).Views.FirstOrDefault(v => v.Id == viewId)
            ?? throw WorkbenchNotFoundException.For("Sheet view", viewId);
    }
}
