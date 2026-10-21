namespace Workbench.Contracts.Identity;

/// <summary>A role: a named bundle of permissions. Built-in roles (<see cref="BuiltIn"/> set) can be cloned but not changed.</summary>
public sealed record RoleDto(
    Guid Id,
    string Name,
    string Description,
    OrgRoles? BuiltIn,
    IReadOnlyList<string> Permissions,
    int UserCount,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy)
{
    public bool IsBuiltIn => BuiltIn is not null;

    /// <summary>Segregation-of-duties conflicts among this role's permissions.</summary>
    public IReadOnlyList<PermissionConflict> Conflicts => PermissionCatalog.ConflictsIn(Permissions);
}

/// <summary>A role as it appears on a user, an invite, an API key or an approval step.</summary>
public sealed record RoleRef(Guid Id, string Name, bool IsBuiltIn);

/// <summary>Creates or replaces a custom role. Unknown permission keys are refused.</summary>
public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions);

/// <summary>A user and the roles they hold (Employee is implicit and not listed).</summary>
public sealed record UserRolesDto(string Email, string DisplayName, bool IsActive, IReadOnlyList<RoleRef> Roles, IReadOnlyList<string> Permissions)
{
    public IReadOnlyList<PermissionConflict> Conflicts => PermissionCatalog.ConflictsIn(Permissions);
}

/// <summary>
/// Admin › Roles. Every method needs <see cref="Permission.AdminRoles"/> (changing someone's roles needs
/// <see cref="Permission.AdminUsers"/>), and nobody can grant a permission they don't hold themselves. Every change is
/// audit logged with before and after.
/// </summary>
public interface IRoleService
{
    /// <summary>Every role, built-in first, then custom by name.</summary>
    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Every role's id and name, for pickers (sharing a report with a role). Anyone signed in.</summary>
    Task<IReadOnlyList<RoleRef>> ListNamesAsync(CancellationToken cancellationToken = default);

    Task<RoleDto> GetAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces a custom role's name, description and permissions. Built-in roles throw <see cref="ArgumentException"/>.</summary>
    Task<RoleDto> UpdateAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken = default);

    /// <summary>A new custom role with the same permissions as <paramref name="roleId"/> (built-in or custom).</summary>
    Task<RoleDto> CloneAsync(Guid roleId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a custom role and removes it from everyone who held it. Throws <see cref="ArgumentException"/> for a built-in
    /// role, or one an approval workflow, SSO group mapping, API key or pending invite still uses.
    /// </summary>
    Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>The users who hold the role (active first).</summary>
    Task<IReadOnlyList<UserRolesDto>> ListMembersAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<UserRolesDto> GetUserRolesAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the roles a user holds. Refuses to leave the organization without an active administrator, and refuses to
    /// remove your own Admin role.
    /// </summary>
    Task<UserRolesDto> SetUserRolesAsync(string email, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default);
}
