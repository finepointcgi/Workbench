using Workbench.Contracts.Identity;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Identity;

/// <summary>
/// The plan editor follows the sheet's roles: Viewers read the outline and export it; Editors (who can edit the project's
/// plan) indent, re-date, link and import. Previewing an import needs Editor too, since it exists only to import.
/// </summary>
public sealed class PermissionCheckingPlanScheduleService(IPlanScheduleService inner, AccessControl access) : IPlanScheduleService
{
    public async Task<PlanScheduleDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetAsync(sheetId, cancellationToken);
    }

    public async Task IndentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.IndentAsync(sheetId, itemId, cancellationToken);
    }

    public async Task OutdentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.OutdentAsync(sheetId, itemId, cancellationToken);
    }

    public async Task SetDurationAsync(Guid sheetId, Guid itemId, int days, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.SetDurationAsync(sheetId, itemId, days, cancellationToken);
    }

    public async Task SetPredecessorsAsync(Guid sheetId, Guid itemId, string? predecessors, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.SetPredecessorsAsync(sheetId, itemId, predecessors, cancellationToken);
    }

    public async Task<PlanFile> ExportAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ExportAsync(sheetId, cancellationToken);
    }

    public Task<PlanFile> TemplateAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.TemplateAsync(cancellationToken);
    }

    public async Task<PlanImportPreview> PreviewImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        return await inner.PreviewImportAsync(sheetId, request, cancellationToken);
    }

    public async Task<PlanImportPreview> ApplyImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        return await inner.ApplyImportAsync(sheetId, request, cancellationToken);
    }
}
