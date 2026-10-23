using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Costpoint;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Identity;

// Decorators that enforce workspace roles in front of the feature services, so the rules hold for every caller
// (Blazor UI, REST API, hub). A sheet the user can't see is "not found"; a sheet they can see but not change
// is "forbidden". Registered last in AddWorkbenchIdentity so they wrap every other decorator.

public sealed class PermissionCheckingSheetService(ISheetService inner, AccessControl access, RestrictedColumnAccess restricted) : ISheetService
{
    public async Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var visible = await access.GetVisibleWorkspaceIdsAsync(cancellationToken);
        var sheets = await inner.ListAsync(cancellationToken);
        if (visible is null)
        {
            return sheets;
        }

        var allowed = await access.GetSheetIdsInAsync(visible, cancellationToken);
        return sheets.Where(s => allowed.Contains(s.Id)).ToList();
    }

    public async Task<SheetDetail?> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        if (await access.GetSheetRoleAsync(sheetId, cancellationToken) is null || await inner.GetAsync(sheetId, cancellationToken) is not { } sheet)
        {
            return null;
        }

        return await restricted.CanSeeRestrictedAsync(sheetId, cancellationToken)
            ? sheet with { CanSeeRestricted = true }
            : RestrictedColumnAccess.Strip(sheet, await restricted.HiddenColumnsAsync(sheetId, cancellationToken));
    }

    /// <summary>The Editor check happens in <see cref="EditableWorkspacePicker"/>, which also picks the workspace.</summary>
    public Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.CreateAsync(request, cancellationToken);
    }
}

/// <summary>
/// Restricted columns' values never reach, and can't be written by, people who can't see them. Changes need Editor, except
/// that an assignee of the item (Assignees or a Person column) with any role may change only its status and % complete.
/// </summary>
public sealed class PermissionCheckingItemService(
    IItemService inner, AccessControl access, RestrictedColumnAccess restricted, AssigneeStatusPermission? assignees = null) : IItemService
{
    public async Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        var items = await inner.ListAsync(sheetId, cancellationToken);
        return hidden.Count == 0 ? items : items.Select(i => RestrictedColumnAccess.Strip(i, hidden)).ToList();
    }

    public async Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        if (hidden.Count > 0 && request.Values is not null)
        {
            request = request with { Values = RestrictedColumnAccess.Without(request.Values, hidden) };
        }

        return RestrictedColumnAccess.Strip(await inner.CreateAsync(sheetId, request, cancellationToken), hidden);
    }

    public async Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        var role = await access.GetSheetRoleAsync(sheetId, cancellationToken);
        if (role is null || role < WorkspaceRole.Editor && (assignees is null || !await assignees.AllowsAsync(sheetId, itemId, request, cancellationToken)))
        {
            await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken); // not found, or forbidden (recorded)
        }

        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        if (hidden.Count > 0)
        {
            request = request with { Values = await restricted.MergeHiddenAsync(sheetId, itemId, request.Values, hidden, cancellationToken) };
        }

        return RestrictedColumnAccess.Strip(await inner.UpdateAsync(sheetId, itemId, request, cancellationToken), hidden);
    }

    public async Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        return RestrictedColumnAccess.Strip(await inner.MoveAsync(sheetId, itemId, request, cancellationToken), hidden);
    }

    public async Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.DeleteAsync(sheetId, itemId, cancellationToken);
    }
}

/// <summary>
/// Columns need Editor. Restricting a column (or lifting it), and changing or deleting a column hidden from the user,
/// needs Finance, Admin or the workspace's Owner role; to everyone else a hidden column doesn't exist.
/// </summary>
public sealed class PermissionCheckingColumnService(IColumnService inner, AccessControl access, RestrictedColumnAccess restricted) : IColumnService
{
    public async Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        if (request.IsRestricted)
        {
            await restricted.RequireCanSeeRestrictedAsync(sheetId, "restrict columns", cancellationToken);
        }

        // A formula over a restricted column is restricted too: its creator gets it back, but reads leave it out from now on.
        return await inner.CreateAsync(sheetId, request, cancellationToken);
    }

    public async Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireVisibleAsync(sheetId, columnId, cancellationToken);
        if (request.IsRestricted is not null)
        {
            await restricted.RequireCanSeeRestrictedAsync(sheetId, "restrict columns or lift a restriction", cancellationToken);
        }

        return await inner.UpdateAsync(sheetId, columnId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireVisibleAsync(sheetId, columnId, cancellationToken);
        await inner.DeleteAsync(sheetId, columnId, cancellationToken);
    }

    private async Task RequireVisibleAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken)
    {
        if ((await restricted.HiddenColumnsAsync(sheetId, cancellationToken)).Contains(columnId))
        {
            throw WorkbenchNotFoundException.For("Column", columnId);
        }
    }
}

public sealed class PermissionCheckingDependencyService(IDependencyService inner, AccessControl access) : IDependencyService
{
    public async Task<IReadOnlyList<DependencyDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ListAsync(sheetId, cancellationToken);
    }

    public async Task<DependencyDto> AddAsync(Guid sheetId, CreateDependencyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        return await inner.AddAsync(sheetId, request, cancellationToken);
    }

    public async Task RemoveAsync(Guid sheetId, Guid dependencyId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.RemoveAsync(sheetId, dependencyId, cancellationToken);
    }

    public async Task<ScheduleAnalysis> AnalyzeAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.AnalyzeAsync(sheetId, cancellationToken);
    }
}

public sealed class PermissionCheckingBaselineService(IBaselineService inner, AccessControl access) : IBaselineService
{
    public async Task<BaselineSummary> CaptureAsync(Guid sheetId, CaptureBaselineRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        return await inner.CaptureAsync(sheetId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<BaselineSummary>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ListAsync(sheetId, cancellationToken);
    }

    public async Task<BaselineDetail> GetAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetAsync(sheetId, baselineId, cancellationToken);
    }

    public async Task<BaselineComparison> CompareAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.CompareAsync(sheetId, baselineId, cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.DeleteAsync(sheetId, baselineId, cancellationToken);
    }
}

public sealed class PermissionCheckingCommentService(ICommentService inner, AccessControl access) : ICommentService
{
    public async Task<IReadOnlyList<CommentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ListAsync(sheetId, itemId, cancellationToken);
    }

    public async Task<CommentDto> CreateAsync(Guid sheetId, Guid itemId, CreateCommentRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Commenter, cancellationToken);
        return await inner.CreateAsync(sheetId, itemId, request, cancellationToken);
    }

    public async Task<CommentDto> UpdateAsync(Guid sheetId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Commenter, cancellationToken);
        return await inner.UpdateAsync(sheetId, commentId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid commentId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Commenter, cancellationToken);
        await inner.DeleteAsync(sheetId, commentId, cancellationToken);
    }
}

public sealed class PermissionCheckingAttachmentService(IAttachmentService inner, AccessControl access) : IAttachmentService
{
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ListAsync(sheetId, itemId, cancellationToken);
    }

    public async Task<AttachmentDto> UploadAsync(Guid sheetId, Guid itemId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        return await inner.UploadAsync(sheetId, itemId, fileName, content, cancellationToken);
    }

    public async Task<AttachmentContent> DownloadAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.DownloadAsync(sheetId, attachmentId, cancellationToken);
    }

    /// <summary>Just builds a URL; the download itself is checked.</summary>
    public string GetDownloadUrl(Guid sheetId, Guid attachmentId) => inner.GetDownloadUrl(sheetId, attachmentId);

    public async Task DeleteAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await inner.DeleteAsync(sheetId, attachmentId, cancellationToken);
    }
}

public sealed class PermissionCheckingActivityService(IActivityService inner, AccessControl access) : IActivityService
{
    public async Task<IReadOnlyList<ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.ListAsync(sheetId, itemId, limit, cancellationToken);
    }
}

/// <summary>Plan-vs-actual follows the sheet's access; a manual sync is for admins. Lookups are open to anyone signed in.</summary>
public sealed class PermissionCheckingCostpointService(ICostpointService inner, AccessControl access) : ICostpointService
{
    public Task<CostpointStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetStatusAsync(cancellationToken);
    }

    public Task<IReadOnlyList<CostpointSyncRunDto>> ListSyncRunsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListSyncRunsAsync(take, cancellationToken);
    }

    public async Task<CostpointSyncRunDto> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminIntegrations, "run the Costpoint sync", cancellationToken);
        return await inner.SyncNowAsync(cancellationToken);
    }

    public Task<IReadOnlyList<CostpointProjectDto>> SearchProjectsAsync(string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.SearchProjectsAsync(query, take, cancellationToken);
    }

    public Task<IReadOnlyList<CostpointWbsDto>> SearchWbsAsync(string? projectId, string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.SearchWbsAsync(projectId, query, take, cancellationToken);
    }

    public async Task<PlanVsActualDto> GetPlanVsActualAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetPlanVsActualAsync(sheetId, cancellationToken);
    }
}
