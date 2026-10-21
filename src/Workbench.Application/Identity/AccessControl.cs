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
/// Answers "what may the current user do here" from workspace memberships and the permissions of the roles the user holds. Nothing is cached:
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

    /// <summary>
    /// The user's built-in roles, every role they hold and the union of their permissions. The system user has every
    /// permission; nobody signed in (or a deactivated user) has none.
    /// </summary>
    public async Task<ResolvedAccess> GetAccessAsync(CancellationToken cancellationToken = default)
    {
        if (IsSystemUser)
        {
            return ResolvedAccess.Everything;
        }

        if (!IsAuthenticated)
        {
            return ResolvedAccess.None;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RoleStore.ResolveAsync(db, Email, cancellationToken);
    }

    /// <summary>The user's effective permissions: the union of every role they hold.</summary>
    public async Task<PermissionSet> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
        (await GetAccessAsync(cancellationToken)).Permissions;

    /// <summary>True when the user holds <paramref name="permission"/> (a <see cref="Permission"/> constant).</summary>
    public async Task<bool> HasAsync(string permission, CancellationToken cancellationToken = default) =>
        (await GetPermissionsAsync(cancellationToken)).Has(permission);

    /// <summary>
    /// Where the user holds <paramref name="permission"/>: everywhere, nowhere, or within the companies, org units,
    /// projects and contracts their scoped roles are limited to.
    /// </summary>
    public async Task<GrantScope> GetGrantScopeAsync(string permission, CancellationToken cancellationToken = default) =>
        RoleScopes.ScopeOf(await GetAccessAsync(cancellationToken), permission);

    /// <summary>True when the user holds <paramref name="permission"/> for this project (everywhere, or a scope covering it).</summary>
    public Task<bool> HasForProjectAsync(string permission, Guid projectId, CancellationToken cancellationToken = default) =>
        HasForAsync(permission, (db, ct) => RoleScopes.ForProjectAsync(db, projectId, ct), cancellationToken);

    /// <summary>True when the user holds <paramref name="permission"/> for this contract.</summary>
    public Task<bool> HasForContractAsync(string permission, Guid contractId, CancellationToken cancellationToken = default) =>
        HasForAsync(permission, (db, ct) => RoleScopes.ForContractAsync(db, contractId, ct), cancellationToken);

    /// <summary>True when the user holds <paramref name="permission"/> for this employee (their company or org unit).</summary>
    public Task<bool> HasForEmployeeAsync(string permission, Guid employeeId, CancellationToken cancellationToken = default) =>
        HasForAsync(permission, (db, ct) => RoleScopes.ForEmployeeAsync(db, employeeId, ct), cancellationToken);

    /// <summary>The projects the user holds <paramref name="permission"/> for; null means every project.</summary>
    public Task<IReadOnlySet<Guid>?> ProjectsWithAsync(string permission, CancellationToken cancellationToken = default) =>
        InScopeAsync(permission, RoleScopes.ProjectIdsAsync, cancellationToken);

    /// <summary>The contracts the user holds <paramref name="permission"/> for; null means every contract.</summary>
    public Task<IReadOnlySet<Guid>?> ContractsWithAsync(string permission, CancellationToken cancellationToken = default) =>
        InScopeAsync(permission, RoleScopes.ContractIdsAsync, cancellationToken);

    /// <summary>The employees the user holds <paramref name="permission"/> for; null means everyone.</summary>
    public Task<IReadOnlySet<Guid>?> EmployeesWithAsync(string permission, CancellationToken cancellationToken = default) =>
        InScopeAsync(permission, RoleScopes.EmployeeIdsAsync, cancellationToken);

    private async Task<bool> HasForAsync(string permission, Func<IWorkbenchDbContext, CancellationToken, Task<ResourceRef>> resource, CancellationToken cancellationToken)
    {
        var scope = await GetGrantScopeAsync(permission, cancellationToken);
        if (scope.All || scope.IsNowhere)
        {
            return scope.All;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return scope.Covers(await resource(db, cancellationToken));
    }

    private async Task<IReadOnlySet<Guid>?> InScopeAsync(
        string permission, Func<IWorkbenchDbContext, GrantScope, CancellationToken, Task<IReadOnlySet<Guid>?>> ids, CancellationToken cancellationToken)
    {
        var scope = await GetGrantScopeAsync(permission, cancellationToken);
        if (scope.All || scope.IsNowhere)
        {
            return scope.All ? null : new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ids(db, scope, cancellationToken);
    }

    /// <summary>
    /// The built-in roles the user holds, as flags (a compatibility view: custom roles aren't in it). Decide access with
    /// <see cref="HasAsync"/> or <see cref="RequireAsync(string, string, CancellationToken)"/> instead.
    /// </summary>
    public async Task<OrgRoles> GetOrgRolesAsync(CancellationToken cancellationToken = default) =>
        (await GetAccessAsync(cancellationToken)).BuiltIn;

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

    /// <summary>
    /// Throws <see cref="WorkbenchForbiddenException"/> (recorded as a denial) unless the user holds
    /// <paramref name="permission"/>. <paramref name="what"/> finishes "You need the … permission to …".
    /// </summary>
    /// <summary>
    /// Compatibility check on the built-in roles only (custom roles aren't in <see cref="GetOrgRolesAsync"/>), for rules
    /// still written as "who holds these built-in roles", such as which roles manage a customizable record type. New
    /// checks use <see cref="RequireAsync(string, string, CancellationToken)"/> with a permission.
    /// </summary>
    public async Task RequireRoleAsync(Func<OrgRoles, bool> allowed, string message, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        if (!allowed(await GetOrgRolesAsync(cancellationToken)))
        {
            throw await DeniedAsync(message, cancellationToken);
        }
    }

    public async Task RequireAsync(string permission, string what, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        if (!await HasAsync(permission, cancellationToken))
        {
            throw await DeniedAsync($"You need the \"{PermissionCatalog.NameOf(permission)}\" permission to {what}.", cancellationToken);
        }
    }

    /// <summary>Throws <see cref="WorkbenchForbiddenException"/> (recorded as a denial) unless the user's permissions pass <paramref name="allowed"/>.</summary>
    public async Task RequireAsync(Func<PermissionSet, bool> allowed, string message, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        if (!allowed(await GetPermissionsAsync(cancellationToken)))
        {
            throw await DeniedAsync(message, cancellationToken);
        }
    }

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

    /// <summary>Ids of the workspaces the user belongs to, or null for those who see every workspace (<see cref="Permission.AdminAllWorkspaces"/>).</summary>
    public async Task<IReadOnlySet<Guid>?> GetVisibleWorkspaceIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await SeesAllWorkspacesAsync(db, cancellationToken))
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

    /// <summary>
    /// Nobody can give out more than they have: throws <see cref="WorkbenchForbiddenException"/> (recorded) when
    /// <paramref name="permissions"/> include one the user doesn't hold.
    /// </summary>
    public async Task RequireCanGrantAsync(IEnumerable<string> permissions, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        var mine = await GetPermissionsAsync(cancellationToken);
        // Unknown keys are left to the caller's validation (a clearer 400).
        var missing = permissions.Where(p => PermissionCatalog.Exists(p) && !mine.Has(p)).Distinct().Select(PermissionCatalog.NameOf).ToList();
        if (missing.Count > 0)
        {
            throw await DeniedAsync($"You can't give out permissions you don't have yourself: {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : "")}.", cancellationToken);
        }
    }

    /// <summary>
    /// Like <see cref="RequireCanGrantRolesAsync"/> for the roles in <paramref name="target"/> that the user with
    /// <paramref name="email"/> doesn't hold yet (taking roles away needs no such check).
    /// </summary>
    public async Task RequireCanGrantNewRolesAsync(string email, IReadOnlyCollection<Guid> target, CancellationToken cancellationToken = default)
    {
        email = Normalize(email);
        List<Guid> held;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var userId = await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == email).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
            held = userId is { } id ? await RoleStore.RoleIdsOfAsync(db, id, cancellationToken) : [];
        }

        await RequireCanGrantRolesAsync(target.Except(held).ToList(), cancellationToken);
    }

    /// <summary>Like <see cref="RequireCanGrantAsync(IEnumerable{string}, CancellationToken)"/> for every permission the roles grant.</summary>
    public async Task RequireCanGrantRolesAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        if (roleIds.Count == 0 || IsSystemUser)
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var granted = await RoleStore.ResolveRoleIdsAsync(db, roleIds, cancellationToken);
        await RequireCanGrantAsync(granted.Permissions, cancellationToken);
    }

    /// <summary>A denial with <paramref name="message"/>, recorded like the role checks above (for rules decided elsewhere).</summary>
    public Task<WorkbenchForbiddenException> DenyAsync(string message, CancellationToken cancellationToken = default) => DeniedAsync(message, cancellationToken);

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

    /// <summary>True for holders of <see cref="Permission.AdminAllWorkspaces"/> (and the system user): Owner of every workspace.</summary>
    internal async Task<bool> SeesAllWorkspacesAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (IsSystemUser)
        {
            return true;
        }

        return IsAuthenticated && (await RoleStore.ResolveAsync(db, Email, cancellationToken)).Permissions.Has(Permission.AdminAllWorkspaces);
    }

    internal async Task<WorkspaceRole?> GetWorkspaceRoleAsync(IWorkbenchDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (await SeesAllWorkspacesAsync(db, cancellationToken))
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
