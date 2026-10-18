namespace Workbench.Contracts.Identity;

/// <summary>Workspace roles, from least to most access. Each role can do everything the ones before it can.</summary>
public enum WorkspaceRole
{
    /// <summary>Reads sheets.</summary>
    Viewer,

    /// <summary>Also comments.</summary>
    Commenter,

    /// <summary>Also changes items, columns, dependencies and baselines, uploads attachments and creates sheets.</summary>
    Editor,

    /// <summary>Also manages the workspace's members.</summary>
    Owner,
}

public static class WorkspaceRoles
{
    public static bool CanView(this WorkspaceRole? role) => role is not null;

    public static bool CanComment(this WorkspaceRole? role) => role >= WorkspaceRole.Commenter;

    public static bool CanEdit(this WorkspaceRole? role) => role >= WorkspaceRole.Editor;

    public static bool CanManage(this WorkspaceRole? role) => role >= WorkspaceRole.Owner;
}

/// <summary>What the signed-in user may do on a sheet. The server enforces the same rules on every call.</summary>
public interface ISheetPermissions
{
    /// <summary>
    /// The user's role on the sheet through its workspace, <see cref="WorkspaceRole.Owner"/> for admins, or null
    /// when the user can't see the sheet or it doesn't exist.
    /// </summary>
    Task<WorkspaceRole?> GetMyRoleAsync(Guid sheetId, CancellationToken cancellationToken = default);
}

public static class SheetPermissionsExtensions
{
    public static async Task<bool> CanCommentAsync(this ISheetPermissions permissions, Guid sheetId, CancellationToken cancellationToken = default) =>
        (await permissions.GetMyRoleAsync(sheetId, cancellationToken)).CanComment();

    public static async Task<bool> CanEditAsync(this ISheetPermissions permissions, Guid sheetId, CancellationToken cancellationToken = default) =>
        (await permissions.GetMyRoleAsync(sheetId, cancellationToken)).CanEdit();

    public static async Task<bool> CanManageAsync(this ISheetPermissions permissions, Guid sheetId, CancellationToken cancellationToken = default) =>
        (await permissions.GetMyRoleAsync(sheetId, cancellationToken)).CanManage();
}
