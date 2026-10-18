namespace Workbench.Domain.Identity;

/// <summary>Workspace roles, from least to most access. Each role can do everything the ones before it can.</summary>
public enum WorkspaceRole
{
    Viewer,
    Commenter,
    Editor,
    Owner,
}

/// <summary>
/// Grants a user a role in a workspace, and through it on every sheet in the workspace. Keyed by email so people
/// can be added before they first sign in.
/// </summary>
public sealed class WorkspaceMember : Entity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Lower-case.</summary>
    public required string UserEmail { get; set; }

    public WorkspaceRole Role { get; set; }
}
