namespace Workbench.Domain.Identity;

/// <summary>A person who has signed in at least once (or was seeded). Created just in time on first sign-in.</summary>
public sealed class AppUser : Entity
{
    /// <summary>Lower-case, unique. Memberships, comments and Person cells refer to users by email.</summary>
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Organization role Admin: sees every workspace and can do everything.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Organization role Finance: rates, contracts, budgets, project setup and fiscal close.</summary>
    public bool IsFinance { get; set; }

    /// <summary>Organization role Timekeeper: can view everyone's timesheets.</summary>
    public bool IsTimekeeper { get; set; }

    /// <summary>Organization role ResourceManager: manages every project's allocations (resource planning).</summary>
    public bool IsResourceManager { get; set; }

    /// <summary>Organization role ProjectManager: sets up, staffs and approves for the projects they manage; assigns tasks.</summary>
    public bool IsProjectManager { get; set; }

    /// <summary>Organization role Materials: parts, inventory, BOMs, work orders and MRP planning.</summary>
    public bool IsMaterials { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }

    /// <summary>Last interactive sign-in (OIDC or dev). <see cref="LastSeenAtUtc"/> also moves with API calls.</summary>
    public DateTime? LastSignInAtUtc { get; set; }

    /// <summary>
    /// False once deactivated (by an admin, SCIM or an access review). Deactivated users can't sign in and their
    /// sessions and tokens stop working; they're never deleted, so their history keeps its author.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime? DeactivatedAtUtc { get; set; }
    public string? DeactivatedBy { get; set; }

    /// <summary>Sessions and tokens issued before this time are rejected ("sign out everywhere").</summary>
    public DateTime? SessionsRevokedAtUtc { get; set; }

    /// <summary>An explicit link to an employee record. When null, the employee with the same email is used.</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>True when an admin unlinked the user: no employee, even if one has the same email.</summary>
    public bool EmployeeUnlinked { get; set; }

    /// <summary>The identity provider's id for this user (SCIM <c>externalId</c>).</summary>
    public string? ExternalId { get; set; }

    /// <summary>Created or last changed through SCIM provisioning.</summary>
    public bool ScimManaged { get; set; }
}
