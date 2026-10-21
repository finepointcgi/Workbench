using Microsoft.EntityFrameworkCore;
using Workbench.Application.Companies;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;
using Workbench.Domain.Travel;

namespace Workbench.Application.Identity;

/// <summary>
/// The one rule for who may see a project at all. Every path that reads project data (the project list and pages, the
/// Projects hub, search and the command palette, the assistant's tools, reports, dashboards, My work and to-dos,
/// notifications and the API) asks this class; finer rules (financials, setup, staffing) apply on top of it.
/// <para>
/// <b>Who sees every project:</b> Finance and administrators (and the worker's system user and trusted background
/// readers), and resource managers, who staff work across all projects. Business development, pricing, HR, materials,
/// timekeepers and project managers get nothing extra from their role here: they see the projects they are on, like
/// any employee (their own modules keep their own role rules).
/// </para>
/// <para>
/// <b>Everyone else sees the projects they are on.</b> Someone is on a project when any of these hold:
/// <list type="number">
/// <item>they are its project manager;</item>
/// <item>they are assigned to it: a resource allocation, a charge assignment on one of its WBS elements, or a task
/// assignment on its plan sheet;</item>
/// <item>they are a member of the workspace that holds its plan sheet;</item>
/// <item>they have charged to it (time, an expense or other ODC, a travel authorization), or may charge to it now: an
/// active project with an open, chargeable code that needs no assignment (indirect and leave codes, for example), the
/// same eligibility the timesheet's charge-code list uses;</item>
/// <item>they approve (or approved) a timesheet, expense report, ODC or travel authorization that charges it, or an
/// approval step scoped to it, themselves or as someone's current delegate.</item>
/// </list>
/// </para>
/// <para>
/// <b>Company access applies too:</b> a project in a company the user can't use (or outside the company picked in the
/// switcher) is hidden whatever the above says.
/// </para>
/// <para>
/// A hidden project is not found (<see cref="WorkbenchNotFoundException"/>), never forbidden, so its existence doesn't
/// leak. Nothing is cached: a Blazor circuit keeps its scope for hours, and assignments change.
/// </para>
/// </summary>
public sealed class ProjectVisibility(IWorkbenchDbContextFactory dbFactory, AccessControl access, CompanyScope? companies = null, TimeProvider? clock = null)
{
    public AccessControl Access => access;

    /// <summary>Roles that see every project (before company access): Finance, Admin and resource managers.</summary>
    public static bool SeesEveryProject(OrgRoles roles) => roles.IsFinance() || roles.IsResourceManager();

    /// <summary>The projects the user may see, or null when nothing limits them (every project, every company).</summary>
    public async Task<IReadOnlySet<Guid>?> VisibleProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return new HashSet<Guid>();
        }

        // "See every project" (custom roles): everywhere, nowhere, or within a scoped role's companies, org units, projects
        // or contracts. Built-in Finance and Resource managers hold it everywhere.
        var granted = access.IsSystemReader ? null : await access.ProjectsWithAsync(Permission.ProjectsViewAll, cancellationToken);
        var everything = access.IsSystemReader || granted is null;
        var inCompanies = companies is null ? null : await companies.VisibleCompanyIdsAsync(cancellationToken);
        if (everything && inCompanies is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var projects = db.Set<Project>().AsNoTracking();
        if (inCompanies is not null)
        {
            var companyIds = inCompanies.ToList();
            projects = projects.Where(p => companyIds.Contains(p.CompanyId));
        }

        if (!everything)
        {
            var mine = (await OnProjectIdsAsync(db, cancellationToken)).Concat(granted ?? new HashSet<Guid>()).Distinct().ToList();
            projects = projects.Where(p => mine.Contains(p.Id));
        }

        return (await projects.Select(p => p.Id).ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>True when the user may see the project (false when it doesn't exist).</summary>
    public async Task<bool> CanSeeAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var visible = await VisibleProjectIdsAsync(cancellationToken);
        if (visible is not null)
        {
            return visible.Contains(projectId);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<Project>().AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken);
    }

    /// <summary>Throws <see cref="WorkbenchNotFoundException"/> unless the user signed in and may see the project.</summary>
    public async Task RequireVisibleAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!await CanSeeAsync(projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }
    }

    /// <summary>Throws <see cref="WorkbenchNotFoundException"/> unless the WBS element's project is visible.</summary>
    public async Task RequireVisibleWbsAsync(Guid wbsElementId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var projectId = await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == wbsElementId).Select(w => (Guid?)w.ProjectId).FirstOrDefaultAsync(cancellationToken);
        if (projectId is null || !await CanSeeAsync(projectId.Value, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("WBS element", wbsElementId);
        }
    }

    /// <summary>Drops the items on projects the user can't see; items with no project are kept.</summary>
    public async Task<IReadOnlyList<T>> FilterAsync<T>(IReadOnlyList<T> items, Func<T, Guid?> projectOf, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0 || await VisibleProjectIdsAsync(cancellationToken) is not { } visible)
        {
            return items;
        }

        return items.Where(i => projectOf(i) is not { } id || visible.Contains(id)).ToList();
    }

    /// <summary>The projects the signed-in user is on (the numbered list in the class summary), ignoring roles and companies.</summary>
    private async Task<HashSet<Guid>> OnProjectIdsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var email = access.Email;
        var me = (await EmployeeLinks.FindForUserAsync(db, email, ct))?.Id;
        var ids = new HashSet<Guid>();

        if (me is { } employeeId)
        {
            // 1. Project manager.
            ids.UnionWith(await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == employeeId).Select(p => p.Id).ToListAsync(ct));

            // 2. Allocations and charge assignments; 4. time, expenses/ODCs and trips charged.
            ids.UnionWith(await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.EmployeeId == employeeId).Select(a => a.ProjectId).Distinct().ToListAsync(ct));
            var timesheets = db.Set<Timesheet>().Where(t => t.EmployeeId == employeeId).Select(t => t.Id);
            var timeWbs = db.Set<TimeEntry>().Where(e => timesheets.Contains(e.TimesheetId)).Select(e => e.WbsElementId);
            var assignedWbs = db.Set<ChargeAssignment>().Where(a => a.EmployeeId == employeeId).Select(a => a.WbsElementId);
            ids.UnionWith(await db.Set<WbsElement>().AsNoTracking()
                .Where(w => timeWbs.Contains(w.Id) || assignedWbs.Contains(w.Id))
                .Select(w => w.ProjectId).Distinct().ToListAsync(ct));
            ids.UnionWith(await db.Set<OdcEntry>().AsNoTracking().Where(o => o.EmployeeId == employeeId).Select(o => o.ProjectId).Distinct().ToListAsync(ct));
            ids.UnionWith(await db.Set<TravelAuthorization>().AsNoTracking().Where(t => t.EmployeeId == employeeId).Select(t => t.ProjectId).Distinct().ToListAsync(ct));

            // 4. May charge now: an active project's open, chargeable leaf code that needs no assignment (as the timesheet's list).
            ids.UnionWith(await (
                    from w in db.Set<WbsElement>().AsNoTracking()
                    join p in db.Set<Project>() on w.ProjectId equals p.Id
                    where w.IsOpen && w.IsChargeable && !w.RequiresAssignment && p.Status == ProjectStatus.Active
                          && !db.Set<WbsElement>().Any(c => c.ParentId == w.Id)
                    select p.Id)
                .Distinct().ToListAsync(ct));
        }

        if (email.Length == 0)
        {
            return ids;
        }

        // 2. Task assignments on a plan sheet; 3. members of the plan sheet's workspace.
        var assignedSheets = db.Set<ItemAssignee>().Where(a => a.Email == email).Select(a => a.SheetId);
        var workspaces = db.Set<WorkspaceMember>().Where(m => m.UserEmail == email).Select(m => m.WorkspaceId);
        var memberSheets = db.Sheets.Where(s => workspaces.Contains(s.WorkspaceId)).Select(s => s.Id);
        ids.UnionWith(await db.Set<Project>().AsNoTracking()
            .Where(p => p.SheetId != null && (assignedSheets.Contains(p.SheetId.Value) || memberSheets.Contains(p.SheetId.Value)))
            .Select(p => p.Id).ToListAsync(ct));

        // 5. Approvers (and current delegates) of documents that charge projects.
        ids.UnionWith(await ApprovedProjectIdsAsync(db, email, ct));
        return ids;
    }

    private async Task<IEnumerable<Guid>> ApprovedProjectIdsAsync(IWorkbenchDbContext db, string email, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        var delegations = await db.Set<ApprovalDelegation>().AsNoTracking()
            .Where(d => d.DelegateEmail == email && d.RevokedAtUtc == null && d.StartsOn <= today && d.EndsOn >= today)
            .Select(d => new { d.DelegatorEmail, d.DocumentType })
            .ToListAsync(ct);
        var approvers = delegations.Select(d => d.DelegatorEmail).Append(email).Distinct().ToList();

        // AssigneesCsv is a comma list: narrow in the database, then match whole addresses here.
        var candidates = new List<(Guid InstanceId, string? ScopeKey, string Assignees, string? DecidedBy, string DocumentType, Guid DocumentId)>();
        foreach (var approver in approvers)
        {
            var rows = await (
                    from t in db.Set<ApprovalTask>().AsNoTracking()
                    join i in db.Set<ApprovalInstance>().AsNoTracking() on t.InstanceId equals i.Id
                    where t.Status != ApprovalTaskStatus.Waiting && (t.AssigneesCsv.Contains(approver) || t.DecidedBy == approver || t.OnBehalfOf == approver)
                    select new { t.InstanceId, t.ScopeKey, t.AssigneesCsv, t.DecidedBy, i.DocumentType, i.DocumentId })
                .ToListAsync(ct);
            candidates.AddRange(rows
                .Where(r => r.DecidedBy == approver || r.AssigneesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(approver))
                .Where(r => approver == email || delegations.Any(d => d.DelegatorEmail == approver && (d.DocumentType is null || d.DocumentType == r.DocumentType)))
                .Select(r => (r.InstanceId, r.ScopeKey, r.AssigneesCsv, r.DecidedBy, r.DocumentType, r.DocumentId)));
        }

        var ids = new HashSet<Guid>();
        foreach (var scope in candidates.Select(c => c.ScopeKey).OfType<string>().Where(k => k.StartsWith("project:", StringComparison.Ordinal)))
        {
            if (Guid.TryParse(scope["project:".Length..], out var scoped))
            {
                ids.Add(scoped);
            }
        }

        List<Guid> Documents(string type) => candidates.Where(c => c.DocumentType == type).Select(c => c.DocumentId).Distinct().ToList();
        var timesheets = Documents(ApprovalDocumentTypes.Timesheet);
        var reports = Documents(ApprovalDocumentTypes.ExpenseReport);
        var odcs = Documents(ApprovalDocumentTypes.VendorOdc);
        var trips = Documents(ApprovalDocumentTypes.TravelAuthorization);
        if (timesheets.Count > 0)
        {
            var wbs = db.Set<TimeEntry>().Where(e => timesheets.Contains(e.TimesheetId)).Select(e => e.WbsElementId);
            ids.UnionWith(await db.Set<WbsElement>().AsNoTracking().Where(w => wbs.Contains(w.Id)).Select(w => w.ProjectId).Distinct().ToListAsync(ct));
        }

        if (reports.Count > 0 || odcs.Count > 0)
        {
            ids.UnionWith(await db.Set<OdcEntry>().AsNoTracking()
                .Where(o => (o.ExpenseReportId != null && reports.Contains(o.ExpenseReportId.Value)) || odcs.Contains(o.Id))
                .Select(o => o.ProjectId).Distinct().ToListAsync(ct));
        }

        if (trips.Count > 0)
        {
            ids.UnionWith(await db.Set<TravelAuthorization>().AsNoTracking().Where(t => trips.Contains(t.Id)).Select(t => t.ProjectId).Distinct().ToListAsync(ct));
        }

        return ids;
    }
}
