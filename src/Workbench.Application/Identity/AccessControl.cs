using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using DomainRole = Workbench.Domain.Identity.WorkspaceRole;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Identity;

/// <summary>
/// Answers "what may the current user do here" from workspace memberships and the admin flag. Nothing is cached:
/// a Blazor circuit keeps its scope for hours, and role changes should apply on the next call.
/// </summary>
public sealed class AccessControl(
    IWorkbenchDbContextFactory dbFactory, ICurrentUser currentUser, Security.SecurityEventLog? securityLog = null, SystemExecutionContext? system = null,
    RuleAuthorExecutionContext? ruleAuthor = null)
{
    /// <summary>Who is checked: the signed-in user, or the rule's author inside a <see cref="RuleAuthorExecutionContext"/>.</summary>
    public string Email => ruleAuthor?.Email ?? Normalize(currentUser.Email);

    /// <summary>
    /// True only inside a trusted background job's scope (<see cref="SystemExecutionContext"/>). It passes no role check;
    /// a permission decorator may accept it on an explicitly listed read method, never on a write.
    /// </summary>
    public bool IsSystemReader => system?.IsActive == true;

    public bool IsAuthenticated => ruleAuthor?.IsActive == true || IsSystemUser || (currentUser.IsAuthenticated && Email.Length > 0);

    // The worker's all-powerful user, unless this scope acts for a rule's author (who gets only their own roles).
    private bool IsSystemUser => currentUser is SystemCurrentUser && ruleAuthor?.IsActive != true;

    public async Task<bool> IsAdminAsync(CancellationToken cancellationToken = default)
    {
        if (IsSystemUser)
        {
            return true;
        }

        if (!IsAuthenticated)
        {
            return false;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await IsAdminAsync(db, cancellationToken);
    }

    /// <summary>The user's organization roles. The system user has them all; nobody signed in has none.</summary>
    public async Task<OrgRoles> GetOrgRolesAsync(CancellationToken cancellationToken = default)
    {
        if (IsSystemUser)
        {
            return OrgRoleChecks.Everything;
        }

        if (!IsAuthenticated)
        {
            return OrgRoles.Employee;
        }

        var email = Email;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.Email == email && u.IsActive)
            .Select(u => new { u.IsAdmin, u.IsFinance, u.IsTimekeeper, u.IsResourceManager, u.IsProjectManager })
            .FirstOrDefaultAsync(cancellationToken);
        return user is null ? OrgRoles.Employee : ToRoles(user.IsAdmin, user.IsFinance, user.IsTimekeeper, user.IsResourceManager, user.IsProjectManager);
    }

    /// <summary>The employee record with the signed-in user's email, if any.</summary>
    public async Task<Guid?> GetEmployeeIdAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated || IsSystemUser)
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await EmployeeLinks.FindForUserAsync(db, Email, cancellationToken))?.Id;
    }

    /// <summary>Throws <see cref="WorkbenchForbiddenException"/> unless the user is in Finance or an Admin.</summary>
    public async Task RequireFinanceAsync(string what, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        if (!(await GetOrgRolesAsync(cancellationToken)).IsFinance())
        {
            throw await DeniedAsync($"Only Finance and administrators can {what}.", cancellationToken);
        }
    }

    internal static OrgRoles ToRoles(bool admin, bool finance, bool timekeeper, bool resourceManager = false, bool projectManager = false) =>
        (admin ? OrgRoles.Admin : 0) | (finance ? OrgRoles.Finance : 0) | (timekeeper ? OrgRoles.Timekeeper : 0)
        | (resourceManager ? OrgRoles.ResourceManager : 0) | (projectManager ? OrgRoles.ProjectManager : 0);

    /// <summary>The user's role on a sheet (Owner for admins), or null when they can't see it or it doesn't exist.</summary>
    public async Task<WorkspaceRole?> GetSheetRoleAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var workspaceId = await db.Sheets.AsNoTracking()
            .Where(s => s.Id == sheetId)
            .Select(s => (Guid?)s.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken);
        return workspaceId is null ? null : await GetWorkspaceRoleAsync(db, workspaceId.Value, cancellationToken);
    }

    /// <summary>The user's role in a workspace (Owner for admins), or null when they aren't a member or it doesn't exist.</summary>
    public async Task<WorkspaceRole?> GetWorkspaceRoleAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await GetWorkspaceRoleAsync(db, workspaceId, cancellationToken);
    }

    /// <summary>Ids of the workspaces the user belongs to, or null for admins (who see every workspace).</summary>
    public async Task<IReadOnlySet<Guid>?> GetVisibleWorkspaceIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await IsAdminAsync(db, cancellationToken))
        {
            return null;
        }

        var email = Email;
        var ids = await db.Set<WorkspaceMember>().AsNoTracking()
            .Where(m => m.UserEmail == email)
            .Select(m => m.WorkspaceId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    /// <summary>Ids of the sheets in the given workspaces.</summary>
    public async Task<IReadOnlySet<Guid>> GetSheetIdsInAsync(IReadOnlySet<Guid> workspaceIds, CancellationToken cancellationToken = default)
    {
        if (workspaceIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = workspaceIds.ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheets = await db.Sheets.AsNoTracking().Where(s => ids.Contains(s.WorkspaceId)).Select(s => s.Id).ToListAsync(cancellationToken);
        return sheets.ToHashSet();
    }

    /// <summary>
    /// Throws <see cref="WorkbenchNotFoundException"/> when the user can't see the sheet, and
    /// <see cref="WorkbenchForbiddenException"/> when their role is below <paramref name="minimum"/>.
    /// </summary>
    public async Task RequireSheetRoleAsync(Guid sheetId, WorkspaceRole minimum, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        var role = await GetSheetRoleAsync(sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        if (role < minimum)
        {
            throw await DeniedAsync(minimum switch
            {
                WorkspaceRole.Commenter => "You need comment access to this sheet to do that.",
                WorkspaceRole.Editor => "You need edit access to this sheet to do that.",
                WorkspaceRole.Owner => "Only the workspace's owners can do that.",
                _ => "You don't have access to this sheet.",
            }, cancellationToken, $"sheet {sheetId}");
        }
    }

    public async Task RequireAdminAsync(CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        if (!await IsAdminAsync(cancellationToken))
        {
            throw await DeniedAsync("Only administrators can do that.", cancellationToken);
        }
    }

    /// <summary>Records the denial in the security event log (A.8.15) and returns the exception to throw.</summary>
    private async Task<WorkbenchForbiddenException> DeniedAsync(string message, CancellationToken cancellationToken, string? target = null)
    {
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(Contracts.Security.SecurityEventTypes.PermissionDenied, target, Contracts.Security.SecurityOutcomes.Denied, message, cancellationToken: cancellationToken);
        }

        return new WorkbenchForbiddenException(message);
    }

    public void RequireSignedIn()
    {
        if (!IsAuthenticated)
        {
            throw new WorkbenchForbiddenException("Sign in first.");
        }
    }

    internal async Task<bool> IsAdminAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (IsSystemUser)
        {
            return true;
        }

        var email = Email;
        return email.Length > 0 && await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.Email == email && u.IsAdmin && u.IsActive, cancellationToken);
    }

    internal async Task<WorkspaceRole?> GetWorkspaceRoleAsync(IWorkbenchDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (await IsAdminAsync(db, cancellationToken))
        {
            return await db.Workspaces.AnyAsync(w => w.Id == workspaceId, cancellationToken) ? WorkspaceRole.Owner : null;
        }

        var email = Email;
        var role = await db.Set<WorkspaceMember>().AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserEmail == email)
            .Select(m => (DomainRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        return ToContract(role);
    }

    internal static WorkspaceRole? ToContract(DomainRole? role) => role is null ? null : (WorkspaceRole)(int)role.Value;

    internal static DomainRole ToDomain(WorkspaceRole role) => (DomainRole)(int)role;

    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
}
