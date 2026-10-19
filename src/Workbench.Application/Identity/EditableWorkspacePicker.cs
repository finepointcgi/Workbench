using Microsoft.EntityFrameworkCore;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Domain;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

/// <summary>
/// Puts a new sheet in the first workspace (by name) where the user is an Editor or Owner, and refuses when there
/// is none. Admins get the default choice. When there are no workspaces at all, it creates the default one with
/// the user as its Owner, so a fresh install isn't stuck. A <see cref="SheetWorkspacePreference"/> the user can edit wins.
/// </summary>
public sealed class EditableWorkspacePicker(AccessControl access, SheetWorkspacePreference? preference = null) : ISheetWorkspacePicker
{
    public async Task<Workspace?> PickAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        access.RequireSignedIn();
        if (!await db.Workspaces.AnyAsync(cancellationToken))
        {
            var created = db.Workspaces.Add(new Workspace { Name = SheetService.DefaultWorkspaceName }).Entity;
            db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = created.Id, UserEmail = access.Email, Role = WorkspaceRole.Owner });
            return created;
        }

        if (preference?.WorkspaceId is { } preferred
            && await access.GetWorkspaceRoleAsync(db, preferred, cancellationToken) is Contracts.Identity.WorkspaceRole.Editor or Contracts.Identity.WorkspaceRole.Owner)
        {
            return await db.Workspaces.FirstAsync(w => w.Id == preferred, cancellationToken);
        }

        if (await access.IsAdminAsync(db, cancellationToken))
        {
            return null;
        }

        var email = access.Email;
        var editable = await db.Set<WorkspaceMember>()
            .Where(m => m.UserEmail == email && (m.Role == WorkspaceRole.Editor || m.Role == WorkspaceRole.Owner))
            .Select(m => m.WorkspaceId)
            .ToListAsync(cancellationToken);
        return await db.Workspaces.Where(w => editable.Contains(w.Id)).OrderBy(w => w.Name).FirstOrDefaultAsync(cancellationToken)
            ?? throw new WorkbenchForbiddenException("You need Editor access to a workspace to create sheets.");
    }
}
