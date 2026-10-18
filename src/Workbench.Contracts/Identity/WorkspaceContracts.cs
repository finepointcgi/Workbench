namespace Workbench.Contracts.Identity;

/// <summary>A workspace and the signed-in user's role in it (null for admins who aren't members).</summary>
public sealed record WorkspaceSummary(Guid Id, string Name, WorkspaceRole? MyRole);

public sealed record WorkspaceMemberDto(Guid WorkspaceId, string UserEmail, string? DisplayName, WorkspaceRole Role);

public sealed record SetWorkspaceMemberRequest(string Email, WorkspaceRole Role);

/// <summary>
/// Workspace members. Anyone who can see a workspace can list its members; only its Owners (and admins) can
/// change them. A workspace always keeps at least one Owner.
/// </summary>
public interface IWorkspaceMembershipService
{
    /// <summary>Workspaces the user belongs to (every workspace for admins), ordered by name.</summary>
    Task<IReadOnlyList<WorkspaceSummary>> ListMyWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>The workspace a sheet belongs to. Throws <see cref="WorkbenchNotFoundException"/> when the user can't see the sheet.</summary>
    Task<WorkspaceSummary> GetForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceMemberDto>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Adds the person or changes their role. People can be added before they first sign in.</summary>
    Task<WorkspaceMemberDto> SetMemberAsync(Guid workspaceId, SetWorkspaceMemberRequest request, CancellationToken cancellationToken = default);

    Task RemoveMemberAsync(Guid workspaceId, string email, CancellationToken cancellationToken = default);
}
