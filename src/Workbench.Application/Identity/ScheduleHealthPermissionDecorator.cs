using Workbench.Contracts.Identity;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Identity;

/// <summary>The schedule health check reads the plan, so it follows the sheet's Viewer role, like the Gantt and its exports.</summary>
public sealed class PermissionCheckingScheduleHealthService(IScheduleHealthService inner, AccessControl access) : IScheduleHealthService
{
    public async Task<ScheduleHealthDto> GetAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetAsync(sheetId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskFloatDto>> GetFloatAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetFloatAsync(sheetId, cancellationToken);
    }

    public async Task<PlanFile> ExportPdfAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ExportPdfAsync(sheetId, request, cancellationToken);
    }

    public async Task<PlanFile> ExportExcelAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ExportExcelAsync(sheetId, request, cancellationToken);
    }
}
