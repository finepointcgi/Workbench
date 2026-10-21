using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Projects;

/// <summary>
/// Who is on which project, for the Projects hub: Finance and administrators see every project; others see the projects
/// they manage or charge to (time recorded, a charge assignment or a resource allocation), and the project page also opens
/// for members of its plan sheet. Money follows <see cref="ProjectAccess.CanReadProjectFinancialsAsync"/>.
/// </summary>
public sealed class ProjectHubAccess(IWorkbenchDbContextFactory dbFactory, AccessControl access)
{
    public AccessControl Access => access;

    /// <param name="IsFinance">Sees every project and its financials (<see cref="Permission.ProjectsFinancialsView"/>).</param>
    /// <param name="IsAdmin">Owns every workspace (<see cref="Permission.AdminAllWorkspaces"/>).</param>
    /// <param name="IsProjectManager">Runs the projects they manage (<see cref="Permission.ProjectsManageOwn"/>).</param>
    /// <param name="SetsUpAll">Sets up every project (<see cref="Permission.SetupProjects"/>).</param>
    /// <param name="FinancialsIn">Projects whose financials a role limited to some companies, org units or projects shows.</param>
    /// <param name="SetupIn">Projects a limited role sets up.</param>
    public sealed record Viewer(
        bool IsFinance, bool IsAdmin, Guid? EmployeeId, bool IsProjectManager = false, bool SetsUpAll = false,
        IReadOnlySet<Guid>? FinancialsIn = null, IReadOnlySet<Guid>? SetupIn = null)
    {
        /// <summary>Sees the project and its money: everywhere, by a limited role's scope, or as its manager.</summary>
        public bool ReadsMoneyOn(Project project) =>
            IsFinance || FinancialsIn?.Contains(project.Id) == true || (EmployeeId is { } me && project.ProjectManagerId == me);

        public bool SetsUp(Project project) =>
            SetsUpAll || SetupIn?.Contains(project.Id) == true || (IsProjectManager && EmployeeId is { } me && project.ProjectManagerId == me);
    }

    public async Task<Viewer> GetViewerAsync(CancellationToken ct)
    {
        access.RequireSignedIn();
        var permissions = await access.GetPermissionsAsync(ct);
        var finance = permissions.Has(Permission.ProjectsFinancialsView);
        var setsUp = permissions.Has(Permission.SetupProjects);
        return new Viewer(finance, permissions.Has(Permission.AdminAllWorkspaces), await access.GetEmployeeIdAsync(ct),
            permissions.Has(Permission.ProjectsManageOwn), setsUp,
            finance ? null : await access.ProjectsWithAsync(Permission.ProjectsFinancialsView, ct),
            setsUp ? null : await access.ProjectsWithAsync(Permission.SetupProjects, ct));
    }

    /// <summary>Projects the employee has recorded time on, is assigned to (on a WBS element) or is allocated to.</summary>
    public static async Task<HashSet<Guid>> ChargedProjectIdsAsync(IWorkbenchDbContext db, Guid? employeeId, CancellationToken ct)
    {
        if (employeeId is not { } me)
        {
            return [];
        }

        var timesheets = db.Set<Timesheet>().Where(t => t.EmployeeId == me).Select(t => t.Id);
        var timeWbs = db.Set<TimeEntry>().Where(e => timesheets.Contains(e.TimesheetId)).Select(e => e.WbsElementId);
        var assignedWbs = db.Set<ChargeAssignment>().Where(a => a.EmployeeId == me).Select(a => a.WbsElementId);
        var fromWbs = await db.Set<WbsElement>().AsNoTracking()
            .Where(w => timeWbs.Contains(w.Id) || assignedWbs.Contains(w.Id))
            .Select(w => w.ProjectId)
            .Distinct()
            .ToListAsync(ct);
        var allocated = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.EmployeeId == me)
            .Select(a => a.ProjectId)
            .Distinct()
            .ToListAsync(ct);
        return [.. fromWbs, .. allocated];
    }

    /// <summary>True when the user may open the project page.</summary>
    public async Task<bool> CanSeeProjectAsync(Guid projectId, CancellationToken ct)
    {
        var viewer = await GetViewerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
        {
            return false;
        }

        if (viewer.ReadsMoneyOn(project))
        {
            return true;
        }

        if ((await ChargedProjectIdsAsync(db, viewer.EmployeeId, ct)).Contains(projectId))
        {
            return true;
        }

        return project.SheetId is { } sheetId && await access.GetSheetRoleAsync(sheetId, ct) is not null;
    }
}
