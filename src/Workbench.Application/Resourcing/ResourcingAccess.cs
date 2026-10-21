using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Resourcing;

/// <summary>
/// Who may see and change what in resource planning:
/// <list type="bullet">
/// <item>Resource managers and administrators manage every project's allocations; a project manager manages their own projects'.</item>
/// <item>Finance reads all allocations; everyone reads their own.</item>
/// <item>The heat map and "find people" show everyone to staffing roles (resource managers, project managers, Finance,
/// Timekeeper, Admin); others see only themselves.</item>
/// <item>Utilization follows timesheet visibility: everyone for Timekeeper/Finance/Admin/ResourceManager, direct reports for
/// a supervisor, otherwise yourself.</item>
/// </list>
/// A project the user can't see at all (<see cref="ProjectVisibility"/>) is not found before any of these says forbidden.
/// </summary>
public sealed class ResourcingAccess(IWorkbenchDbContextFactory dbFactory, AccessControl access, ProjectVisibility? visibility = null)
{
    public AccessControl Access => access;

    public ProjectVisibility Visibility { get; } = visibility ?? new ProjectVisibility(dbFactory, access);

    public Task<OrgRoles> RolesAsync(CancellationToken ct) => access.GetOrgRolesAsync(ct);

    public Task<Guid?> MeAsync(CancellationToken ct) => access.GetEmployeeIdAsync(ct);

    public async Task<bool> ManagesAllAsync(CancellationToken ct) => (await RolesAsync(ct)).IsResourceManager();

    public async Task<bool> ReadsAllAllocationsAsync(CancellationToken ct)
    {
        var roles = await RolesAsync(ct);
        return roles.IsResourceManager() || roles.IsFinance();
    }

    /// <summary>Projects the user manages (as project manager).</summary>
    public async Task<IReadOnlySet<Guid>> ManagedProjectIdsAsync(CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if (me is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(ct);
        return (await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == me).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
    }

    public async Task<bool> CanManageProjectAsync(Guid projectId, CancellationToken ct) =>
        access.IsAuthenticated && (await ManagesAllAsync(ct) || (await ManagedProjectIdsAsync(ct)).Contains(projectId));

    public async Task RequireManageProjectAsync(Guid projectId, CancellationToken ct)
    {
        await Visibility.RequireVisibleAsync(projectId, ct);
        if (!await CanManageProjectAsync(projectId, ct))
        {
            throw new WorkbenchForbiddenException("Only resource managers, administrators and the project's manager can change its allocations.");
        }
    }

    /// <summary>Staffing and open demand: Finance, Admin, resource managers or the project's manager.</summary>
    public async Task RequireReadProjectAsync(Guid projectId, CancellationToken ct)
    {
        await Visibility.RequireVisibleAsync(projectId, ct);
        if (!await ReadsAllAllocationsAsync(ct) && !(await ManagedProjectIdsAsync(ct)).Contains(projectId))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators, resource managers and the project's manager can see its staffing.");
        }
    }

    /// <summary>True for staffing roles, who see everyone on the heat map and in "find people".</summary>
    public async Task<bool> SeesEveryoneAsync(CancellationToken ct)
    {
        var roles = await RolesAsync(ct);
        return roles.IsResourceManager() || roles.CanViewAllTimesheets() || (await ManagedProjectIdsAsync(ct)).Count > 0;
    }

    /// <summary>Employees whose utilization the user may see; null means everyone.</summary>
    public async Task<IReadOnlySet<Guid>?> UtilizationEmployeeIdsAsync(CancellationToken ct)
    {
        var roles = await RolesAsync(ct);
        if (roles.IsResourceManager() || roles.CanViewAllTimesheets())
        {
            return null;
        }

        var me = await MeAsync(ct);
        if (me is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var reports = await db.Set<Employee>().AsNoTracking().Where(e => e.SupervisorId == me).Select(e => e.Id).ToListAsync(ct);
        return reports.Append(me.Value).ToHashSet();
    }
}
