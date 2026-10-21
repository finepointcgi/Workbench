using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Domain.ProjectAccounting;

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

    /// <summary>Projects the user may see at all (<see cref="ProjectVisibility"/>); null = every project.</summary>
    IReadOnlySet<Guid>? VisibleProjects = null)
{
    public bool CanSeeProject(Guid projectId) => VisibleProjects is null || VisibleProjects.Contains(projectId);

    public bool IsFinance => Roles.IsFinance();

    public bool IsAdmin => Roles.IsAdmin();

    public bool ManagesAnyProject => FinancialProjects is null || FinancialProjects.Count > 0;

    public bool CanReadProject(Guid projectId) => FinancialProjects is null || FinancialProjects.Contains(projectId);

    public bool CanReadContract(Guid? contractId) => FinancialContracts is null || (contractId is { } id && FinancialContracts.Contains(id));

    public bool CanSeeEmployeeTime(Guid employeeId) => VisibleEmployees is null || VisibleEmployees.Contains(employeeId);
}

public sealed class ReportAccess(ProjectAccess projects, IWorkbenchDbContextFactory dbFactory)
{
    public AccessControl Access => projects.Access;

    public async Task<ReportAccessContext> GetAsync(CancellationToken ct)
    {
        var access = projects.Access;
        access.RequireSignedIn();
        var roles = await access.GetOrgRolesAsync(ct);
        var me = await access.GetEmployeeIdAsync(ct);
        var visible = await projects.VisibleTimeEmployeeIdsAsync(ct);
        var contracts = await projects.ManagedContractIdsAsync(ct);

        IReadOnlySet<Guid>? managedProjects = null;
        if (!roles.IsFinance())
        {
            if (me is null)
            {
                managedProjects = new HashSet<Guid>();
            }
            else
            {
                await using var db = await dbFactory.CreateAsync(ct);
                managedProjects = (await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == me).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
            }
        }

        return new ReportAccessContext(access.Email, roles, me, visible, managedProjects, contracts, await projects.Visibility.VisibleProjectIdsAsync(ct));
    }

    public static WorkbenchForbiddenException Forbidden(string message) => new(message);
}
