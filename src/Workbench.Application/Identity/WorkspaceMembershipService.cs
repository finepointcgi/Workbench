using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Identity;

public sealed class WorkspaceMembershipService(IWorkbenchDbContextFactory dbFactory, AccessControl access) : IWorkspaceMembershipService
{
    public async Task<IReadOnlyList<WorkspaceSummary>> ListMyWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var email = access.Email;
        var memberships = await db.Set<WorkspaceMember>().AsNoTracking()
            .Where(m => m.UserEmail == email)
            .ToDictionaryAsync(m => m.WorkspaceId, m => m.Role, cancellationToken);
        var admin = await access.IsAdminAsync(db, cancellationToken);
        var workspaces = await db.Workspaces.AsNoTracking()
            .Where(w => admin || memberships.Keys.Contains(w.Id))
            .OrderBy(w => w.Name)
            .ToListAsync(cancellationToken);
        return workspaces
            .Select(w => new WorkspaceSummary(w.Id, w.Name, memberships.TryGetValue(w.Id, out var role) ? AccessControl.ToContract(role) : null))
            .ToList();
    }

    public async Task<WorkspaceSummary> GetForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        var role = await access.GetSheetRoleAsync(sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var workspace = await db.Sheets.AsNoTracking()
            .Where(s => s.Id == sheetId)
            .Select(s => new { s.WorkspaceId, s.Workspace!.Name })
            .FirstAsync(cancellationToken);
        return new WorkspaceSummary(workspace.WorkspaceId, workspace.Name, role);
    }

    public async Task<IReadOnlyList<WorkspaceMemberDto>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceRoleAsync(workspaceId, WorkspaceRole.Viewer, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var members = await db.Set<WorkspaceMember>().AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var emails = members.Select(m => m.UserEmail).ToList();
        var names = await db.Set<AppUser>().AsNoTracking()
            .Where(u => emails.Contains(u.Email))
            .ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        return members
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.UserEmail, StringComparer.Ordinal)
            .Select(m => ToDto(m, names.GetValueOrDefault(m.UserEmail)))
            .ToList();
    }

    public async Task<WorkspaceMemberDto> SetMemberAsync(Guid workspaceId, SetWorkspaceMemberRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = AccessControl.Normalize(request.Email);
        if (email.Length is < 3 or > 320 || email.IndexOf('@') is <= 0 || email.EndsWith('@') || email.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Enter a valid email address.", nameof(request));
        }

        if (!Enum.IsDefined(request.Role))
        {
            throw new ArgumentException("Unknown role.", nameof(request));
        }

        await RequireWorkspaceRoleAsync(workspaceId, WorkspaceRole.Owner, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var role = AccessControl.ToDomain(request.Role);
        var member = await db.Set<WorkspaceMember>().FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserEmail == email, cancellationToken);
        if (member is null)
        {
            member = db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = workspaceId, UserEmail = email, Role = role }).Entity;
        }
        else
        {
            if (member.Role == Domain.Identity.WorkspaceRole.Owner && role != Domain.Identity.WorkspaceRole.Owner)
            {
                await EnsureAnotherOwnerAsync(db, workspaceId, cancellationToken);
            }

            member.Role = role;
        }

        await db.SaveChangesAsync(cancellationToken);
        var name = await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == email).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken);
        return ToDto(member, name);
    }

    public async Task RemoveMemberAsync(Guid workspaceId, string email, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceRoleAsync(workspaceId, WorkspaceRole.Owner, cancellationToken);
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var member = await db.Set<WorkspaceMember>().FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserEmail == email, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"{email} isn't a member of this workspace.");
        if (member.Role == Domain.Identity.WorkspaceRole.Owner)
        {
            await EnsureAnotherOwnerAsync(db, workspaceId, cancellationToken);
        }

        db.Set<WorkspaceMember>().Remove(member);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RequireWorkspaceRoleAsync(Guid workspaceId, WorkspaceRole minimum, CancellationToken cancellationToken)
    {
        access.RequireSignedIn();
        var role = await access.GetWorkspaceRoleAsync(workspaceId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Workspace", workspaceId);
        if (role < minimum)
        {
            throw new WorkbenchForbiddenException("Only the workspace's owners can manage its members.");
        }
    }

    private static async Task EnsureAnotherOwnerAsync(IWorkbenchDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        var owners = await db.Set<WorkspaceMember>().CountAsync(m => m.WorkspaceId == workspaceId && m.Role == Domain.Identity.WorkspaceRole.Owner, cancellationToken);
        if (owners <= 1)
        {
            throw new ArgumentException("A workspace needs at least one owner. Make someone else an owner first.");
        }
    }

    private static WorkspaceMemberDto ToDto(WorkspaceMember m, string? displayName) =>
        new(m.WorkspaceId, m.UserEmail, displayName, AccessControl.ToContract(m.Role)!.Value);
}
