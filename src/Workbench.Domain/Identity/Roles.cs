namespace Workbench.Domain.Identity;

/// <summary>
/// A role: a named bundle of permissions assigned to users. The ten built-in roles have fixed ids and
/// <see cref="BuiltIn"/> set (the <c>OrgRoles</c> flag value; 0 is Employee); their permissions come from the
/// permission catalog in code, not from <see cref="RolePermission"/> rows. Custom roles store their permissions as rows.
/// </summary>
public sealed class Role : Entity
{
    public required string Name { get; set; }

    /// <summary>Lower-case trimmed <see cref="Name"/>, unique.</summary>
    public required string NormalizedName { get; set; }

    public string Description { get; set; } = "";

    /// <summary>The <c>OrgRoles</c> flag of a built-in role (0 = Employee); null for a custom role.</summary>
    public int? BuiltIn { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

/// <summary>One permission (a <c>Permission</c> catalog key) granted by a custom role.</summary>
public sealed class RolePermission : Entity
{
    public Guid RoleId { get; set; }

    public required string Permission { get; set; }
}

/// <summary>
/// A user holding a role. A user's permissions are the union of every role they hold. An assignment can be limited to
/// some companies, org units, projects or contracts (<see cref="ScopeKind"/> and <see cref="ScopeIds"/>); unscoped means
/// everywhere.
/// </summary>
public sealed class UserRole : Entity
{
    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    /// <summary>The <c>RoleScopeKind</c>: 0 everywhere, 1 companies, 2 org units (and their sub-units), 3 projects, 4 contracts.</summary>
    public int ScopeKind { get; set; }

    /// <summary>Comma-separated ids of the companies, org units, projects or contracts the role is limited to.</summary>
    public string? ScopeIds { get; set; }

    public DateTime AssignedAtUtc { get; set; }

    public string? AssignedBy { get; set; }
}

/// <summary>
/// A permission a feature registered at run time for this organization (a custom field, an automation, a form). Built-in
/// roles it's granted to get <see cref="RolePermission"/> rows.
/// </summary>
public sealed class RegisteredPermission : Entity
{
    /// <summary>Unique, lower-case and dotted, never a built-in key.</summary>
    public required string Key { get; set; }

    public required string Area { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = "";

    /// <summary>The feature that registered it, e.g. <c>custom-fields</c>.</summary>
    public required string Source { get; set; }

    /// <summary>Built-in roles (<c>OrgRoles</c> flags) it's granted to.</summary>
    public int GrantedTo { get; set; }

    public bool Sensitive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
