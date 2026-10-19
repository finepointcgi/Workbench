using Microsoft.EntityFrameworkCore;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>
/// A project manager (with the Project manager org role) is Owner of the workspace that holds their project's plan sheet,
/// by default: set when the project is created or its manager changes, when its plan sheet is created, and when someone
/// who manages projects is given the role. It only ever raises a membership to Owner; an Owner can change it afterwards.
/// The caller saves.
/// </summary>
public static class ProjectWorkspaceOwnership
{
    /// <summary>Makes the project's manager Owner of its plan sheet's workspace, when they have the role.</summary>
    public static async Task EnsureForProjectAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var row = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.SheetId, p.ProjectManagerId })
            .FirstOrDefaultAsync(ct);
        if (row?.SheetId is not { } sheetId || row.ProjectManagerId is not { } managerId)
        {
            return;
        }

        var email = await ManagerEmailAsync(db, managerId, ct);
        if (email is null || !await HasRoleAsync(db, email, ct))
        {
            return;
        }

        var workspaceId = await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => (Guid?)s.WorkspaceId).FirstOrDefaultAsync(ct);
        if (workspaceId is { } id)
        {
            await MakeOwnerAsync(db, id, email, ct);
        }
    }

    /// <summary>Makes the user Owner of every workspace holding a plan sheet of a project they manage (after the role is given).</summary>
    public static async Task EnsureForManagerAsync(IWorkbenchDbContext db, AppUser user, CancellationToken ct)
    {
        if (!user.IsProjectManager)
        {
            return;
        }

        var employee = await EmployeeLinks.FindForUserAsync(db, user.Email, ct);
        if (employee is null)
        {
            return;
        }

        var sheetIds = await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == employee.Id && p.SheetId != null)
            .Select(p => p.SheetId!.Value).ToListAsync(ct);
        var workspaceIds = await db.Sheets.AsNoTracking().Where(s => sheetIds.Contains(s.Id)).Select(s => s.WorkspaceId).Distinct().ToListAsync(ct);
        foreach (var workspaceId in workspaceIds)
        {
            await MakeOwnerAsync(db, workspaceId, user.Email, ct);
        }
    }

    internal static async Task MakeOwnerAsync(IWorkbenchDbContext db, Guid workspaceId, string email, CancellationToken ct)
    {
        email = AccessControl.Normalize(email);
        var tracked = db.Set<WorkspaceMember>().Local.FirstOrDefault(m => m.WorkspaceId == workspaceId && m.UserEmail == email);
        var member = tracked ?? await db.Set<WorkspaceMember>().FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserEmail == email, ct);
        if (member is null)
        {
            db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = workspaceId, UserEmail = email, Role = WorkspaceRole.Owner });
        }
        else if (member.Role != WorkspaceRole.Owner)
        {
            member.Role = WorkspaceRole.Owner;
        }
    }

    private static async Task<string?> ManagerEmailAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct)
    {
        // An explicit user link wins over the employee's email (EmployeeLinks), so look for a user linked to the employee first.
        var linked = await db.Set<AppUser>().AsNoTracking().Where(u => u.EmployeeId == employeeId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (linked is not null)
        {
            return linked;
        }

        var email = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.Email).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(email) ? null : AccessControl.Normalize(email);
    }

    private static Task<bool> HasRoleAsync(IWorkbenchDbContext db, string email, CancellationToken ct) =>
        db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.Email == email && u.IsActive && u.IsProjectManager && !u.EmployeeUnlinked, ct);
}

/// <summary>
/// Where the next sheet goes, when a caller knows better than "the first workspace I can edit" (a project manager's new
/// plan sheet goes in their project's workspace). Scoped; inactive unless set. <see cref="EditableWorkspacePicker"/> uses
/// it only when the user can edit that workspace.
/// </summary>
public sealed class SheetWorkspacePreference
{
    public Guid? WorkspaceId { get; set; }
}
