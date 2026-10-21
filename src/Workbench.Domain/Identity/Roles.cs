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

/// <summary>A user holding a role. A user's permissions are the union of every role they hold.</summary>
public sealed class UserRole : Entity
{
    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    public DateTime AssignedAtUtc { get; set; }

    public string? AssignedBy { get; set; }
}
