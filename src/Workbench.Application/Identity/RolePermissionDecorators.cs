using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

/// <summary>
/// Admin › Roles: roles are managed with "Manage roles", who holds them with "Administer users". Nobody can create a
/// role, or give someone a role, with a permission they don't hold themselves (so a user administrator can't make
/// themselves an accountant), and API keys can't manage roles at all.
/// </summary>
public sealed class PermissionCheckingRoleService(IRoleService inner, AccessControl access) : IRoleService
{
    private const string Manage = "manage roles";

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await RequireAnyAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoleRef>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return await inner.ListNamesAsync(cancellationToken);
    }

    public async Task<RoleDto> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await RequireAnyAsync(cancellationToken);
        return await inner.GetAsync(roleId, cancellationToken);
    }

    public async Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        await RequireManageAsync(cancellationToken);
        await access.RequireCanGrantAsync(request.Permissions ?? [], cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<RoleDto> UpdateAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        await RequireManageAsync(cancellationToken);
        var current = await inner.GetAsync(roleId, cancellationToken);
        await access.RequireCanGrantAsync((request.Permissions ?? []).Except(current.Permissions), cancellationToken);
        return await inner.UpdateAsync(roleId, request, cancellationToken);
    }

    public async Task<RoleDto> CloneAsync(Guid roleId, string name, CancellationToken cancellationToken = default)
    {
        await RequireManageAsync(cancellationToken);
        await access.RequireCanGrantAsync((await inner.GetAsync(roleId, cancellationToken)).Permissions, cancellationToken);
        return await inner.CloneAsync(roleId, name, cancellationToken);
    }

    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await RequireManageAsync(cancellationToken);
        await inner.DeleteAsync(roleId, cancellationToken);
    }

    public async Task<IReadOnlyList<UserRolesDto>> ListMembersAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await RequireAnyAsync(cancellationToken);
        return await inner.ListMembersAsync(roleId, cancellationToken);
    }

    public async Task<UserRolesDto> GetUserRolesAsync(string email, CancellationToken cancellationToken = default)
    {
        await RequireAnyAsync(cancellationToken);
        return await inner.GetUserRolesAsync(email, cancellationToken);
    }

    public async Task<UserRolesDto> SetUserRolesAsync(string email, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        RequireNotApiKey();
        await access.RequireAsync(Permission.AdminUsers, "change people's roles", cancellationToken);
        await access.RequireCanGrantNewRolesAsync(email, roleIds ?? [], cancellationToken);
        return await inner.SetUserRolesAsync(email, roleIds ?? [], cancellationToken);
    }

    private async Task RequireManageAsync(CancellationToken ct)
    {
        RequireNotApiKey();
        await access.RequireAsync(Permission.AdminRoles, Manage, ct);
    }

    // Reading roles: role managers and user administrators (who pick roles for people).
    private async Task RequireAnyAsync(CancellationToken ct)
    {
        RequireNotApiKey();
        await access.RequireAsync(p => p.HasAny(Permission.AdminRoles, Permission.AdminUsers),
            $"You need the \"{PermissionCatalog.NameOf(Permission.AdminRoles)}\" or \"{PermissionCatalog.NameOf(Permission.AdminUsers)}\" permission to see roles.", ct);
    }

    private void RequireNotApiKey()
    {
        access.RequireSignedIn();
        if (Integrations.ApiKeyPrincipal.IsServiceAccount(access.Email))
        {
            throw new Contracts.WorkbenchForbiddenException("API keys can't manage roles.");
        }
    }
}
