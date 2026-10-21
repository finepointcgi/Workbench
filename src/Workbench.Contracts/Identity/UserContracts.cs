namespace Workbench.Contracts.Identity;

/// <summary>
/// The ten built-in organization roles, as flags (alongside the per-workspace <see cref="WorkspaceRole"/>s). Everyone is
/// an <see cref="Employee"/>; the others combine. Kept as a compatibility layer: roles are now bundles of
/// <see cref="Permission"/>s (<see cref="BuiltInRoles"/>, custom roles in Admin › Roles), and access is decided by
/// permissions, never by these flags.
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
    /// Business development: sees the capture pipeline (accounts, contacts, opportunities, forecast) and creates leads and
    /// opportunities; edits the opportunities they capture or manage the proposal for. Pipeline values are business-sensitive.
    /// </summary>
    BusinessDevelopment = 128,
    /// Prices proposals: sees employee cost rates (as labor rate averages), indirect rates and fee in proposal pricing, and
    /// builds, compares and locks prices. Nothing else in Finance. (256 leaves room for roles other branches add.)
    /// </summary>
    Pricing = 256,
}

/// <summary>
/// What the built-in role flags meant before permissions (for display, the API's role flags and tests). Don't decide access
/// with these: check a <see cref="Permission"/> (<c>AccessControl.RequireAsync</c>, <c>CurrentUserInfo.Can</c>), so custom
/// roles work too.
/// </summary>
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

    /// <summary>Business development or Admin: the org role (capture and proposal managers of an opportunity edit it without it).</summary>
    public static bool IsBusinessDevelopment(this OrgRoles roles) => (roles & (OrgRoles.BusinessDevelopment | OrgRoles.Admin)) != 0;

    /// <summary>Business development, Finance or Admin: sees the whole pipeline.</summary>
    public static bool CanViewPipeline(this OrgRoles roles) => (roles & (OrgRoles.BusinessDevelopment | OrgRoles.Finance | OrgRoles.Admin)) != 0;

    /// <summary>Pricing, Finance or Admin: may see and edit proposal prices (cost rates, indirect rates, fee).</summary>
    public static bool CanPrice(this OrgRoles roles) => (roles & (OrgRoles.Pricing | OrgRoles.Finance | OrgRoles.Admin)) != 0;

    /// <summary>Every assignable organization role, in display order (Employee is implicit).</summary>
    public static IReadOnlyList<OrgRoles> All { get; } =
        [OrgRoles.Admin, OrgRoles.Finance, OrgRoles.ProjectManager, OrgRoles.Timekeeper, OrgRoles.ResourceManager, OrgRoles.Hr, OrgRoles.Materials, OrgRoles.BusinessDevelopment, OrgRoles.Pricing];

    /// <summary>Every role flag combined (what the system user has).</summary>
    public const OrgRoles Everything = OrgRoles.Admin | OrgRoles.Finance | OrgRoles.Timekeeper | OrgRoles.ResourceManager | OrgRoles.ProjectManager | OrgRoles.Hr | OrgRoles.Materials | OrgRoles.BusinessDevelopment | OrgRoles.Pricing;

    /// <summary>A role's name for people: "Project manager", "Resource manager", or the enum name.</summary>
    public static string Label(OrgRoles role) => role switch
    {
        OrgRoles.ProjectManager => "Project manager",
        OrgRoles.ResourceManager => "Resource manager",
        OrgRoles.Hr => "HR",
        OrgRoles.BusinessDevelopment => "Business development",
        _ => role.ToString(),
    };
}

/// <summary>A user. <see cref="Roles"/> are the built-in roles held; <see cref="AssignedRoles"/> every role (built-in and custom).</summary>
public sealed record UserDto(string Email, string DisplayName, OrgRoles Roles, DateTime CreatedAtUtc, DateTime? LastSeenAtUtc, IReadOnlyList<RoleRef>? AssignedRoles = null)
{
    public bool IsAdmin => Roles.HasFlag(OrgRoles.Admin);
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
/// <see cref="Roles"/> lists the built-in roles held; <see cref="PermissionKeys"/> is the union of every role held
/// (built-in and custom). Decide with <see cref="Can"/>; when <see cref="PermissionKeys"/> is null (built from roles
/// alone, as tests do) the built-in roles' permissions apply.
/// </summary>
public sealed record CurrentUserInfo(
    string Email,
    string DisplayName,
    OrgRoles Roles,
    Guid? EmployeeId,
    IReadOnlyList<string>? PermissionKeys = null,
    IReadOnlyList<RoleRef>? AssignedRoles = null)
{
    private PermissionSet? _permissions;

    /// <summary>The user's effective permissions.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PermissionSet Permissions => _permissions ??= PermissionKeys is null ? PermissionCatalog.ForBuiltInRoles(Roles) : PermissionSet.Of(PermissionKeys);

    /// <summary>True when the user holds <paramref name="permission"/> (a <see cref="Permission"/> constant).</summary>
    public bool Can(string permission) => Permissions.Has(permission);

    /// <summary>True when the user holds any of <paramref name="permissions"/>.</summary>
    public bool CanAny(params string[] permissions) => Permissions.HasAny(permissions);

    /// <summary>Holds the built-in Admin role. For access decisions use <see cref="Can"/>.</summary>
    public bool IsAdmin => Roles.HasFlag(OrgRoles.Admin);
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
