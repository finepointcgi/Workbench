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

    /// <summary>
    /// Runs projects: sets up the projects they manage (WBS, charge assignments, plan sheets), edits their draft budgets and
    /// EACs, staffs them, approves what's charged to them and assigns tasks. Scoped to projects they manage; no cost rates.
    /// </summary>
    ProjectManager = 16,

    /// <summary>
    /// Human resources: personnel records (including EEO and veteran data), positions, compensation, benefits and
    /// onboarding/offboarding. Administrators can do all of it too.
    /// </summary>
    Hr = 32,
    /// Runs materials: the part master, warehouses, inventory transactions, BOMs and routings, work orders and MRP planning
    /// (firming planned buys into requisitions). Sees inventory values but not labor cost rates.
    /// </summary>
    Materials = 64,
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

    /// <summary>ProjectManager or Admin: the org role, not "manages some project" (see <c>Project.ProjectManagerId</c>).</summary>
    public static bool IsProjectManager(this OrgRoles roles) => (roles & (OrgRoles.ProjectManager | OrgRoles.Admin)) != 0;

    /// <summary>HR or Admin: sees and changes personnel records, EEO/veteran data, compensation and benefits.</summary>
    public static bool IsHr(this OrgRoles roles) => (roles & (OrgRoles.Hr | OrgRoles.Admin)) != 0;

    /// <summary>ProjectManager, Finance or Admin: may create projects and set up the ones they manage (Finance/Admin: every one).</summary>
    public static bool CanManageProjects(this OrgRoles roles) => (roles & (OrgRoles.ProjectManager | OrgRoles.Finance | OrgRoles.Admin)) != 0;

    /// <summary>Materials or Admin: the materials org role (Finance reads materials and costs through <see cref="CanViewMaterials"/>).</summary>
    public static bool IsMaterials(this OrgRoles roles) => (roles & (OrgRoles.Materials | OrgRoles.Admin)) != 0;

    /// <summary>Materials, Finance or Admin: may see parts, inventory, work orders, MRP and inventory reports.</summary>
    public static bool CanViewMaterials(this OrgRoles roles) => (roles & (OrgRoles.Materials | OrgRoles.Finance | OrgRoles.Admin)) != 0;

    /// <summary>Every assignable organization role, in display order (Employee is implicit).</summary>
    public static IReadOnlyList<OrgRoles> All { get; } =
        [OrgRoles.Admin, OrgRoles.Finance, OrgRoles.ProjectManager, OrgRoles.Timekeeper, OrgRoles.ResourceManager, OrgRoles.Hr, OrgRoles.Materials];

    /// <summary>Every role flag combined (what the system user has).</summary>
    public const OrgRoles Everything = OrgRoles.Admin | OrgRoles.Finance | OrgRoles.Timekeeper | OrgRoles.ResourceManager | OrgRoles.ProjectManager | OrgRoles.Hr | OrgRoles.Materials;

    /// <summary>A role's name for people: "Project manager", "Resource manager", or the enum name.</summary>
    public static string Label(OrgRoles role) => role switch
    {
        OrgRoles.ProjectManager => "Project manager",
        OrgRoles.ResourceManager => "Resource manager",
        OrgRoles.Hr => "HR",
        _ => role.ToString(),
    };
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

    public bool IsHr => Roles.IsHr();
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
