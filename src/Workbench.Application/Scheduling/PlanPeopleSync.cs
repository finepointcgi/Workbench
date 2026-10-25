using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Resourcing;
using Workbench.Contracts.Resourcing;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Scheduling;

/// <summary>
/// On a project's plan, the people on a task are one list: whoever has hours on a task (staffing) is also its assignee,
/// so the task shows in their My work, on the Gantt and the board; taking someone off either takes them off both. And
/// people on a task keep its dates: when the task moves (typed, dragged, or pushed by a predecessor) their hours move
/// with it, unless someone set other dates on purpose (<see cref="StaffingAssignment.FollowsTask"/>).
/// </summary>
internal static class PlanPeopleSync
{
    /// <summary>The <see cref="ItemAssignee.AssignedBy"/> of assignees added because they have hours on the task.</summary>
    public const string StaffingActor = "staffing";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether an assignment's dates are its task's: it ends when the task finishes and starts when the task starts (or,
    /// for a task already under way, on any day from the task's start up to today, since nobody is staffed in the past).
    /// </summary>
    public static bool Aligned(DateOnly from, DateOnly to, DateOnly? start, DateOnly? finish, DateOnly today) =>
        start is { } s && finish is { } f && to == f && from >= s && from <= (s > today ? s : today);

    /// <summary>
    /// After the plan reflows: assignments that follow their task take its new dates, their hours spread over its working
    /// days (weeks already past keep theirs). Assignments made before this was kept are judged by today's dates. True when
    /// anything changed (the caller saves).
    /// </summary>
    public static async Task<bool> FollowTasksAsync(IWorkbenchDbContext db, Project project, IReadOnlyCollection<Item> items, WorkCalendar calendar, DateTime now, CancellationToken ct)
    {
        var byItem = items.ToDictionary(i => i.Id);
        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == project.Id && w.ItemId != null)
            .Select(w => new { w.Id, ItemId = w.ItemId!.Value }).ToListAsync(ct);
        var itemOf = elements.Where(e => byItem.ContainsKey(e.ItemId)).ToDictionary(e => e.Id, e => byItem[e.ItemId]);
        if (itemOf.Count == 0)
        {
            return false;
        }

        var ids = itemOf.Keys.ToList();
        var assignments = await db.Set<StaffingAssignment>()
            .Where(a => a.ProjectId == project.Id && a.WbsElementId != null && ids.Contains(a.WbsElementId!.Value) && a.FollowsTask != false)
            .ToListAsync(ct);
        if (assignments.Count == 0)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(now);
        var thisWeek = CapacityCalculator.WeekStart(today);
        var actor = Sheets.SheetRowStamps.CurrentAuthor ?? "plan";
        var employeeIds = assignments.Select(a => a.EmployeeId).Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var changed = false;
        foreach (var a in assignments)
        {
            var item = itemOf[a.WbsElementId!.Value];
            if (item.Start is not { } start || item.Finish is not { } finish || item.IsMilestone)
            {
                continue;
            }

            if (a.FollowsTask is null)
            {
                a.FollowsTask = Aligned(a.From, a.To, start, finish, today);
                changed = true;
                continue;
            }

            // Already on the task's dates, or the task is over (its history stays as it was).
            if (Aligned(a.From, a.To, start, finish, today) || finish < today || !employees.TryGetValue(a.EmployeeId, out var employee))
            {
                continue;
            }

            var rows = await db.Set<ResourceAllocation>().Where(r => r.AssignmentId == a.Id).ToListAsync(ct);
            var past = rows.Where(r => r.WeekStart < thisWeek && r.WeekStart >= CapacityCalculator.WeekStart(start) && r.WeekStart <= finish)
                .GroupBy(r => r.WeekStart).ToDictionary(g => g.Key, g => g.Sum(r => r.Hours));
            if (past.Values.Sum() > a.TotalHours)
            {
                past = [];
            }

            var spreadFrom = start > today ? start : today;
            IReadOnlyList<AssignmentWeekDto> weeks;
            try
            {
                weeks = AssignmentSpread.Spread(a.TotalHours, spreadFrom, finish, d => calendar.IsWorkingDay(d) && employee.IsActiveOn(d), past);
            }
            catch (ArgumentException)
            {
                continue; // no working days left in the task for them: leave their hours where they are
            }

            var before = Snapshot(a);
            db.Set<ResourceAllocation>().RemoveRange(rows);
            db.Set<ResourceAllocation>().AddRange(weeks.Where(w => w.Hours > 0).Select(w => new ResourceAllocation
            {
                EmployeeId = a.EmployeeId, ProjectId = a.ProjectId, WbsElementId = a.WbsElementId, LaborCategoryId = a.LaborCategoryId,
                WeekStart = w.WeekStart, Hours = w.Hours, Source = AllocationSource.Manual, AssignmentId = a.Id, Notes = a.Notes,
                CreatedBy = actor, CreatedAtUtc = now,
            }));
            (a.From, a.To, a.UpdatedBy, a.UpdatedAtUtc) = (past.Count > 0 ? start : spreadFrom, finish, actor, now);
            Audit(db, a, "FollowedTask", before, actor, now);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Makes everyone with hours on a plan task one of its assignees, and drops assignees added that way once they have no
    /// hours there. Assignees someone added by hand stay (they're on the task with no hours yet). True when anything changed
    /// (the caller saves).
    /// </summary>
    public static async Task<bool> SyncAssigneesAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var sheetId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId).Select(p => p.SheetId).FirstOrDefaultAsync(ct);
        if (sheetId is not { } sheet)
        {
            return false;
        }

        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId && w.ItemId != null)
            .Select(w => new { w.Id, ItemId = w.ItemId!.Value }).ToListAsync(ct);
        var onSheet = (await db.Items.AsNoTracking().Where(i => i.SheetId == sheet).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
        var itemOf = elements.Where(e => onSheet.Contains(e.ItemId)).ToDictionary(e => e.Id, e => e.ItemId);
        var ids = itemOf.Keys.ToList();

        // Hours by assignment, and hours staffed week by week that aren't an assignment yet.
        var staffed = await db.Set<StaffingAssignment>().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.WbsElementId != null && ids.Contains(a.WbsElementId!.Value))
            .Select(a => new { WbsElementId = a.WbsElementId!.Value, a.EmployeeId }).ToListAsync(ct);
        staffed.AddRange(await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.AssignmentId == null && r.Hours > 0 && r.WbsElementId != null && ids.Contains(r.WbsElementId!.Value))
            .Select(r => new { WbsElementId = r.WbsElementId!.Value, r.EmployeeId }).Distinct().ToListAsync(ct));
        var employeeIds = staffed.Select(s => s.EmployeeId).Distinct().ToList();
        var emailOf = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Email.ToLowerInvariant(), ct);
        var emails = emailOf.Values.Distinct().ToList();
        var users = (await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive && emails.Contains(u.Email)).Select(u => u.Email).ToListAsync(ct))
            .Select(e => e.ToLowerInvariant()).ToHashSet();
        var want = staffed.Where(s => emailOf.ContainsKey(s.EmployeeId)).Select(s => (Item: itemOf[s.WbsElementId], Email: emailOf[s.EmployeeId]))
            .Where(p => users.Contains(p.Email)).ToHashSet();

        var current = await db.Set<ItemAssignee>().Where(a => a.SheetId == sheet).ToListAsync(ct);
        var have = current.Select(a => (a.ItemId, a.Email)).ToHashSet();
        var now = DateTime.UtcNow;
        var add = want.Where(w => !have.Contains(w)).ToList();
        var drop = current.Where(a => a.AssignedBy == StaffingActor && !want.Contains((a.ItemId, a.Email))).ToList();
        db.Set<ItemAssignee>().AddRange(add.Select(w => new ItemAssignee { SheetId = sheet, ItemId = w.Item, Email = w.Email, AssignedBy = StaffingActor, AssignedAtUtc = now }));
        db.Set<ItemAssignee>().RemoveRange(drop);
        return add.Count > 0 || drop.Count > 0;
    }

    /// <summary>
    /// Someone was taken off a task's hours (their assignment <paramref name="removedAssignmentId"/> is going): unless they
    /// still have other hours there, they're no longer its assignee either, however they were added.
    /// </summary>
    public static async Task TakeOffTaskAsync(IWorkbenchDbContext db, Guid? wbsElementId, Guid employeeId, Guid removedAssignmentId, CancellationToken ct)
    {
        if (wbsElementId is not { } wbs
            || await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == wbs).Select(w => w.ItemId).FirstOrDefaultAsync(ct) is not { } itemId
            || await db.Set<StaffingAssignment>().AnyAsync(a => a.WbsElementId == wbs && a.EmployeeId == employeeId && a.Id != removedAssignmentId, ct))
        {
            return;
        }

        var email = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.Email.ToLower()).FirstOrDefaultAsync(ct);
        db.Set<ItemAssignee>().RemoveRange(await db.Set<ItemAssignee>().Where(a => a.ItemId == itemId && a.Email == email).ToListAsync(ct));
    }

    /// <summary>
    /// Assignees were taken off a plan task: their hours on it go too (assignments and any week-by-week hours). Nothing
    /// happens on a sheet that isn't a project's plan.
    /// </summary>
    public static async Task TakeOffHoursAsync(IWorkbenchDbContext db, Guid itemId, IReadOnlyCollection<string> emails, string actor, CancellationToken ct)
    {
        if (emails.Count == 0 || await db.Set<WbsElement>().AsNoTracking().Where(w => w.ItemId == itemId).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct) is not { } wbs)
        {
            return;
        }

        var lower = emails.Select(e => e.ToLowerInvariant()).ToList();
        var people = await db.Set<Employee>().AsNoTracking().Where(e => lower.Contains(e.Email.ToLower())).Select(e => e.Id).ToListAsync(ct);
        var assignments = await db.Set<StaffingAssignment>().Where(a => a.WbsElementId == wbs && people.Contains(a.EmployeeId)).ToListAsync(ct);
        var assignmentIds = assignments.Select(a => a.Id).ToList();
        db.Set<ResourceAllocation>().RemoveRange(await db.Set<ResourceAllocation>()
            .Where(r => r.WbsElementId == wbs && people.Contains(r.EmployeeId) && (r.AssignmentId == null || assignmentIds.Contains(r.AssignmentId!.Value))).ToListAsync(ct));
        db.Set<StaffingAssignment>().RemoveRange(assignments);
        var now = DateTime.UtcNow;
        foreach (var a in assignments)
        {
            db.Set<AuditEvent>().Add(new AuditEvent
            {
                EntityType = nameof(StaffingAssignment), EntityId = a.Id, Action = "Deleted", BeforeJson = JsonSerializer.Serialize(Snapshot(a), Json),
                Actor = actor, OccurredAtUtc = now,
            });
        }
    }

    private static object Snapshot(StaffingAssignment a) => new { a.EmployeeId, a.WbsElementId, a.LaborCategoryId, a.From, a.To, a.TotalHours, a.FollowsTask };

    private static void Audit(IWorkbenchDbContext db, StaffingAssignment a, string action, object? before, string actor, DateTime now) =>
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            EntityType = nameof(StaffingAssignment),
            EntityId = a.Id,
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = JsonSerializer.Serialize(Snapshot(a), Json),
            Actor = actor,
            OccurredAtUtc = now,
        });
}
