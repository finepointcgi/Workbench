using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tasks;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;

namespace Workbench.Application.Identity;

/// <summary>
/// Task assignment: anyone on the sheet reads its assignees; Editors and Owners assign (a project manager owns their
/// projects' workspaces); an assignee may update their own task's status and % complete with any role (the item decorator
/// enforces exactly that); a project's workload is for Finance, administrators and the project's manager.
/// </summary>
public sealed class PermissionCheckingTaskAssignmentService(ITaskAssignmentService inner, AccessControl access, ProjectAccess projects) : ITaskAssignmentService
{
    public async Task<SheetTasksDto> GetSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var role = await access.GetSheetRoleAsync(sheetId, cancellationToken);
        return (await inner.GetSheetAsync(sheetId, cancellationToken)) with { CanAssign = role.CanEdit() };
    }

    public async Task<IReadOnlyList<TaskAssigneeDto>> SetAssigneesAsync(Guid sheetId, Guid itemId, SetAssigneesRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        return await inner.SetAssigneesAsync(sheetId, itemId, request, cancellationToken);
    }

    public async Task<ItemDto> UpdateStatusAsync(Guid sheetId, Guid itemId, UpdateTaskStatusRequest request, CancellationToken cancellationToken = default)
    {
        // Viewer here; the item decorator lets the update through for Editors, and for assignees when only status changes.
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.UpdateStatusAsync(sheetId, itemId, request, cancellationToken);
    }

    public Task<IReadOnlyList<MyTaskDto>> ListMineAsync(bool includeDone = false, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListMineAsync(includeDone, cancellationToken);
    }

    public async Task<ProjectWorkloadDto> GetProjectWorkloadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see who's working on what", cancellationToken);
        return await inner.GetProjectWorkloadAsync(projectId, cancellationToken);
    }
}

/// <summary>The narrow rule behind "assignees update their own task": status and % complete only, any sheet role.</summary>
public sealed class AssigneeStatusPermission(IWorkbenchDbContextFactory dbFactory, AccessControl access)
{
    public async Task<bool> AllowsAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        return TaskRules.ChangesOnlyStatus(item, columns, request)
            && await TaskRules.IsAssignedAsync(db, item, columns, access.Email, cancellationToken);
    }
}
