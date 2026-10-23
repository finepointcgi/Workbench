using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// Assigning people by total hours. An assignment's total is the truth; its weekly <see cref="ResourceAllocation"/> rows
/// are spread from it (<see cref="AssignmentSpread"/>) over the working days of the project's calendar, so staffing, the
/// heat map, budget-vs-staffed and charging keep reading weekly rows. Permission checks are in the identity decorator.
/// </summary>
public sealed class StaffingAssignmentService(
    IWorkbenchDbContextFactory dbFactory,
    IResourceLeaveCalendar leaveCalendar,
    ResourcingAccess rules,
    AuditRecorder audit,
    TimeProvider clock) : IStaffingAssignmentService
{
    private const string EntityType = nameof(StaffingAssignment);

    /// <summary>The longest assignment: three years.</summary>
    public const int MaxDays = 3 * 366;

    public const decimal MaxTotalHours = 99_999m;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ProjectAssignmentsDto> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var assignments = await db.Set<StaffingAssignment>().AsNoTracking().Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);
        var dtos = await ToDtosAsync(db, assignments, cancellationToken);

        var loose = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.AssignmentId == null && a.Hours > 0).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, loose.Select(a => a.EmployeeId), loose.Select(a => a.WbsElementId), loose.Select(a => a.LaborCategoryId), cancellationToken);
        var weekByWeek = loose
            .GroupBy(a => (a.EmployeeId, a.WbsElementId, a.LaborCategoryId))
            .Select(g => new UnassignedStaffingDto(
                g.Key.EmployeeId, names.Person(g.Key.EmployeeId), g.Key.WbsElementId, names.WbsCode(g.Key.WbsElementId), names.WbsName(g.Key.WbsElementId),
                g.Key.LaborCategoryId, names.Category(g.Key.LaborCategoryId), g.Min(a => a.WeekStart), g.Max(a => a.WeekStart).AddDays(6), g.Sum(a => a.Hours)))
            .OrderBy(u => u.EmployeeName, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.WbsCode, StringComparer.Ordinal)
            .ToList();
        return new ProjectAssignmentsDto(project.Id, project.Code, project.Start, project.Finish, dtos, weekByWeek);
    }

    public async Task<AssignmentPreviewDto> PreviewAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await PlanAsync(db, request, cancellationToken);
        return await PreviewOfAsync(db, plan, cancellationToken);
    }

    public async Task<StaffingAssignmentDto> SaveAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var saved = await SaveOneAsync(db, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [saved], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<StaffingAssignmentDto>> SaveManyAsync(Guid projectId, IReadOnlyList<SaveAssignmentRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is 0 or > 500)
        {
            throw new ArgumentException("Save between 1 and 500 assignments at a time.");
        }

        if (requests.Any(r => r.ProjectId != projectId))
        {
            throw new ArgumentException("Every assignment must be on this project.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var saved = new List<StaffingAssignment>();
        foreach (var (request, index) in requests.Select((r, i) => (r, i)))
        {
            try
            {
                saved.Add(await SaveOneAsync(db, request, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Row {index + 1}: {ex.Message} Nothing was saved.", ex);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDtosAsync(db, saved, cancellationToken);
    }

    public async Task DeleteAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var assignment = await db.Set<StaffingAssignment>().FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Assignment", assignmentId);
        var rows = await db.Set<ResourceAllocation>().Where(a => a.AssignmentId == assignmentId).ToListAsync(cancellationToken);
        db.Set<ResourceAllocation>().RemoveRange(rows);
        db.Set<StaffingAssignment>().Remove(assignment);
        audit.Record(db, EntityType, assignment.Id, "Deleted", before: Snapshot(assignment));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<StaffingAssignmentDto> SetWeekAsync(Guid assignmentId, DateOnly weekStart, decimal hours, CancellationToken cancellationToken = default)
    {
        if (hours is < 0 or > 168)
        {
            throw new ArgumentException("Hours in a week must be between 0 and 168.");
        }

        hours = Math.Round(hours, 2, MidpointRounding.AwayFromZero);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var assignment = await db.Set<StaffingAssignment>().FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Assignment", assignmentId);
        var week = CapacityCalculator.WeekStart(weekStart);
        if (week < CapacityCalculator.WeekStart(assignment.From) || week > assignment.To)
        {
            throw new ArgumentException($"The week of {week:MMM d} is outside the assignment's dates ({assignment.From:MMM d, yyyy} – {assignment.To:MMM d, yyyy}). Change the dates first.");
        }

        var rows = await db.Set<ResourceAllocation>().Where(a => a.AssignmentId == assignmentId).ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault(r => r.WeekStart == week);
        var now = clock.GetUtcNow().UtcDateTime;
        var before = Snapshot(assignment);
        if (row is null && hours > 0)
        {
            row = NewRow(assignment, week, hours, now);
            db.Set<ResourceAllocation>().Add(row);
            rows.Add(row);
        }
        else if (row is not null && hours == 0)
        {
            db.Set<ResourceAllocation>().Remove(row);
            rows.Remove(row);
        }
        else if (row is not null)
        {
            row.Hours = hours;
            row.UpdatedBy = audit.Actor;
            row.UpdatedAtUtc = now;
        }

        // The weeks are what the person is booked for, so the total follows them.
        assignment.TotalHours = rows.Sum(r => r.Hours);
        assignment.UpdatedBy = audit.Actor;
        assignment.UpdatedAtUtc = now;
        audit.Record(db, EntityType, assignment.Id, "WeekChanged", before, new { Week = week, Hours = hours, assignment.TotalHours });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [assignment], cancellationToken))[0];
    }

    public async Task<AssignmentPastePreviewDto> PreviewPasteAsync(Guid projectId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = AssignmentPaste.Parse(text);
        if (lines.Count == 0)
        {
            throw new ArgumentException("Paste at least one row: person, task, hours, from, to.");
        }

        if (lines.Count > 500)
        {
            throw new ArgumentException("Paste up to 500 rows at a time.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var people = await db.Set<Employee>().AsNoTracking().ToListAsync(cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(cancellationToken);
        var rows = new List<AssignmentPasteRowDto>();
        foreach (var line in lines)
        {
            var errors = new List<string>(line.Errors);
            var person = AssignmentPaste.FindPerson(people, line.Person, errors);
            var task = AssignmentPaste.FindTask(wbs, project, line.Task, errors);
            SaveAssignmentRequest? request = null;
            var warnings = new List<string>();
            if (errors.Count == 0 && person is not null && line.Hours is { } hours)
            {
                request = new SaveAssignmentRequest(person.Id, projectId, hours, task?.Id, line.From, line.To);
                await CheckRowAsync(db, request, errors, warnings, cancellationToken);
            }

            rows.Add(new AssignmentPasteRowDto(line.Line, line.Person, line.Task, line.Hours, line.From, line.To, errors.Count == 0 ? request : null, errors, warnings));
        }

        return new AssignmentPastePreviewDto(rows);
    }

    public async Task<AssignmentPastePreviewDto> PreviewCopyTeamAsync(Guid projectId, Guid fromProjectId, CancellationToken cancellationToken = default)
    {
        if (projectId == fromProjectId)
        {
            throw new ArgumentException("Pick a different project to copy the team from.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var source = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == fromProjectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", fromProjectId);
        var team = await db.Set<StaffingAssignment>().AsNoTracking().Where(a => a.ProjectId == fromProjectId).ToListAsync(cancellationToken);
        var members = team.Select(a => (a.EmployeeId, a.WbsElementId, a.LaborCategoryId, a.TotalHours)).ToList();
        if (members.Count == 0)
        {
            // A project staffed week by week: its people, tasks and hours, added up.
            members = (await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.ProjectId == fromProjectId && a.Hours > 0).ToListAsync(cancellationToken))
                .GroupBy(a => (a.EmployeeId, a.WbsElementId, a.LaborCategoryId))
                .Select(g => (g.Key.EmployeeId, g.Key.WbsElementId, g.Key.LaborCategoryId, g.Sum(a => a.Hours)))
                .ToList();
        }

        if (members.Count == 0)
        {
            throw new ArgumentException($"Nobody is assigned to {source.Code} yet, so there's no team to copy.");
        }

        var sourceWbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == fromProjectId).ToDictionaryAsync(w => w.Id, cancellationToken);
        var targetWbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, members.Select(m => m.EmployeeId), [], [], cancellationToken);
        var people = await db.Set<Employee>().AsNoTracking().Where(e => members.Select(m => m.EmployeeId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var rows = new List<AssignmentPasteRowDto>();
        var line = 0;
        foreach (var m in members.OrderBy(m => names.Person(m.EmployeeId), StringComparer.OrdinalIgnoreCase))
        {
            line++;
            var errors = new List<string>();
            var warnings = new List<string>();
            var from = m.WbsElementId is { } w ? sourceWbs.GetValueOrDefault(w) : null;
            var match = from is null ? null : AssignmentPaste.MatchTask(from, source.Code, project.Code, targetWbs);
            if (from is not null && match is null)
            {
                warnings.Add($"{project.Code} has no task like {from.Code} {from.Name}, so they're assigned to the whole project.");
            }

            var person = people.GetValueOrDefault(m.EmployeeId);
            if (person is not null && person.TerminationDate is { } left && left < Today)
            {
                errors.Add($"{person.DisplayName} has left the company.");
            }

            var request = new SaveAssignmentRequest(m.EmployeeId, projectId, m.TotalHours, match?.Id, LaborCategoryId: m.LaborCategoryId);
            if (errors.Count == 0)
            {
                await CheckRowAsync(db, request, errors, warnings, cancellationToken);
            }

            rows.Add(new AssignmentPasteRowDto(line, names.Person(m.EmployeeId), match is null ? "Whole project" : $"{match.Code} {match.Name}",
                m.TotalHours, null, null, errors.Count == 0 ? request : null, errors, warnings));
        }

        return new AssignmentPastePreviewDto(rows);
    }

    public async Task<IReadOnlyList<AssignmentHoursLeftDto>> ListMyHoursLeftAsync(DateOnly from, DateOnly to, Guid? excludeTimesheetId = null, CancellationToken cancellationToken = default)
    {
        var me = await rules.MeAsync(cancellationToken);
        if (me is null)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var mine = await db.Set<StaffingAssignment>().AsNoTracking()
            .Where(a => a.EmployeeId == me && a.From <= to && a.To >= from).ToListAsync(cancellationToken);
        if (mine.Count == 0)
        {
            return [];
        }

        var covered = await CoveredAsync(db, mine, cancellationToken);
        var projectIds = mine.Select(a => a.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var names = await NamesAsync(db, [], mine.Select(a => a.WbsElementId), [], cancellationToken);
        var allIds = covered.Values.SelectMany(v => v).Distinct().ToList();
        var first = mine.Min(a => a.From);
        var last = mine.Max(a => a.To);
        var entries = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.EmployeeId == me && t.Status != TimesheetStatus.Rejected && t.Id != excludeTimesheetId
                      && allIds.Contains(e.WbsElementId) && e.WorkDate >= first && e.WorkDate <= last
                select new { e.WbsElementId, e.WorkDate, e.Hours })
            .ToListAsync(cancellationToken);
        return mine
            .Select(a =>
            {
                var ids = covered[a.Id];
                var charged = entries.Where(e => ids.Contains(e.WbsElementId) && e.WorkDate >= a.From && e.WorkDate <= a.To).Sum(e => e.Hours);
                var label = a.WbsElementId is { } w ? $"{names.WbsCode(w)} {names.WbsName(w)}".Trim() : "the whole project";
                return new AssignmentHoursLeftDto(a.Id, projects.GetValueOrDefault(a.ProjectId) ?? "?", label, a.From, a.To, a.TotalHours, charged, ids.ToList());
            })
            .OrderBy(a => a.ProjectCode, StringComparer.Ordinal).ThenBy(a => a.From)
            .ToList();
    }

    /// <summary>
    /// The chargeable leaf WBS elements each assignment covers: its element when that's a leaf, the leaves under it when
    /// it's a summary, every leaf on the project for the whole project. Charging and the timesheet's hours-left use this.
    /// </summary>
    internal static async Task<Dictionary<Guid, IReadOnlySet<Guid>>> CoveredAsync(IWorkbenchDbContext db, IReadOnlyCollection<StaffingAssignment> assignments, CancellationToken ct)
    {
        var projectIds = assignments.Select(a => a.ProjectId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => projectIds.Contains(w.ProjectId))
            .Select(w => new { w.Id, w.ProjectId, w.ParentId }).ToListAsync(ct);
        var parents = wbs.Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToHashSet();
        var parentOf = wbs.ToDictionary(w => w.Id, w => w.ParentId);
        var leaves = wbs.Where(w => !parents.Contains(w.Id)).ToList();
        var result = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var a in assignments)
        {
            result[a.Id] = leaves.Where(l => l.ProjectId == a.ProjectId && (a.WbsElementId is null || IsUnder(l.Id, a.WbsElementId.Value)))
                .Select(l => l.Id).ToHashSet();
        }

        return result;

        bool IsUnder(Guid id, Guid ancestor)
        {
            for (Guid? current = id; current is { } c; current = parentOf.GetValueOrDefault(c))
            {
                if (c == ancestor)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // ---- Saving ----

    /// <summary>A validated assignment: the resolved dates and labor category and the weekly spread, before anything is saved.</summary>
    private sealed record AssignmentPlan(
        SaveAssignmentRequest Request,
        StaffingAssignment? Existing,
        Employee Employee,
        Project Project,
        DateOnly From,
        DateOnly To,
        Guid? LaborCategoryId,
        IReadOnlyList<AssignmentWeekDto> Weeks,

        /// <summary>True when an edit changed nothing that moves hours, so the weeks (and any by-week adjustments) stay.</summary>
        bool KeepsWeeks);

    private async Task<StaffingAssignment> SaveOneAsync(IWorkbenchDbContext db, SaveAssignmentRequest request, CancellationToken ct)
    {
        var plan = await PlanAsync(db, request, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var assignment = plan.Existing is { } existing
            ? await db.Set<StaffingAssignment>().FirstAsync(a => a.Id == existing.Id, ct)
            : null;
        var before = assignment is null ? null : Snapshot(assignment);
        if (assignment is null)
        {
            assignment = new StaffingAssignment { CreatedBy = audit.Actor, CreatedAtUtc = now };
            db.Set<StaffingAssignment>().Add(assignment);
        }
        else
        {
            assignment.UpdatedBy = audit.Actor;
            assignment.UpdatedAtUtc = now;
        }

        assignment.EmployeeId = plan.Employee.Id;
        assignment.ProjectId = plan.Project.Id;
        assignment.WbsElementId = request.WbsElementId;
        assignment.LaborCategoryId = plan.LaborCategoryId;
        assignment.From = plan.From;
        assignment.To = plan.To;
        assignment.TotalHours = Math.Round(request.TotalHours, 2, MidpointRounding.AwayFromZero);
        assignment.Notes = Clean(request.Notes);

        var rows = plan.Existing is null ? [] : await db.Set<ResourceAllocation>().Where(a => a.AssignmentId == assignment.Id).ToListAsync(ct);
        if (plan.KeepsWeeks)
        {
            foreach (var row in rows)
            {
                row.LaborCategoryId = plan.LaborCategoryId;
                row.Notes = assignment.Notes;
            }
        }
        else
        {
            db.Set<ResourceAllocation>().RemoveRange(rows);

            // Week-by-week staffing of the same person on the same task in these weeks is taken over by the assignment.
            var weeks = plan.Weeks.Select(w => w.WeekStart).ToList();
            var taken = await db.Set<ResourceAllocation>()
                .Where(a => a.AssignmentId == null && a.EmployeeId == assignment.EmployeeId && a.ProjectId == assignment.ProjectId
                    && a.WbsElementId == assignment.WbsElementId && weeks.Contains(a.WeekStart))
                .ToListAsync(ct);
            db.Set<ResourceAllocation>().RemoveRange(taken.Where(a => (a.LaborCategoryId ?? plan.Employee.DefaultLaborCategoryId) == plan.LaborCategoryId));
            db.Set<ResourceAllocation>().AddRange(plan.Weeks.Where(w => w.Hours > 0).Select(w => NewRow(assignment, w.WeekStart, w.Hours, now)));
        }

        audit.Record(db, EntityType, assignment.Id, before is null ? "Created" : "Updated", before, Snapshot(assignment));
        return assignment;
    }

    private ResourceAllocation NewRow(StaffingAssignment assignment, DateOnly week, decimal hours, DateTime now) => new()
    {
        EmployeeId = assignment.EmployeeId, ProjectId = assignment.ProjectId, WbsElementId = assignment.WbsElementId,
        LaborCategoryId = assignment.LaborCategoryId, WeekStart = week, Hours = hours, Source = AllocationSource.Manual,
        AssignmentId = assignment.Id, Notes = assignment.Notes, CreatedBy = audit.Actor, CreatedAtUtc = now,
    };

    private async Task<AssignmentPlan> PlanAsync(IWorkbenchDbContext db, SaveAssignmentRequest request, CancellationToken ct)
    {
        if (request.TotalHours is <= 0 or > MaxTotalHours)
        {
            throw new ArgumentException($"Enter the total hours, more than 0 and up to {MaxTotalHours:#,##0}.");
        }

        StaffingAssignment? existing = null;
        if (request.Id is { } id)
        {
            existing = await db.Set<StaffingAssignment>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
                ?? throw WorkbenchNotFoundException.For("Assignment", id);
            if (existing.ProjectId != request.ProjectId)
            {
                throw new ArgumentException("An assignment can't move to another project. Assign them there instead.");
            }
        }

        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, ct)
            ?? throw new ArgumentException("Pick a person.");
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct)
            ?? throw new ArgumentException("Pick a project.");
        if (project.Status == ProjectStatus.Closed)
        {
            throw new ArgumentException($"Project {project.Code} is closed.");
        }

        WbsElement? element = null;
        if (request.WbsElementId is { } wbsId)
        {
            element = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbsId && w.ProjectId == project.Id, ct)
                ?? throw new ArgumentException($"That task isn't on project {project.Code}.");
        }

        if (request.LaborCategoryId is { } catId && !await db.Set<LaborCategory>().AnyAsync(c => c.Id == catId, ct))
        {
            throw new ArgumentException("Unknown labor category.");
        }

        var (from, to) = await DatesAsync(db, request, project, element, ct);
        if (employee.TerminationDate is { } end && end < from)
        {
            throw new ArgumentException($"{employee.DisplayName} has left the company.");
        }

        if (employee.HireDate > to)
        {
            throw new ArgumentException($"{employee.DisplayName} starts on {employee.HireDate:MMM d, yyyy}, after these dates.");
        }

        var category = request.LaborCategoryId ?? existing?.LaborCategoryId ?? employee.DefaultLaborCategoryId;
        var total = Math.Round(request.TotalHours, 2, MidpointRounding.AwayFromZero);
        var keeps = existing is not null && existing.EmployeeId == employee.Id && existing.WbsElementId == request.WbsElementId
            && existing.From == from && existing.To == to && existing.TotalHours == total;
        IReadOnlyList<AssignmentWeekDto> weeks;
        if (keeps)
        {
            weeks = (await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.AssignmentId == existing!.Id).ToListAsync(ct))
                .OrderBy(a => a.WeekStart).Select(a => new AssignmentWeekDto(a.WeekStart, a.Hours)).ToList();
        }
        else
        {
            // Weeks already past keep their hours when an assignment changes; the rest of the total is spread from this week on.
            var thisWeek = CapacityCalculator.WeekStart(Today);
            var past = existing is null ? [] : (await db.Set<ResourceAllocation>().AsNoTracking()
                    .Where(a => a.AssignmentId == existing.Id && a.WeekStart < thisWeek).ToListAsync(ct))
                .Where(a => a.WeekStart >= CapacityCalculator.WeekStart(from) && a.WeekStart <= to)
                .ToDictionary(a => a.WeekStart, a => a.Hours);
            if (past.Values.Sum() > total || (past.Count > 0 && thisWeek > to))
            {
                past = [];
            }

            var calendar = project.SheetId is { } sheetId ? await PlanReflow.CalendarAsync(db, sheetId, ct) : await PlanReflow.CalendarAsync(db, ct);
            weeks = AssignmentSpread.Spread(total, from, to, d => calendar.IsWorkingDay(d) && employee.IsActiveOn(d), past);
        }

        return new AssignmentPlan(request, existing, employee, project, from, to, category, weeks, keeps);
    }

    /// <summary>The dates asked for, else the task's dates on the plan (or the project's), starting no earlier than today.</summary>
    private async Task<(DateOnly From, DateOnly To)> DatesAsync(IWorkbenchDbContext db, SaveAssignmentRequest request, Project project, WbsElement? element, CancellationToken ct)
    {
        DateOnly? start = null, finish = null;
        if (element is not null && (request.From is null || request.To is null))
        {
            (start, finish) = await TaskDatesAsync(db, element, ct);
        }

        start ??= project.Start;
        finish ??= project.Finish;
        var from = request.From ?? (start is { } s && s > Today ? s : Today);
        var to = request.To ?? finish
            ?? throw new ArgumentException($"Pick the dates: {(element is null ? $"project {project.Code}" : element.Code)} has no finish date.");
        if (to < from)
        {
            throw new ArgumentException(request.To is null && request.From is null
                ? $"{(element is null ? $"Project {project.Code}" : element.Code)} finished on {to:MMM d, yyyy}. Pick the dates."
                : "The end date is before the start date.");
        }

        if (to.DayNumber - from.DayNumber > MaxDays)
        {
            throw new ArgumentException("That date range is too long: assign up to three years at a time.");
        }

        return (from, to);
    }

    /// <summary>The plan task's start and finish for the element (its own task, else the nearest parent's).</summary>
    private static async Task<(DateOnly? Start, DateOnly? Finish)> TaskDatesAsync(IWorkbenchDbContext db, WbsElement element, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == element.ProjectId)
            .Select(w => new { w.Id, w.ParentId, w.ItemId }).ToDictionaryAsync(w => w.Id, ct);
        for (Guid? current = element.Id; current is { } c && wbs.TryGetValue(c, out var w); current = w.ParentId)
        {
            if (w.ItemId is { } itemId)
            {
                var item = await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => new { i.Start, i.Finish }).FirstOrDefaultAsync(ct);
                if (item is { Start: not null } || item is { Finish: not null })
                {
                    return (item.Start, item.Finish);
                }
            }
        }

        return (null, null);
    }

    private async Task<AssignmentPreviewDto> PreviewOfAsync(IWorkbenchDbContext db, AssignmentPlan plan, CancellationToken ct)
    {
        var warnings = new List<string>();
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var label = plan.LaborCategoryId is { } c && categories.TryGetValue(c, out var cat) ? ProjectStaffingService.Label(cat) : null;
        if (plan.LaborCategoryId is null)
        {
            warnings.Add($"{plan.Employee.DisplayName} has no labor category, so these hours won't count against a budget. Pick one under Details.");
        }

        if (plan.Weeks.Count > 0 && plan.Weeks.Sum(w => w.Hours) < plan.Request.TotalHours - 0.005m)
        {
            warnings.Add("The hours from weeks already past are kept, so fewer hours are spread from this week on.");
        }

        var overbooked = await OverbookedAsync(db, plan, ct);
        if (overbooked.Count > 0)
        {
            var shown = overbooked.Take(3).Select(w =>
                $"{Hours(w.ThisAssignment + w.OtherHours)} of {Hours(w.Capacity)} h the week of {w.WeekStart:MMM d}" + (w.OtherHours > 0 ? $" ({Hours(w.OtherHours)} h on other work)" : ""));
            warnings.Add($"{plan.Employee.DisplayName} would be booked past their hours: {string.Join("; ", shown)}"
                + (overbooked.Count > 3 ? $"; and {overbooked.Count - 3} more {(overbooked.Count == 4 ? "week" : "weeks")}." : "."));
        }

        return new AssignmentPreviewDto(plan.From, plan.To, plan.LaborCategoryId, label, plan.Weeks, overbooked, warnings);
    }

    /// <summary>Weeks where this assignment plus everything else the person is staffed on (all projects) is more than they can work.</summary>
    private async Task<IReadOnlyList<AssignmentOverbookedWeekDto>> OverbookedAsync(IWorkbenchDbContext db, AssignmentPlan plan, CancellationToken ct)
    {
        var weeks = plan.Weeks.Where(w => w.Hours > 0 && w.WeekStart >= CapacityCalculator.WeekStart(Today)).ToList();
        if (weeks.Count == 0)
        {
            return [];
        }

        var employeeId = plan.Employee.Id;
        var (first, last) = (weeks[0].WeekStart, weeks[^1].WeekStart);
        var existingId = plan.Existing?.Id;
        var others = (await db.Set<ResourceAllocation>().AsNoTracking()
                .Where(a => a.EmployeeId == employeeId && a.WeekStart >= first && a.WeekStart <= last && (existingId == null || a.AssignmentId != existingId))
                .ToListAsync(ct))
            // Week-by-week staffing on the same task is replaced by this assignment, so it isn't "other work".
            .Where(a => !(a.AssignmentId == null && a.ProjectId == plan.Project.Id && a.WbsElementId == plan.Request.WbsElementId
                && (a.LaborCategoryId ?? plan.Employee.DefaultLaborCategoryId) == plan.LaborCategoryId))
            .GroupBy(a => a.WeekStart).ToDictionary(g => g.Key, g => g.Sum(a => a.Hours));
        var e = plan.Employee;
        var holidays = await leaveCalendar.GetHolidaysAsync(first, last.AddDays(6), [employeeId], ct);
        var leave = await leaveCalendar.GetApprovedLeaveAsync(first, last.AddDays(6), [employeeId], ct);
        var capacity = CapacityCalculator.Compute(new CapacityPerson(e.Id, e.StandardHoursPerWeek, e.HireDate, e.TerminationDate),
            weeks.Select(w => w.WeekStart).ToList(), holidays, leave, 0m).ToDictionary(c => c.WeekStart, c => c.Capacity);
        return weeks
            .Select(w => new AssignmentOverbookedWeekDto(w.WeekStart, w.Hours, others.GetValueOrDefault(w.WeekStart), capacity.GetValueOrDefault(w.WeekStart)))
            .Where(w => w.ThisAssignment + w.OtherHours > w.Capacity + 0.005m)
            .ToList();
    }

    /// <summary>Validates a pasted or copied row the way saving would, adding its errors and warnings.</summary>
    private async Task CheckRowAsync(IWorkbenchDbContext db, SaveAssignmentRequest request, List<string> errors, List<string> warnings, CancellationToken ct)
    {
        try
        {
            var plan = await PlanAsync(db, request, ct);
            warnings.AddRange((await PreviewOfAsync(db, plan, ct)).Warnings);
            var same = await db.Set<StaffingAssignment>().AsNoTracking()
                .Where(a => a.ProjectId == request.ProjectId && a.EmployeeId == request.EmployeeId && a.WbsElementId == request.WbsElementId
                    && a.From <= plan.To && a.To >= plan.From)
                .FirstOrDefaultAsync(ct);
            if (same is not null)
            {
                warnings.Add($"{plan.Employee.DisplayName} is already assigned {Hours(same.TotalHours)} h here ({same.From:MMM d} – {same.To:MMM d}); this adds another assignment.");
            }
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
        }
    }

    // ---- Reading ----

    private sealed record Names(
        IReadOnlyDictionary<Guid, string> People,
        IReadOnlyDictionary<Guid, (string Code, string Name)> Wbs,
        IReadOnlyDictionary<Guid, string> Categories)
    {
        public string Person(Guid id) => People.GetValueOrDefault(id) ?? "?";

        public string? WbsCode(Guid? id) => id is { } w && Wbs.TryGetValue(w, out var x) ? x.Code : null;

        public string? WbsName(Guid? id) => id is { } w && Wbs.TryGetValue(w, out var x) ? x.Name : null;

        public string? Category(Guid? id) => id is { } c ? Categories.GetValueOrDefault(c) : null;
    }

    private static async Task<Names> NamesAsync(IWorkbenchDbContext db, IEnumerable<Guid> people, IEnumerable<Guid?> wbs, IEnumerable<Guid?> categories, CancellationToken ct)
    {
        var personIds = people.Distinct().ToList();
        var wbsIds = wbs.OfType<Guid>().Distinct().ToList();
        var catIds = categories.OfType<Guid>().Distinct().ToList();
        return new Names(
            await db.Set<Employee>().AsNoTracking().Where(e => personIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct),
            await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => (w.Code, w.Name), ct),
            (await db.Set<LaborCategory>().AsNoTracking().Where(c => catIds.Contains(c.Id)).ToListAsync(ct)).ToDictionary(c => c.Id, ProjectStaffingService.Label));
    }

    private static async Task<IReadOnlyList<StaffingAssignmentDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyCollection<StaffingAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0)
        {
            return [];
        }

        var ids = assignments.Select(a => a.Id).ToList();
        var weeks = (await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.AssignmentId != null && ids.Contains(a.AssignmentId.Value)).ToListAsync(ct))
            .GroupBy(a => a.AssignmentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AssignmentWeekDto>)g.OrderBy(a => a.WeekStart).Select(a => new AssignmentWeekDto(a.WeekStart, a.Hours)).ToList());
        var names = await NamesAsync(db, assignments.Select(a => a.EmployeeId), assignments.Select(a => a.WbsElementId), assignments.Select(a => a.LaborCategoryId), ct);
        var projectIds = assignments.Select(a => a.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);

        // Charged: the person's hours on the codes the assignment covers between its dates (rejected timesheets don't count).
        var covered = await CoveredAsync(db, assignments, ct);
        var employeeIds = assignments.Select(a => a.EmployeeId).Distinct().ToList();
        var codes = covered.Values.SelectMany(v => v).Distinct().ToList();
        var (first, last) = (assignments.Min(a => a.From), assignments.Max(a => a.To));
        var entries = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status != TimesheetStatus.Rejected && employeeIds.Contains(t.EmployeeId) && codes.Contains(e.WbsElementId)
                      && e.WorkDate >= first && e.WorkDate <= last
                select new { t.EmployeeId, e.WbsElementId, e.WorkDate, e.Hours })
            .ToListAsync(ct);
        return assignments
            .Select(a => new StaffingAssignmentDto(
                a.Id, a.EmployeeId, names.Person(a.EmployeeId), a.ProjectId, projects.GetValueOrDefault(a.ProjectId) ?? "?",
                a.WbsElementId, names.WbsCode(a.WbsElementId), names.WbsName(a.WbsElementId), a.LaborCategoryId, names.Category(a.LaborCategoryId),
                a.From, a.To, a.TotalHours,
                entries.Where(e => e.EmployeeId == a.EmployeeId && covered[a.Id].Contains(e.WbsElementId) && e.WorkDate >= a.From && e.WorkDate <= a.To).Sum(e => e.Hours),
                weeks.GetValueOrDefault(a.Id) ?? [], a.Notes))
            .OrderBy(a => a.EmployeeName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.WbsCode is null ? 1 : 0).ThenBy(a => a.WbsCode, StringComparer.Ordinal).ThenBy(a => a.From)
            .ToList();
    }

    private static string Hours(decimal hours) => hours.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string? Clean(string? notes) => string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 500)];

    private static object Snapshot(StaffingAssignment a) => new { a.EmployeeId, a.ProjectId, a.WbsElementId, a.LaborCategoryId, a.From, a.To, a.TotalHours };
}

/// <summary>
/// Spreads an assignment's total over its working days, evenly: each working day gets the same share, weeks get their
/// days' shares, and each week is rounded to the quarter hour on the running total so the weeks add up to the total
/// exactly and no week is front-loaded. A partial first or last week gets only its days' share.
/// </summary>
public static class AssignmentSpread
{
    /// <param name="total">The hours to spread.</param>
    /// <param name="from">First day.</param>
    /// <param name="to">Last day.</param>
    /// <param name="isWorkingDay">Working days (the calendar's, and days the person is employed).</param>
    /// <param name="fixedWeeks">Weeks that keep their hours (by Monday); the rest of the total is spread over the other weeks.</param>
    public static IReadOnlyList<AssignmentWeekDto> Spread(decimal total, DateOnly from, DateOnly to, Func<DateOnly, bool> isWorkingDay, IReadOnlyDictionary<DateOnly, decimal>? fixedWeeks = null)
    {
        if (total < 0)
        {
            throw new ArgumentException("The total can't be negative.");
        }

        fixedWeeks ??= new Dictionary<DateOnly, decimal>();
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays)
            .Where(d => isWorkingDay(d) && !fixedWeeks.ContainsKey(CapacityCalculator.WeekStart(d)))
            .ToList();
        var remaining = total - fixedWeeks.Values.Sum();
        if (days.Count == 0 && remaining > 0)
        {
            throw new ArgumentException($"There are no working days between {from.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} and {to.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} to spread the hours over.");
        }

        var result = fixedWeeks.Select(f => new AssignmentWeekDto(f.Key, f.Value)).ToList();
        var counted = 0;
        var placed = 0m;
        foreach (var week in days.GroupBy(CapacityCalculator.WeekStart).OrderBy(g => g.Key))
        {
            counted += week.Count();
            var through = counted == days.Count ? remaining : Quarter(remaining * counted / days.Count);
            result.Add(new AssignmentWeekDto(week.Key, through - placed));
            placed = through;
        }

        return result.OrderBy(w => w.WeekStart).ToList();
    }

    private static decimal Quarter(decimal hours) => Math.Round(hours * 4, MidpointRounding.AwayFromZero) / 4;
}
