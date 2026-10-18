namespace Workbench.Contracts.Identity;

/// <summary>
/// Organization-level roles, alongside the per-workspace <see cref="WorkspaceRole"/>s. Everyone is an
/// <see cref="Employee"/>; the others combine. <see cref="Admin"/> can do everything.
/// </summary>
[Flags]
public enum OrgRoles
{
    /// <summary>The default: records their own time and expenses.</summary>
    Employee = 0,

    /// <summary>Can view everyone's timesheets.</summary>
    Timekeeper = 1,

    /// <summary>Sees and changes cost rates, bill rates, contracts and budgets; sets up projects; closes fiscal periods.</summary>
    Finance = 2,

    /// <summary>Everything, including users and every workspace.</summary>
    Admin = 4,

    /// <summary>Staffs every project: manages anyone's allocations and sees capacity and utilization (no cost rates).</summary>
    ResourceManager = 8,
}

public static class OrgRoleChecks
{
    public static bool IsAdmin(this OrgRoles roles) => roles.HasFlag(OrgRoles.Admin);

    /// <summary>Finance or Admin.</summary>
    public static bool IsFinance(this OrgRoles roles) => (roles & (OrgRoles.Finance | OrgRoles.Admin)) != 0;

    /// <summary>Timekeeper, Finance or Admin: may see everyone's timesheets.</summary>
    public static bool CanViewAllTimesheets(this OrgRoles roles) => (roles & (OrgRoles.Timekeeper | OrgRoles.Finance | OrgRoles.Admin)) != 0;

    /// <summary>ResourceManager or Admin: manages every project's allocations.</summary>
    public static bool IsResourceManager(this OrgRoles roles) => (roles & (OrgRoles.ResourceManager | OrgRoles.Admin)) != 0;
}

public sealed record UserDto(string Email, string DisplayName, OrgRoles Roles, DateTime CreatedAtUtc, DateTime? LastSeenAtUtc)
{
    public bool IsAdmin => Roles.IsAdmin();
}

/// <summary>People who have signed in (or were added). Used for Person columns, mentions, sharing and admin.</summary>
public interface IUserDirectory
{
    /// <summary>The signed-in user, provisioning them if needed.</summary>
    Task<UserDto> GetMeAsync(CancellationToken cancellationToken = default);

    /// <summary>Users whose email or name contains <paramref name="query"/> (all when blank), ordered by name.</summary>
    Task<IReadOnlyList<UserDto>> SearchAsync(string? query, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a user's organization roles. Admins only. Throws <see cref="ArgumentException"/> when it would remove
    /// the last admin.
    /// </summary>
    Task<UserDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default);
}

/// <summary>
/// Who is signed in, for permission decisions in other features (for example a supervisor approving a timesheet).
/// <see cref="EmployeeId"/> links the sign-in to the employee record with the same email, when there is one.
/// </summary>
public sealed record CurrentUserInfo(string Email, string DisplayName, OrgRoles Roles, Guid? EmployeeId)
{
    public bool IsAdmin => Roles.IsAdmin();

    public bool IsFinance => Roles.IsFinance();

    public bool CanViewAllTimesheets => Roles.CanViewAllTimesheets();
}

public interface ICurrentUserContext
{
    /// <summary>The signed-in user's roles and employee id. Read fresh each call, so role changes apply at once.</summary>
    Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// How the UI signs the user out. The web host sends the browser to the sign-out endpoint; the desktop app forgets
/// its tokens.
/// </summary>
public interface ISignOutService
{
    Task SignOutAsync();
}
