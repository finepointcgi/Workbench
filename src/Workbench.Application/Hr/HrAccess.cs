using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Hr;

/// <summary>
/// Who may see which part of an HR record. HR and administrators see everything; the employee sees their own record;
/// supervisors (directly or up the chain) and Finance see job fields; everyone sees the directory.
/// </summary>
public sealed class HrAccess(AccessControl access, IWorkbenchDbContextFactory dbFactory)
{
    public sealed record Viewer(string Email, OrgRoles Roles, Guid? EmployeeId)
    {
        public bool IsHr => Roles.IsHr();

        public bool IsFinance => Roles.IsFinance();

        public bool IsAdmin => Roles.IsAdmin();

        public bool IsSelf(Guid employeeId) => EmployeeId == employeeId;
    }

    public AccessControl Access => access;

    public async Task<Viewer> ViewerAsync(CancellationToken ct)
    {
        access.RequireSignedIn();
        var roles = await access.GetOrgRolesAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var me = await EmployeeLinks.FindForUserAsync(db, access.Email, ct);
        return new Viewer(access.Email, roles, me?.Id);
    }

    public async Task<Viewer> RequireHrAsync(string what, CancellationToken ct)
    {
        await access.RequireHrAsync(what, ct);
        return await ViewerAsync(ct);
    }

    /// <summary>Everyone below <paramref name="managerId"/> in the supervisor tree.</summary>
    public static async Task<HashSet<Guid>> ReportsOfAsync(IWorkbenchDbContext db, Guid managerId, CancellationToken ct)
    {
        var links = await db.Set<Employee>().AsNoTracking().Where(e => e.SupervisorId != null).Select(e => new { e.Id, e.SupervisorId }).ToListAsync(ct);
        var byManager = links.ToLookup(l => l.SupervisorId!.Value, l => l.Id);
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>([managerId]);
        while (queue.Count > 0)
        {
            foreach (var report in byManager[queue.Dequeue()])
            {
                if (report != managerId && result.Add(report))
                {
                    queue.Enqueue(report);
                }
            }
        }

        return result;
    }

    public static async Task<HrRecordAccess> LevelAsync(IWorkbenchDbContext db, Viewer viewer, Guid employeeId, CancellationToken ct)
    {
        if (viewer.IsHr)
        {
            return HrRecordAccess.Full;
        }

        if (viewer.IsSelf(employeeId))
        {
            return HrRecordAccess.Self;
        }

        if (viewer.IsFinance || (viewer.EmployeeId is { } me && (await ReportsOfAsync(db, me, ct)).Contains(employeeId)))
        {
            return HrRecordAccess.Job;
        }

        return HrRecordAccess.Directory;
    }

    /// <summary>HR, an administrator or the employee; anyone else is refused (and the refusal recorded).</summary>
    public async Task<Viewer> RequireHrOrSelfAsync(Guid employeeId, string what, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        if (!viewer.IsHr && !viewer.IsSelf(employeeId))
        {
            throw await access.DenyAsync($"Only HR, administrators and the employee can {what}.", ct);
        }

        return viewer;
    }

    public async Task<Viewer> RequireHrOrFinanceAsync(string what, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        if (!viewer.IsHr && !viewer.IsFinance)
        {
            throw await access.DenyAsync($"Only HR, Finance and administrators can {what}.", ct);
        }

        return viewer;
    }

    public static WorkbenchNotFoundException EmployeeNotFound(Guid employeeId) => WorkbenchNotFoundException.For("Employee", employeeId);
}
