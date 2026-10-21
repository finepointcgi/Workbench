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

/// <summary>
/// A role as it appears on a user, an invite, an API key or an approval step. On a user, <paramref name="Scope"/> is
/// where the role applies (null or <see cref="RoleScope.Everywhere"/>: everywhere) and <paramref name="ScopeText"/> says so
/// in words ("company B", "org units ENG, OPS").
/// </summary>
public sealed record RoleRef(Guid Id, string Name, bool IsBuiltIn, RoleScope? Scope = null, string? ScopeText = null)
{
    /// <summary>"Finance" or "Finance (company B)".</summary>
    public string Label => ScopeText is { Length: > 0 } s ? $"{Name} ({s})" : Name;
}

/// <summary>What a role assignment can be limited to.</summary>
public enum RoleScopeKind
{
    /// <summary>No limit: the role applies everywhere.</summary>
    Everywhere,

    /// <summary>
    /// Only while the person works in one of these companies (the company switcher). The company filters then show only
    /// that company's records.
    /// </summary>
    Companies,

    /// <summary>
    /// Records of these org units and their sub-units: employees in them, and projects whose manager is in them (and those
    /// projects' contracts).
    /// </summary>
    OrgUnits,

    /// <summary>These projects (and their contracts, for contract-level permissions).</summary>
    Projects,

    /// <summary>These contracts and their projects.</summary>
    Contracts,
}

/// <summary>Where a role assignment applies. <see cref="Ids"/> are company, org unit, project or contract ids.</summary>
public sealed record RoleScope(RoleScopeKind Kind, IReadOnlyList<Guid> Ids)
{
    public static RoleScope Everywhere { get; } = new(RoleScopeKind.Everywhere, []);

    public bool IsEverywhere => Kind == RoleScopeKind.Everywhere || Ids.Count == 0;

    /// <summary>The same scope with ids in a stable order (for comparing).</summary>
    public RoleScope Normalized => IsEverywhere ? Everywhere : this with { Ids = Ids.Distinct().Order().ToList() };

    public bool SameAs(RoleScope? other) =>
        Normalized.Kind == (other ?? Everywhere).Normalized.Kind && Normalized.Ids.SequenceEqual((other ?? Everywhere).Normalized.Ids);
}

/// <summary>One role to give someone, optionally limited (<see cref="Scope"/> null = everywhere).</summary>
public sealed record RoleAssignment(Guid RoleId, RoleScope? Scope = null);

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
    /// Replaces the roles a user holds. Roles they keep keep their scope; new ones apply everywhere. Refuses to leave the
    /// organization without an active administrator, and refuses to remove your own Admin role.
    /// </summary>
    Task<UserRolesDto> SetUserRolesAsync(string email, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the roles a user holds, each with its scope (a company, org unit, project or contract limit, or everywhere).
    /// The built-in Admin role can't be limited.
    /// </summary>
    Task<UserRolesDto> SetUserRoleAssignmentsAsync(string email, IReadOnlyList<RoleAssignment> assignments, CancellationToken cancellationToken = default);

    /// <summary>The companies, org units, projects and contracts a role can be limited to, for the scope picker.</summary>
    Task<RoleScopeOptionsDto> GetScopeOptionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Something a role can be limited to.</summary>
public sealed record ScopeOption(Guid Id, string Code, string Name);

/// <summary>Everything a role can be limited to, by kind.</summary>
public sealed record RoleScopeOptionsDto(
    IReadOnlyList<ScopeOption> Companies,
    IReadOnlyList<ScopeOption> OrgUnits,
    IReadOnlyList<ScopeOption> Projects,
    IReadOnlyList<ScopeOption> Contracts);
