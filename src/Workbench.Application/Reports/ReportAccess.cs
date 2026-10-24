using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Reports;

/// <summary>
/// What the current user may see, worked out once per report run from the same rules the rest of project accounting
/// uses (<see cref="ProjectAccess"/>): Finance and Admin see everything; a project manager sees their projects'
/// financials and the contracts those projects are on; hours follow the timekeeping-report rule (Timekeeper/Finance/Admin
/// everyone, supervisors their direct reports, otherwise yourself). Projects the user can't see at all
/// (<see cref="ProjectVisibility"/>) are left out of project lists and not found as a report parameter.
/// </summary>
public sealed record ReportAccessContext(
    string Email,
    OrgRoles Roles,
    Guid? EmployeeId,

    /// <summary>Employees whose time the user may see; null = everyone.</summary>
    IReadOnlySet<Guid>? VisibleEmployees,

    /// <summary>Projects whose financials the user may read; null = every project.</summary>
    IReadOnlySet<Guid>? FinancialProjects,

    /// <summary>Contracts whose values and invoices the user may read; null = every contract.</summary>
    IReadOnlySet<Guid>? FinancialContracts,

    /// <summary>The user's permissions; null means those of <see cref="Roles"/> (built-in roles only).</summary>
    PermissionSet? Permissions = null,

    /// <summary>Every role the user holds (built-in and custom), for reports shared with roles.</summary>
    IReadOnlyList<Guid>? RoleIds = null,

    /// <summary>Projects the user may see at all (<see cref="ProjectVisibility"/>); null = every project.</summary>
    IReadOnlySet<Guid>? VisibleProjects = null,

    /// <summary>
    /// Companies whose records the report shows (their companies, the switcher's pick, or the report's own company
    /// filter); null = every company. Ledger lines by their company, hours and ODCs by their project's, invoices, bills,
    /// projects and employees (home company) by theirs.
    /// </summary>
    IReadOnlySet<Guid>? VisibleCompanies = null)
{
    public bool InCompany(Guid companyId) => VisibleCompanies is null || VisibleCompanies.Contains(companyId);

    public bool CanSeeProject(Guid projectId) => VisibleProjects is null || VisibleProjects.Contains(projectId);

    public PermissionSet Granted => Permissions ?? PermissionCatalog.ForBuiltInRoles(Roles);

    public bool Can(string permission) => Granted.Has(permission);

    /// <summary>Sees cost rates and labor cost (<see cref="Permission.LaborCostView"/>).</summary>
    public bool SeesLaborCost => Can(Permission.LaborCostView);

    /// <summary>Changes anyone's saved reports and schedules (<see cref="Permission.AdminAllWorkspaces"/>).</summary>
    public bool OwnsAllReports => Can(Permission.AdminAllWorkspaces);

    /// <summary>Holds the role with <paramref name="roleId"/> (built-in roles count from <see cref="Roles"/> too).</summary>
    public bool HoldsRole(Guid roleId) =>
        roleId == BuiltInRoles.Employee.Id || RoleIds?.Contains(roleId) == true || BuiltInRoles.Find(roleId) is { } d && Roles.HasFlag(d.Role) && d.Role != OrgRoles.Employee;

    public bool ManagesAnyProject => FinancialProjects is null || FinancialProjects.Count > 0;

    public bool CanReadProject(Guid projectId) => FinancialProjects is null || FinancialProjects.Contains(projectId);

    public bool CanReadContract(Guid? contractId) => FinancialContracts is null || (contractId is { } id && FinancialContracts.Contains(id));

    public bool CanSeeEmployeeTime(Guid employeeId) => VisibleEmployees is null || VisibleEmployees.Contains(employeeId);
}

public sealed class ReportAccess(ProjectAccess projects, Companies.CompanyScope? companies = null)
{
    public AccessControl Access => projects.Access;

    public async Task<ReportAccessContext> GetAsync(CancellationToken ct)
    {
        var access = projects.Access;
        access.RequireSignedIn();
        var resolved = await access.GetAccessAsync(ct);
        var me = await access.GetEmployeeIdAsync(ct);
        var visible = await projects.VisibleTimeEmployeeIdsAsync(ct);
        var contracts = await projects.ManagedContractIdsAsync(ct);

        var managedProjects = await projects.ManagedProjectIdsAsync(ct); // Managed, or covered by a limited role's scope.

        return new ReportAccessContext(access.Email, resolved.BuiltIn, me, visible, managedProjects, contracts, resolved.Permissions, resolved.Roles.Select(r => r.Id).ToList(), await projects.Visibility.VisibleProjectIdsAsync(ct),
            companies is null ? null : await companies.VisibleCompanyIdsAsync(ct));
    }

    public static WorkbenchForbiddenException Forbidden(string message) => new(message);
}
