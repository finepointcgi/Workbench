using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Collaboration;
using Workbench.Application.Financials;
using Workbench.Application.IndirectRates;
using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Resourcing;

/// <summary>
/// The resource-loaded plan: a project's plan tasks with their people, hours and cost, the totals against the budget, and
/// "try changes". It owns no data: people are <see cref="StaffingAssignment"/> rows saved through
/// <see cref="StaffingAssignmentService"/> (the same spread and rules as Budget &amp; staffing), tasks move with
/// <see cref="ScheduleEngine"/> as the plan grid's edits do, cost is hours × each person's cost rate in each week
/// (<see cref="EmployeeCostRate"/>) with the burden multiplier of the indirect rates in force, and "booked past their hours"
/// is <see cref="CapacityCalculator.IsOverbooked"/> on every project. Permission checks and cost masking are in the
/// identity decorator.
/// </summary>
public sealed class ResourcePlanService(
    IWorkbenchDbContextFactory dbFactory,
    IResourceLeaveCalendar leaveCalendar,
    StaffingAssignmentService staffing,
    TimeProvider clock,
    IScheduleHealthService? health = null,
    ActivityRecorder? activity = null) : IResourcePlanService
{
    /// <summary>How far a fix looks for room: a year.</summary>
    public const int FixWeeksAhead = 52;

    /// <summary>How many other people "pick someone else" offers.</summary>
    public const int MaxOtherPeople = 3;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private DateOnly ThisWeek => CapacityCalculator.WeekStart(Today);

    public async Task<ResourcePlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var model = await LoadAsync(db, projectId, tracked: false, cancellationToken);
        var bookings = model.CurrentBookings();
        var snapshot = await SnapshotAsync(db, model, bookings, new HashSet<Guid>(), cancellationToken);
        PlanHealthSummaryDto? summary = null;
        if (health is not null && model.Plan is not null)
        {
            try
            {
                var check = await health.GetAsync(model.Plan.Sheet.Id, cancellationToken: cancellationToken);
                summary = new PlanHealthSummaryDto(check.Passed, check.Failed, check.Checks.Count - check.Passed - check.Failed);
            }
            catch (Exception ex) when (ex is WorkbenchForbiddenException or WorkbenchNotFoundException or ArgumentException)
            {
                summary = null; // the schedule check is for the plan's viewers
            }
        }

        return new ResourcePlanDto(model.Project.Id, model.Project.Code, model.Project.SheetId ?? Guid.Empty, Today,
            ShowsCost: true, CostIncludesBurden: model.Multiplier is not null, CanChange: true, CanMoveTasks: model.Plan is not null,
            snapshot.Tasks, snapshot.NotOnATask, snapshot.Totals, summary, model.Notes.Concat(snapshot.Notes).Distinct().ToList());
    }

    public async Task<PlanTryDto> TryAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Limit(changes);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var model = await LoadAsync(db, projectId, tracked: false, cancellationToken);
        var before = await SnapshotAsync(db, model, model.CurrentBookings(), new HashSet<Guid>(), cancellationToken);
        var scenario = await BuildAsync(db, model, changes, cancellationToken);
        var after = await SnapshotAsync(db, model, scenario.Bookings, scenario.MovedItems, cancellationToken);
        int? moved = before.Totals.Finish is { } a && after.Totals.Finish is { } b ? model.Calendar.Between(a, b) : null;
        return new PlanTryDto(before.Totals, after.Totals, moved, after.Tasks, after.NotOnATask, scenario.Errors);
    }

    public async Task<PlanAppliedDto> ApplyAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Limit(changes);
        if (changes.Count == 0)
        {
            throw new ArgumentException("There are no changes to make.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var model = await LoadAsync(db, projectId, tracked: true, cancellationToken);
        var scenario = await BuildAsync(db, model, changes, cancellationToken);
        if (scenario.Errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", scenario.Errors) + " Nothing was changed.");
        }

        // What puts it all back: the moved tasks' dates, and each person as they were (their exact weeks).
        var undo = new List<PlanChange>();
        foreach (var itemId in scenario.MovedItems.Where(id => !model.Outline!.IsSummary(id)))
        {
            var (start, finish) = model.BaseDates[itemId];
            undo.Add(new PlanChange(PlanChangeKind.MoveTask, ItemId: itemId, Start: start, Finish: finish));
        }

        foreach (var booking in scenario.Bookings.Where(b => b.Request is not null && !b.IsNew))
        {
            var was = model.Assignments.First(a => a.Id == booking.AssignmentId);
            undo.Add(PutBack(was, model.Weeks.GetValueOrDefault(was.Id) ?? [], recreate: false, null));
        }

        foreach (var removed in scenario.Removed)
        {
            undo.Add(PutBack(removed, model.Weeks.GetValueOrDefault(removed.Id) ?? [], recreate: true, model.ItemOf(removed.WbsElementId)));
        }

        // Hours staffed week by week that are changed or taken off become assignments first, their weeks unchanged, in a save
        // of their own (on its own that changes no hours, cost or charging); the change then applies to the assignment.
        var converting = scenario.Bookings.Where(b => b.Request is not null && !b.IsNew).Select(b => b.AssignmentId!.Value)
            .Concat(scenario.Removed.Select(r => r.Id)).Where(model.WeekByWeek.ContainsKey).Distinct().ToList();
        if (converting.Count > 0)
        {
            await using var convertDb = await dbFactory.CreateAsync(cancellationToken);
            foreach (var id in converting)
            {
                await staffing.ConvertAsync(convertDb, model.WeekByWeek[id], cancellationToken);
            }

            await convertDb.SaveChangesAsync(cancellationToken);
        }

        // Everything in one save: the moved tasks (already moved and reflowed on the tracked items) and the people.
        foreach (var booking in scenario.Bookings.Where(b => b.Request is not null))
        {
            var saved = await staffing.SaveOneAsync(db, booking.Request!, cancellationToken, booking.ExactWeeks);
            if (booking.IsNew)
            {
                undo.Add(new PlanChange(PlanChangeKind.RemovePerson, AssignmentId: saved.Id));
            }
        }

        foreach (var removed in scenario.Removed)
        {
            await staffing.DeleteOneAsync(db, removed.Id, cancellationToken);
        }

        if (scenario.MovedItems.Count > 0 && model.Plan is { } plan)
        {
            var sheet = await db.Sheets.FirstAsync(s => s.Id == plan.Sheet.Id, cancellationToken);
            sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);
        await UnassignAsync(db, model, changes.Where(c => c.Kind == PlanChangeKind.Unassign).ToList(), cancellationToken);
        await StaffingAssignmentService.SyncAssigneesAsync(db, model.Project.Id, cancellationToken);

        // Open grids, Gantts and calendars refresh as they do for any edit.
        if (activity is not null && model.Plan is { } shown)
        {
            foreach (var itemId in scenario.MovedItems)
            {
                var item = model.Plan.Items.First(i => i.Id == itemId);
                await activity.RecordAsync(shown.Sheet.Id, itemId, "updated", $"Moved {item.Title} to {Range(item.Start, item.Finish)} from the plan's people view", SheetChangeKind.ItemUpdated);
            }
        }

        // Summary rows follow their subtasks, so only tasks count as moved.
        var tasks = scenario.MovedItems.Count(id => !model.Outline!.IsSummary(id));
        var added = scenario.Bookings.Count(b => b.IsNew);
        var changed = scenario.Bookings.Count(b => b.Request is not null && !b.IsNew);
        return new PlanAppliedDto(tasks, added + changed + scenario.Removed.Count, Describe(tasks, added, changed, scenario.Removed.Count), undo);
    }

    public async Task<IReadOnlyList<OverbookingFixDto>> ListFixesAsync(Guid projectId, Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var model = await LoadAsync(db, projectId, tracked: false, cancellationToken);
        var bookings = model.CurrentBookings();
        var booking = bookings.FirstOrDefault(b => b.AssignmentId == assignmentId)
            ?? throw WorkbenchNotFoundException.For("Assignment", assignmentId);
        var person = model.People[booking.EmployeeId];

        // Their load on everything else (other projects, other tasks here), by week, and what they can work.
        var others = await LoadByWeekAsync(db, model, bookings.Where(b => !ReferenceEquals(b, booking)).ToList(), [booking.EmployeeId], cancellationToken);
        var capacity = await CapacityAsync([person], ThisWeek, ThisWeek.AddDays(7 * (FixWeeksAhead + 60)), cancellationToken);
        if (OverWeeks(booking.Weeks, booking.EmployeeId, others, capacity).Count == 0)
        {
            return [];
        }

        var fixes = new List<OverbookingFixDto>();
        var first = person.DisplayName.Split(' ')[0];
        var where = model.ItemOf(booking.WbsElementId) is { } taskId ? model.Plan!.Items.First(i => i.Id == taskId).Title : "the project";

        // 1. The same hours over more weeks, from the same start.
        for (var k = 1; k <= FixWeeksAhead; k++)
        {
            var to = booking.To.AddDays(7 * k);
            var weeks = Spread(model, person, booking.Hours, booking.From, to);
            if (weeks is not null && OverWeeks(weeks, person.Id, others, capacity).Count == 0)
            {
                var perWeek = booking.Hours / Math.Max(1, weeks.Count(w => w.Hours > 0));
                var past = model.ItemOf(booking.WbsElementId) is { } t && model.Plan!.Items.First(i => i.Id == t).Finish is { } f && to > f
                    ? $" That's {Weeks(CapacityCalculator.WeekStart(to).DayNumber - CapacityCalculator.WeekStart(f).DayNumber)} past {where}'s finish; the task's dates stay."
                    : " The task's dates stay.";
                fixes.Add(new OverbookingFixDto(OverbookingFixKind.SpreadOverMoreWeeks, $"Spread over {Weeks(7 * weeks.Count)}",
                    $"Spread {first}'s {H(booking.Hours)} h on {where} to {to.ToString("MMM d", CultureInfo.InvariantCulture)}, about {H(Math.Round(perWeek, 1))} h a week, so each week fits around their other work.{past}",
                    [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: booking.AssignmentId, To: to)]));
                break;
            }
        }

        // 2. Move the task (and its successors) to start when they have room.
        if (model.ItemOf(booking.WbsElementId) is { } itemId && model.Plan is not null)
        {
            var item = model.Plan.Items.First(i => i.Id == itemId);
            if (item.Start is { } start && item.Finish is { } finish)
            {
                for (var k = 1; k <= FixWeeksAhead; k++)
                {
                    var newStart = model.Calendar.OnOrAfter(start.AddDays(7 * k));
                    var delta = model.Calendar.Between(start, newStart);
                    var from = Shift(model, booking.From, delta);
                    var to = Shift(model, booking.To, delta);
                    var weeks = Spread(model, person, booking.Hours, from, to);
                    if (weeks is null || OverWeeks(weeks, person.Id, others, capacity).Count > 0)
                    {
                        continue;
                    }

                    PlanChange[] change = [new PlanChange(PlanChangeKind.MoveTask, ItemId: itemId, Start: newStart)];
                    var tried = await TryAsync(projectId, change, cancellationToken);
                    var finishText = tried.After.Finish is { } after && tried.Before.Finish is { } was && after != was
                        ? $" The plan then finishes {after.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} ({Days(tried.FinishMovesDays ?? 0)} later)."
                        : " The plan's finish doesn't move.";
                    var newlyOver = tried.After.OverbookedPeople.Except(tried.Before.OverbookedPeople).ToList();
                    var othersText = newlyOver.Count > 0 ? $" {string.Join(", ", newlyOver)} would then be booked past their hours." : "";
                    fixes.Add(new OverbookingFixDto(OverbookingFixKind.MoveAfterOtherWork, $"Move {item.Title} to {newStart.ToString("MMM d", CultureInfo.InvariantCulture)}",
                        $"Start {item.Title} on {newStart.ToString("MMM d", CultureInfo.InvariantCulture)}, after {first}'s other work; its successors and their people move with it.{finishText}{othersText}",
                        change));
                    break;
                }
            }
        }

        // 3. Someone else in the same labor category with room in those weeks.
        if (booking.LaborCategoryId is { } category)
        {
            var label = model.Categories.TryGetValue(category, out var cat) ? ProjectStaffingService.Label(cat) : "this labor category";
            var everyone = await db.Set<Employee>().AsNoTracking()
                .Where(e => e.Id != person.Id && e.HireDate <= booking.From && (e.TerminationDate == null || e.TerminationDate >= booking.To))
                .ToListAsync(cancellationToken);
            var qualified = await ProjectStaffingService.QualificationsAsync(db, everyone.Select(e => e.Id).ToList(), cancellationToken);
            var candidates = everyone.Where(e => StaffingCalculator.Qualifies(qualified, e.Id, category)).ToList();
            if (candidates.Count > 0)
            {
                var load = await LoadByWeekAsync(db, model, bookings, candidates.Select(c => c.Id).ToList(), cancellationToken);
                var room = await CapacityAsync(candidates, ThisWeek, CapacityCalculator.WeekStart(booking.To), cancellationToken);
                var fits = candidates
                    .Select(c => (Person: c, Weeks: Spread(model, c, booking.Hours, booking.From, booking.To)))
                    .Where(c => c.Weeks is not null && OverWeeks(c.Weeks, c.Person.Id, load, room).Count == 0)
                    .Select(c => (c.Person, Booked: c.Weeks!.Where(w => w.WeekStart >= ThisWeek).Sum(w => load.GetValueOrDefault((c.Person.Id, w.WeekStart)))))
                    .OrderBy(c => c.Booked).ThenBy(c => c.Person.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxOtherPeople);
                foreach (var (other, _) in fits)
                {
                    fixes.Add(new OverbookingFixDto(OverbookingFixKind.SomeoneElse, $"Give it to {other.DisplayName}",
                        $"Give {first}'s {H(booking.Hours)} h on {where} to {other.DisplayName} ({label}), who has room in those weeks.",
                        [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: booking.AssignmentId, EmployeeId: other.Id, LaborCategoryId: category)]));
                }
            }
        }

        return fixes;
    }

    // ---- Loading ----

    /// <summary>Someone's hours on the project: an assignment (or a new one, or hours staffed week by week seen as one).</summary>
    private sealed record Booking(
        Guid? AssignmentId,
        Guid EmployeeId,
        Guid? WbsElementId,
        Guid? LaborCategoryId,
        DateOnly From,
        DateOnly To,
        decimal Hours,
        IReadOnlyList<AssignmentWeekDto> Weeks,
        bool Changed = false,
        bool IsNew = false,

        /// <summary>What to save to make this booking (null: it's as saved).</summary>
        SaveAssignmentRequest? Request = null,
        IReadOnlyList<AssignmentWeekDto>? ExactWeeks = null);

    private sealed class Model
    {
        public required Project Project { get; init; }
        public required PlanState? Plan { get; init; }
        public PlanOutline? Outline { get; set; }
        public required WorkCalendar Calendar { get; init; }
        public required Dictionary<Guid, WbsElement> Wbs { get; init; }
        public required List<StaffingAssignment> Assignments { get; init; }
        public required Dictionary<Guid, IReadOnlyList<AssignmentWeekDto>> Weeks { get; init; }
        /// <summary>
        /// Hours staffed week by week, by the id they'd have as an assignment. They're in <see cref="Assignments"/> like any
        /// other, so they can be changed here; changing one makes it a real assignment.
        /// </summary>
        public required Dictionary<Guid, StaffingAssignmentService.WeekByWeekGroup> WeekByWeek { get; init; }
        public required Dictionary<Guid, Employee> People { get; init; }

        /// <summary>Each plan task's assignees (employees), including those with no hours on it yet.</summary>
        public required Dictionary<Guid, List<Guid>> Assignees { get; init; }

        public required Dictionary<Guid, LaborCategory> Categories { get; init; }
        public required EffectiveRateTable<Guid> Rates { get; init; }
        public required decimal? Multiplier { get; init; }
        public required string? BudgetName { get; init; }
        public required List<BudgetLine> LaborBudget { get; init; }
        public required Dictionary<Guid, (DateOnly? Start, DateOnly? Finish)> BaseDates { get; init; }
        public required List<string> Notes { get; init; }

        /// <summary>The ceiling, funding, type and fee the plan's dollars are measured against.</summary>
        public required ProjectAccounting.ProjectTerms.Loaded Terms { get; init; }

        /// <summary>The contract's bill rates by labor category (T&amp;M and labor-hour work bills at them).</summary>
        public required EffectiveRateTable<Guid> BillRates { get; init; }

        /// <summary>The plan task a charge code was made from, if it's on this plan.</summary>
        public Guid? ItemOf(Guid? wbsElementId) =>
            wbsElementId is { } w && Wbs.TryGetValue(w, out var e) && e.ItemId is { } item && BaseDates.ContainsKey(item) ? item : null;

        public Guid? WbsOf(Guid itemId) => Wbs.Values.FirstOrDefault(w => w.ItemId == itemId)?.Id;

        public Guid? CategoryOf(Guid? category, Guid employeeId) => category ?? People.GetValueOrDefault(employeeId)?.DefaultLaborCategoryId;

        public List<Booking> CurrentBookings() =>
            Assignments.Select(a => new Booking(a.Id, a.EmployeeId, a.WbsElementId, a.LaborCategoryId, a.From, a.To, a.TotalHours, Weeks.GetValueOrDefault(a.Id) ?? []))
                .ToList();
    }

    private async Task<Model> LoadAsync(IWorkbenchDbContext db, Guid projectId, bool tracked, CancellationToken ct)
    {
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var notes = new List<string>();
        PlanState? plan = null;
        if (project.SheetId is { } sheetId && await db.Sheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, ct) is { } sheet)
        {
            plan = await PlanReflow.LoadAsync(db, sheet, ct, tracked);
        }
        else
        {
            notes.Add("This project has no plan yet, so people show on the whole project.");
        }

        var calendar = plan?.Calendar ?? await PlanReflow.CalendarAsync(db, ct);
        var assignments = await db.Set<StaffingAssignment>().AsNoTracking().Where(a => a.ProjectId == projectId).ToListAsync(ct);
        var ids = assignments.Select(a => a.Id).ToList();
        var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.ProjectId == projectId && a.Hours > 0).ToListAsync(ct);
        var weeks = rows.Where(r => r.AssignmentId is { } id && ids.Contains(id)).GroupBy(r => r.AssignmentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AssignmentWeekDto>)g.OrderBy(r => r.WeekStart).Select(r => new AssignmentWeekDto(r.WeekStart, r.Hours)).ToList());
        var people = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);

        // Hours staffed week by week show (and change) as the assignments they'd be.
        var weekByWeek = StaffingAssignmentService.GroupWeekByWeek(rows, e => people.GetValueOrDefault(e)?.DefaultLaborCategoryId)
            .ToDictionary(g => g.Assignment.Id);
        foreach (var group in weekByWeek.Values)
        {
            assignments.Add(group.Assignment);
            weeks[group.Assignment.Id] = group.Rows.GroupBy(r => r.WeekStart).OrderBy(g => g.Key).Select(g => new AssignmentWeekDto(g.Key, g.Sum(r => r.Hours))).ToList();
        }

        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        // Future labor is burdened at the current fiscal year's labor multiplier, as in the burdened financials.
        var book = await RateBook.LoadAsync(db, ct);
        decimal? multiplier = null;
        if (book.HasRates && (book.YearOf(Today)?.Year ?? book.Years.LastOrDefault()?.Year) is { } year)
        {
            multiplier = BurdenCascade.LaborMultiplier(book.Steps, book.For(year, IndirectRateBasis.Actual).Of);
        }

        var terms = await ProjectAccounting.ProjectTerms.LoadAsync(db, project, ct);
        var billRates = new EffectiveRateTable<Guid>();
        if (project.ContractId is { } contractId)
        {
            foreach (var r in await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contractId).ToListAsync(ct))
            {
                billRates.Add(r.LaborCategoryId, r.EffectiveFrom, r.HourlyBillRate);
            }
        }

        var versions = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        var version = versions.Where(v => v.Kind == BudgetKind.Current).MaxBy(v => v.CreatedAtUtc)
            ?? versions.Where(v => v.Kind == BudgetKind.Baseline).MaxBy(v => v.CreatedAtUtc);
        var budget = version is null ? [] : await db.Set<BudgetLine>().AsNoTracking()
            .Where(l => l.BudgetVersionId == version.Id && l.CostType == BudgetCostType.Labor).ToListAsync(ct);

        var byEmail = people.Values.GroupBy(e => e.Email.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        var assignees = plan is null ? [] : (await db.Set<ItemAssignee>().AsNoTracking().Where(a => a.SheetId == plan.Sheet.Id).ToListAsync(ct))
            .Where(a => byEmail.ContainsKey(a.Email.ToLowerInvariant()))
            .GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.Select(a => byEmail[a.Email.ToLowerInvariant()]).Distinct().ToList());

        var model = new Model
        {
            Project = project,
            Plan = plan,
            Calendar = calendar,
            Wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, ct),
            Assignments = assignments,
            Weeks = weeks,
            WeekByWeek = weekByWeek,
            People = people,
            Assignees = assignees,
            Categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
            Rates = rates,
            Multiplier = multiplier,
            BudgetName = budget.Count > 0 ? version!.Name : null,
            LaborBudget = budget,
            BaseDates = plan?.Items.ToDictionary(i => i.Id, i => (i.Start, i.Finish)) ?? [],
            Notes = notes,
            Terms = terms,
            BillRates = billRates,
        };
        model.Outline = plan is null ? null : new PlanOutline(plan.Items);
        return model;
    }

    /// <summary>Takes people with no hours yet off their tasks (and any hours they do have there, as the Assignees cell does).</summary>
    private static async Task UnassignAsync(IWorkbenchDbContext db, Model model, IReadOnlyList<PlanChange> unassign, CancellationToken ct)
    {
        foreach (var change in unassign)
        {
            if (model.ItemOf(change.WbsElementId) is not { } itemId || change.EmployeeId is not { } employeeId
                || model.People.GetValueOrDefault(employeeId)?.Email.ToLowerInvariant() is not { } email)
            {
                continue;
            }

            db.Set<ItemAssignee>().RemoveRange(await db.Set<ItemAssignee>().Where(a => a.ItemId == itemId && a.Email == email).ToListAsync(ct));
        }

        if (unassign.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    // ---- Trying changes ----

    private sealed record Scenario(List<Booking> Bookings, List<StaffingAssignment> Removed, IReadOnlySet<Guid> MovedItems, List<string> Errors);

    /// <summary>
    /// Applies the changes to the model: moves tasks on the plan's items (tracked or not, the caller decides whether they're
    /// saved), reflows, re-dates the people on every task that moved, and works out each changed person's weeks the way
    /// saving would. Nothing is saved here.
    /// </summary>
    private async Task<Scenario> BuildAsync(IWorkbenchDbContext db, Model model, IReadOnlyList<PlanChange> changes, CancellationToken ct)
    {
        var errors = new List<string>();
        var moved = new HashSet<Guid>();
        var before = model.BaseDates;
        if (changes.Any(c => c.Kind == PlanChangeKind.MoveTask))
        {
            if (model.Plan is not { } plan)
            {
                errors.Add("This project has no plan, so there are no tasks to move.");
            }
            else
            {
                // Compare with the plan as the grid would leave it (a task typed on a weekend starts the next working day),
                // so only what these changes move counts as moved.
                ScheduleEngine.Reflow(plan.Items, plan.Links, plan.Calendar, plan.PercentColumn?.Id, plan.Constraints);
                before = plan.Items.ToDictionary(i => i.Id, i => (i.Start, i.Finish));
                var outline = model.Outline!;
                foreach (var change in changes.Where(c => c.Kind == PlanChangeKind.MoveTask))
                {
                    MoveTask(model, outline, change, errors);
                }

                ScheduleEngine.Reflow(plan.Items, plan.Links, plan.Calendar, plan.PercentColumn?.Id, plan.Constraints);
                moved.UnionWith(plan.Items.Where(i => before.TryGetValue(i.Id, out var was) && (was.Start != i.Start || was.Finish != i.Finish)).Select(i => i.Id));
            }
        }

        var bookings = new List<Booking>();
        var removed = new List<StaffingAssignment>();
        var known = model.Assignments.Select(a => a.Id).ToHashSet();
        foreach (var unknown in changes.Where(c => c.Kind != PlanChangeKind.MoveTask && c.AssignmentId is { } id && !known.Contains(id)).Select(c => c.AssignmentId!.Value).Distinct())
        {
            errors.Add("Someone on the plan was changed or removed by someone else. Reload and try again.");
        }

        foreach (var a in model.Assignments)
        {
            var name = model.People.GetValueOrDefault(a.EmployeeId)?.DisplayName ?? "?";
            if (changes.Any(c => c.Kind == PlanChangeKind.RemovePerson && c.AssignmentId == a.Id))
            {
                removed.Add(a);
                continue;
            }

            var set = changes.LastOrDefault(c => c.Kind == PlanChangeKind.SetPerson && c.AssignmentId == a.Id);
            var weeks = model.Weeks.GetValueOrDefault(a.Id) ?? [];
            var (from, to) = (a.From, a.To);
            var taskId = model.ItemOf(a.WbsElementId);
            var follows = taskId is { } moving && moved.Contains(moving);
            if (follows && set?.From is null && set?.To is null)
            {
                var itemId = taskId!.Value;
                // People on a task that moved move with it: their first and last days by as many working days as its start and finish.
                var item = model.Plan!.Items.First(i => i.Id == itemId);
                var (wasStart, wasFinish) = before[itemId];
                if (wasStart is { } ws && item.Start is { } ns && from >= Today)
                {
                    from = Max(Shift(model, from, model.Calendar.Between(ws, ns)), Today);
                }

                if (wasFinish is { } wf && item.Finish is { } nf)
                {
                    to = Shift(model, to, model.Calendar.Between(wf, nf), backwards: true);
                }

                to = Max(to, from);
            }

            if (set is null && !follows)
            {
                bookings.Add(new Booking(a.Id, a.EmployeeId, a.WbsElementId, a.LaborCategoryId, a.From, a.To, a.TotalHours, weeks));
                continue;
            }

            var hours = set?.Weeks is { } exact ? exact.Sum(w => w.Hours) : set?.Hours ?? a.TotalHours;
            var request = new SaveAssignmentRequest(set?.EmployeeId ?? a.EmployeeId, a.ProjectId, hours, a.WbsElementId,
                set?.From ?? from, set?.To ?? to, set?.LaborCategoryId ?? a.LaborCategoryId, a.Notes, a.Id);
            var booking = await PlannedAsync(db, request, set?.Weeks, isNew: false, name, errors, ct, model.WeekByWeek.GetValueOrDefault(a.Id));
            bookings.Add(booking ?? new Booking(a.Id, a.EmployeeId, a.WbsElementId, a.LaborCategoryId, a.From, a.To, a.TotalHours, weeks));
        }

        foreach (var add in changes.Where(c => c.Kind == PlanChangeKind.SetPerson && c.AssignmentId is null))
        {
            if (add.EmployeeId is not { } employeeId || !model.People.TryGetValue(employeeId, out var person))
            {
                errors.Add("Pick a person to add.");
                continue;
            }

            if (add.Weeks is null && add.Hours is not > 0)
            {
                errors.Add($"Enter {person.DisplayName}'s hours.");
                continue;
            }

            Guid? wbsId = add.WbsElementId;
            DateOnly? from = add.From, to = add.To;
            if (add.ItemId is { } itemId)
            {
                var item = model.Plan?.Items.FirstOrDefault(i => i.Id == itemId);
                if (item is null)
                {
                    errors.Add("That task isn't on the plan any more. Reload and try again.");
                    continue;
                }

                wbsId = model.WbsOf(itemId);
                if (wbsId is null)
                {
                    errors.Add($"{item.Title} isn't a charge code yet (milestones never are), so nobody can be assigned to it.");
                    continue;
                }

                from ??= item.Start is { } s && s > Today ? s : Today;
                to ??= item.Finish;
            }
            else if (wbsId is { } w && !model.Wbs.ContainsKey(w))
            {
                errors.Add($"That charge code isn't on project {model.Project.Code}.");
                continue;
            }

            var hours = add.Weeks is { } exact ? exact.Sum(x => x.Hours) : add.Hours!.Value;
            var request = new SaveAssignmentRequest(employeeId, model.Project.Id, hours, wbsId, from, to, add.LaborCategoryId);
            if (await PlannedAsync(db, request, add.Weeks, isNew: true, person.DisplayName, errors, ct) is { } booking)
            {
                bookings.Add(booking);
            }
        }

        return new Scenario(bookings, removed, moved, errors.Distinct().ToList());
    }

    private static void MoveTask(Model model, PlanOutline outline, PlanChange change, List<string> errors)
    {
        var item = change.ItemId is { } id ? outline.Find(id) : null;
        if (item is null)
        {
            errors.Add("That task isn't on the plan any more. Reload and try again.");
            return;
        }

        var summary = outline.IsSummary(item.Id);
        if (change.Start is { } start && change.Finish is { } finish && !summary)
        {
            if (finish < start)
            {
                errors.Add($"{item.Title} can't finish before it starts.");
                return;
            }

            item.Start = start;
            item.Finish = item.IsMilestone ? start : finish;
        }
        else if (change.Start is { } only)
        {
            ScheduleEngine.ShiftTo(outline, item, only, model.Calendar);
        }
        else if (change.Finish is { } end)
        {
            if (summary)
            {
                errors.Add($"{item.Title}'s dates come from its subtasks. Move a subtask instead.");
                return;
            }

            if (item.Start is { } s && end < s)
            {
                errors.Add($"{item.Title} can't finish before it starts ({s.ToString("MMM d", CultureInfo.InvariantCulture)}).");
                return;
            }

            item.Finish = end;
            if (item.IsMilestone)
            {
                item.Start = end;
            }
        }
        else
        {
            errors.Add($"Give {item.Title} a new start or finish.");
        }
    }

    /// <summary>The booking a save would make (dates, category, weekly spread), or null with the reason added to <paramref name="errors"/>.</summary>
    private async Task<Booking?> PlannedAsync(IWorkbenchDbContext db, SaveAssignmentRequest request, IReadOnlyList<AssignmentWeekDto>? exact, bool isNew, string name, List<string> errors,
        CancellationToken ct, StaffingAssignmentService.WeekByWeekGroup? weekByWeek = null)
    {
        try
        {
            var plan = await staffing.PlanAsync(db, request, ct, weekByWeek);
            var weeks = exact?.Where(w => w.Hours > 0).OrderBy(w => w.WeekStart).ToList() ?? plan.Weeks;
            return new Booking(request.Id, plan.Employee.Id, request.WbsElementId, plan.LaborCategoryId, plan.From, plan.To, weeks.Sum(w => w.Hours), weeks,
                Changed: true, IsNew: isNew, Request: request, ExactWeeks: exact);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{name}: {ex.Message}");
            return null;
        }
    }

    // ---- The plan with its people, hours, cost and totals ----

    private sealed record Snapshot(IReadOnlyList<PlanTaskRowDto> Tasks, IReadOnlyList<PlanPersonDto> NotOnATask, PlanTotalsDto Totals, IReadOnlyList<string> Notes);

    private async Task<Snapshot> SnapshotAsync(IWorkbenchDbContext db, Model model, IReadOnlyList<Booking> bookings, IReadOnlySet<Guid> movedItems, CancellationToken ct)
    {
        var notes = new List<string>();
        var employeeIds = bookings.Select(b => b.EmployeeId).Distinct().ToList();
        var load = await LoadByWeekAsync(db, model, bookings, employeeIds, ct);

        var lastWeek = bookings.SelectMany(b => b.Weeks).Select(w => w.WeekStart).DefaultIfEmpty(ThisWeek).Max();
        var capacity = await CapacityAsync(employeeIds.Select(id => model.People[id]).ToList(), ThisWeek, Max(lastWeek, ThisWeek), ct);
        var missingRate = 0m;
        var unrated = 0m;

        PlanPersonDto Person(Booking b)
        {
            var direct = 0m;
            foreach (var w in b.Weeks)
            {
                var rate = model.Rates.Find(b.EmployeeId, w.WeekStart);
                if (rate is null)
                {
                    missingRate += w.Hours;
                }

                direct += w.Hours * (rate ?? 0);
            }

            var over = b.Weeks
                .Where(w => w.WeekStart >= ThisWeek && w.Hours > 0)
                .Select(w =>
                {
                    var total = load.GetValueOrDefault((b.EmployeeId, w.WeekStart));
                    return new AssignmentOverbookedWeekDto(w.WeekStart, w.Hours, total - w.Hours, capacity.GetValueOrDefault((b.EmployeeId, w.WeekStart)));
                })
                .Where(w => CapacityCalculator.IsOverbooked(w.ThisAssignment + w.OtherHours, w.Capacity))
                .ToList();
            var category = model.CategoryOf(b.LaborCategoryId, b.EmployeeId);
            return new PlanPersonDto(b.AssignmentId, b.EmployeeId, model.People.GetValueOrDefault(b.EmployeeId)?.DisplayName ?? "?",
                category, category is { } c && model.Categories.TryGetValue(c, out var cat) ? ProjectStaffingService.Label(cat) : null,
                b.From, b.To, b.Hours, Money.Round(direct), model.Multiplier is { } m ? Money.Round(direct * m) : null, over, b.Changed, b.Weeks, Problems(model, b));
        }

        // Someone assigned to a task with no hours on it yet: on the task's dates, for the PM to give hours.
        PlanPersonDto NoHours(Guid employeeId, Item task)
        {
            var category = model.CategoryOf(null, employeeId);
            var start = task.Start ?? task.Finish ?? Today;
            return new PlanPersonDto(null, employeeId, model.People.GetValueOrDefault(employeeId)?.DisplayName ?? "?",
                category, category is { } c && model.Categories.TryGetValue(c, out var cat) ? ProjectStaffingService.Label(cat) : null,
                start, task.Finish ?? start, 0m, 0m, model.Multiplier is null ? null : 0m, [], NoHoursYet: true);
        }

        var people = bookings.Select(b => (Booking: b, Person: Person(b))).ToList();
        var priced = people.ToDictionary(p => p.Booking, p => Price(model, p.Booking, p.Person, ref unrated));
        var rows = new List<PlanTaskRowDto>();
        var onTasks = new HashSet<Booking>();
        if (model.Plan is { } plan)
        {
            var outline = new PlanOutline(plan.Items);
            var slack = Float(plan);
            var own = new Dictionary<Guid, List<PlanPersonDto>>();
            foreach (var item in outline.Ordered)
            {
                var wbs = model.WbsOf(item.Id);
                var mine = wbs is null ? [] : people.Where(p => p.Booking.WbsElementId == wbs).ToList();
                onTasks.UnionWith(mine.Select(p => p.Booking));
                var listed = mine.Select(p => p.Person).ToList();
                if (wbs is not null && model.Assignees.TryGetValue(item.Id, out var assigned))
                {
                    listed.AddRange(assigned.Where(e => mine.All(p => p.Booking.EmployeeId != e)).Select(e => NoHours(e, item)));
                }

                own[item.Id] = listed.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            // Hours and cost roll up from subtasks to summaries, bottom up.
            var sums = new Dictionary<Guid, (decimal Hours, decimal Direct, decimal Burdened, bool Changed)>();
            foreach (var item in outline.Ordered.Reverse())
            {
                var mine = own[item.Id];
                var s = (mine.Sum(p => p.Hours), mine.Sum(p => p.DirectCost ?? 0), mine.Sum(p => p.BurdenedCost ?? 0), mine.Any(p => p.Changed));
                foreach (var child in outline.ChildrenOf(item.Id))
                {
                    var c = sums[child.Id];
                    s = (s.Item1 + c.Hours, s.Item2 + c.Direct, s.Item3 + c.Burdened, s.Item4 || c.Changed);
                }

                sums[item.Id] = s;
            }

            foreach (var item in outline.Ordered)
            {
                var wbs = model.WbsOf(item.Id);
                var s = sums[item.Id];
                rows.Add(new PlanTaskRowDto(item.Id, outline.OutlineNumber.GetValueOrDefault(item.Id) ?? "", item.Title, outline.Level.GetValueOrDefault(item.Id),
                    outline.IsSummary(item.Id), item.IsMilestone, wbs, wbs is { } w ? model.Wbs[w].Code : null, item.Start, item.Finish,
                    slack.TryGetValue(item.Id, out var f) ? f : null, own[item.Id], s.Hours, s.Direct, model.Multiplier is null ? null : s.Burdened,
                    movedItems.Contains(item.Id) || own[item.Id].Any(p => p.Changed)));
            }
        }

        var notOnTask = people.Where(p => !onTasks.Contains(p.Booking)).Select(p => p.Person)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (missingRate > 0)
        {
            notes.Add($"{H(missingRate)} planned hours have no cost rate, so they're costed at $0.");
        }

        var all = people.Select(p => p.Person).ToList();
        var totals = Totals(model, all, rows) with { Money = MoneyAgainstCeiling(model, people.Select(p => p.Booking).ToList(), priced, unrated) };
        return new Snapshot(rows, notOnTask, totals, notes);
    }

    private PlanTotalsDto Totals(Model model, IReadOnlyList<PlanPersonDto> people, IReadOnlyList<PlanTaskRowDto> rows)
    {
        string Label(Guid? id) => id is { } c && model.Categories.TryGetValue(c, out var cat) ? ProjectStaffingService.Label(cat) : "No labor category";
        var keys = people.Select(p => p.LaborCategoryId).Concat(model.LaborBudget.Select(l => l.LaborCategoryId)).Distinct().ToList();
        var burdened = model.Multiplier is not null;
        var categories = keys
            .Select(k =>
            {
                var mine = people.Where(p => p.LaborCategoryId == k).ToList();
                var budget = model.LaborBudget.Where(l => l.LaborCategoryId == k).ToList();
                return new PlanCategoryTotalDto(k, Label(k), mine.Sum(p => p.Hours), mine.Sum(p => p.DirectCost ?? 0),
                    burdened ? mine.Sum(p => p.BurdenedCost ?? 0) : null, budget.Sum(l => l.Hours), budget.Sum(l => l.Amount));
            })
            .OrderBy(c => c.LaborCategoryId is null ? 1 : 0).ThenBy(c => c.LaborCategory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var leaves = rows.Where(r => !r.IsSummary).ToList();
        var dated = rows.Where(r => (r.Start ?? r.Finish) is not null).ToList();
        var open = leaves.Where(r => r.TotalFloatDays is not null && (r.Finish ?? r.Start) >= Today).ToList();
        var floats = (open.Count > 0 ? open : leaves.Where(r => r.TotalFloatDays is not null).ToList()).Select(r => r.TotalFloatDays!.Value).ToList();
        return new PlanTotalsDto(
            people.Sum(p => p.Hours), people.Sum(p => p.DirectCost ?? 0), burdened ? people.Sum(p => p.BurdenedCost ?? 0) : null,
            model.BudgetName, model.LaborBudget.Sum(l => l.Hours), model.LaborBudget.Sum(l => l.Amount), categories,
            dated.Count == 0 ? null : dated.Min(r => (r.Start ?? r.Finish)!.Value),
            dated.Count == 0 ? null : dated.Max(r => (r.Finish ?? r.Start)!.Value),
            floats.Count == 0 ? null : floats.Min(),
            people.Where(p => p.IsOverbooked).Select(p => p.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// A booking priced as the contract bills it: T&amp;M and labor-hour hours at the contract's bill rate for the labor
    /// category in each week; otherwise (and for hours without a bill rate) cost (burdened when there are indirect rates)
    /// plus the fee. Hours with no bill rate are added to <paramref name="unrated"/>.
    /// </summary>
    private static decimal Price(Model model, Booking booking, PlanPersonDto person, ref decimal unrated)
    {
        var terms = model.Terms.Terms;
        var cost = person.BurdenedCost ?? person.DirectCost ?? 0m;
        var markup = 1m + terms.FeeRate;
        if (!terms.IsBilledByRate || booking.Hours == 0)
        {
            return Money.Round(cost * markup);
        }

        var price = 0m;
        var costPerHour = cost / booking.Hours;
        foreach (var w in booking.Weeks)
        {
            if (person.LaborCategoryId is { } category && model.BillRates.Find(category, w.WeekStart) is { } rate)
            {
                price += w.Hours * rate;
            }
            else
            {
                unrated += w.Hours;
                price += w.Hours * costPerHour * markup;
            }
        }

        return Money.Round(price);
    }

    /// <summary>The plan's dollars against the project's and each top WBS element's (CLIN's) ceiling and funding.</summary>
    private static PlanMoneyDto MoneyAgainstCeiling(Model model, IReadOnlyList<Booking> bookings, IReadOnlyDictionary<Booking, decimal> priced, decimal unrated)
    {
        var terms = model.Terms.Terms;
        static PlanMoneyStatus Status(decimal planned, decimal? ceiling, decimal? funded) =>
            ceiling is { } c && planned > c + 0.005m ? PlanMoneyStatus.OverCeiling
            : funded is { } f && planned > f + 0.005m ? PlanMoneyStatus.OverFunding
            : PlanMoneyStatus.Ok;

        var total = bookings.Sum(b => priced[b]);
        var project = new PlanMoneyLineDto(null, model.Project.Code, null, total, terms.Ceiling, terms.Funded, Status(total, terms.Ceiling, terms.Funded));
        var lines = new List<PlanMoneyLineDto>();
        if (model.Plan is { } plan && model.Outline is { } outline)
        {
            foreach (var top in outline.ChildrenOf(null))
            {
                var ids = outline.Descendants(top.Id).Select(d => model.WbsOf(d.Id)).Append(model.WbsOf(top.Id)).OfType<Guid>().ToHashSet();
                var planned = bookings.Where(b => b.WbsElementId is { } w && ids.Contains(w)).Sum(b => priced[b]);
                var clin = model.WbsOf(top.Id) is { } wbs ? model.Terms.ClinOfTopElement.GetValueOrDefault(wbs) : null;
                lines.Add(new PlanMoneyLineDto(model.WbsOf(top.Id), $"{top.CostpointWbsId} {top.Title}".Trim(), clin?.Number, planned,
                    clin?.Ceiling, clin?.Funded, clin is null ? PlanMoneyStatus.Ok : Status(planned, clin.Ceiling, clin.Funded)));
            }
        }

        var basis = terms.IsBilledByRate
            ? "hours at the contract's bill rates"
            : (model.Multiplier is null ? "labor cost" : "labor cost with fringe, overhead and G&A")
                + (terms.FeeRate > 0 ? $" plus {(terms.FeeRate * 100).ToString("0.##", CultureInfo.InvariantCulture)}% fee" : "");
        var notes = new List<string>();
        if (unrated > 0)
        {
            notes.Add($"{H(unrated)} hours are in labor categories with no bill rate on the contract, so they're priced at cost plus fee.");
        }

        if (terms.Ceiling is null)
        {
            notes.Add(terms.ContractId is null ? "There's no contract or ceiling yet. Finance can set one under Setup." : "The contract has no ceiling yet.");
        }

        if (terms.SharedWith.Count > 0 && (terms.CeilingFromContract || terms.FundedFromContract))
        {
            notes.Add($"The contract's ceiling and funding are shared with {string.Join(", ", terms.SharedWith)}; this compares only this project's plan.");
        }

        return new PlanMoneyDto(project, lines, basis, notes);
    }

    /// <summary>Total float of the plan's dated leaf tasks, as the critical path and the grid's float columns work it out.</summary>
    private static Dictionary<Guid, int> Float(PlanState plan)
    {
        var parents = plan.Items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
        var tasks = plan.Items
            .Where(i => !parents.Contains(i.Id) && (i.Start ?? i.Finish) is not null)
            .Select(i => new ScheduleTask(i.Id, (i.Start ?? i.Finish)!.Value, (i.Finish ?? i.Start)!.Value, i.IsMilestone))
            .ToList();
        var links = plan.Links.Select(l => new ScheduleLink(l.PredecessorId, l.SuccessorId, l.Type, l.LagDays)).ToList();
        return CriticalPathCalculator.Analyze(tasks, links, plan.Calendar, plan.Constraints).Items.ToDictionary(t => t.ItemId, t => t.SlackDays);
    }

    // ---- Load and capacity ----

    /// <summary>
    /// Everything each person is booked for from this week on, by week: <paramref name="bookings"/> (this project as it
    /// would be) plus their other projects. This project's saved assignments count only through the bookings, so one left
    /// out of them (a fix looking at "everything else") isn't counted.
    /// </summary>
    private async Task<Dictionary<(Guid, DateOnly), decimal>> LoadByWeekAsync(
        IWorkbenchDbContext db, Model model, IReadOnlyCollection<Booking> bookings, IReadOnlyList<Guid> employeeIds, CancellationToken ct)
    {
        var thisWeek = ThisWeek;
        var ids = employeeIds.ToList();
        var here = model.Assignments.Select(a => a.Id).ToList();
        var rows = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId) && a.WeekStart >= thisWeek && (a.AssignmentId == null || !here.Contains(a.AssignmentId.Value)))
            .ToListAsync(ct);

        // Week-by-week rows on this project are assignments here, so they count only through the bookings too.
        var load = rows.Where(r => !(r.AssignmentId is null && r.ProjectId == model.Project.Id && r.Hours > 0))
            .GroupBy(r => (r.EmployeeId, r.WeekStart)).ToDictionary(g => g.Key, g => g.Sum(r => r.Hours));
        foreach (var b in bookings.Where(b => ids.Contains(b.EmployeeId)))
        {
            foreach (var w in b.Weeks.Where(w => w.WeekStart >= thisWeek))
            {
                load[(b.EmployeeId, w.WeekStart)] = load.GetValueOrDefault((b.EmployeeId, w.WeekStart)) + w.Hours;
            }
        }

        return load;
    }

    private async Task<Dictionary<(Guid, DateOnly), decimal>> CapacityAsync(IReadOnlyList<Employee> people, DateOnly firstWeek, DateOnly lastWeek, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, DateOnly), decimal>();
        if (people.Count == 0)
        {
            return result;
        }

        var ids = people.Select(p => p.Id).ToList();
        var weeks = CapacityCalculator.Weeks(firstWeek, lastWeek);
        var holidays = await leaveCalendar.GetHolidaysAsync(firstWeek, lastWeek.AddDays(6), ids, ct);
        var leave = await leaveCalendar.GetApprovedLeaveAsync(firstWeek, lastWeek.AddDays(6), ids, ct);
        foreach (var p in people)
        {
            // Booked past their working hours (standard less holidays and leave), as when assigning; no non-billable allowance.
            foreach (var week in CapacityCalculator.Compute(new CapacityPerson(p.Id, p.StandardHoursPerWeek, p.HireDate, p.TerminationDate), weeks, holidays, leave, 0m))
            {
                result[(p.Id, week.WeekStart)] = week.Capacity;
            }
        }

        return result;
    }

    private List<DateOnly> OverWeeks(IReadOnlyList<AssignmentWeekDto> weeks, Guid employeeId, IReadOnlyDictionary<(Guid, DateOnly), decimal> others, IReadOnlyDictionary<(Guid, DateOnly), decimal> capacity) =>
        weeks.Where(w => w.WeekStart >= ThisWeek && w.Hours > 0
                && CapacityCalculator.IsOverbooked(w.Hours + others.GetValueOrDefault((employeeId, w.WeekStart)), capacity.GetValueOrDefault((employeeId, w.WeekStart))))
            .Select(w => w.WeekStart).ToList();

    /// <summary>The weekly spread a save would make over these dates (no weeks already past are kept), or null when there are no working days.</summary>
    private IReadOnlyList<AssignmentWeekDto>? Spread(Model model, Employee person, decimal hours, DateOnly from, DateOnly to)
    {
        try
        {
            return AssignmentSpread.Spread(hours, Max(from, Today), Max(to, Max(from, Today)), d => model.Calendar.IsWorkingDay(d) && person.IsActiveOn(d));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// The change that puts an assignment back as it is now, its weeks exactly. A removed one (<paramref name="recreate"/>)
    /// comes back as a new assignment on the same task or charge code.
    /// </summary>
    private static PlanChange PutBack(StaffingAssignment a, IReadOnlyList<AssignmentWeekDto> weeks, bool recreate, Guid? itemId) =>
        new(PlanChangeKind.SetPerson, ItemId: recreate ? itemId : null, AssignmentId: recreate ? null : a.Id,
            EmployeeId: a.EmployeeId, Hours: a.TotalHours, LaborCategoryId: a.LaborCategoryId, From: a.From, To: a.To,
            Weeks: weeks.Count > 0 ? weeks : null, WbsElementId: recreate ? a.WbsElementId : null);

    private static void Limit(IReadOnlyList<PlanChange> changes)
    {
        if (changes.Count > 500)
        {
            throw new ArgumentException("Try up to 500 changes at a time.");
        }
    }

    /// <summary>Moves a date by working days on the plan calendar (a day off counts from the next working day, or the previous one going backwards).</summary>
    private static DateOnly Shift(Model model, DateOnly date, int days, bool backwards = false) =>
        days == 0 ? date : model.Calendar.AddWorkingDays(backwards ? model.Calendar.OnOrBefore(date) : model.Calendar.OnOrAfter(date), days);

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string H(decimal hours) => hours.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string Weeks(int days) => Math.Max(1, (days + 6) / 7) is var w && w == 1 ? "1 week" : $"{w} weeks";

    private static string Days(int days) => Math.Abs(days) == 1 ? "1 working day" : $"{Math.Abs(days)} working days";

    /// <summary>
    /// What doesn't make sense about someone's hours on their task: on a summary row (people go on its tasks), dates outside
    /// the task's, or more hours than they can work in their dates. Null when nothing's wrong or they're not on a plan task.
    /// </summary>
    private static List<string>? Problems(Model model, Booking b)
    {
        if (model.ItemOf(b.WbsElementId) is not { } taskId || model.Plan is null || model.Outline is null)
        {
            return null;
        }

        var task = model.Plan.Items.First(i => i.Id == taskId);
        var person = model.People.GetValueOrDefault(b.EmployeeId);
        var name = person?.DisplayName ?? "They";
        var problems = new List<string>();
        if (model.Outline.IsSummary(taskId))
        {
            problems.Add($"{task.Title} is a summary row: move {name} to one of its tasks.");
        }
        else if (task.Start is { } s && task.Finish is { } f && (b.From < s || b.To > f))
        {
            problems.Add($"Their dates ({Range(b.From, b.To)}) run outside the task ({Range(s, f)}).");
        }

        if (person is not null && b.Hours > 0)
        {
            var days = Enumerable.Range(0, b.To.DayNumber - b.From.DayNumber + 1).Select(b.From.AddDays).Count(d => model.Calendar.IsWorkingDay(d) && person.IsActiveOn(d));
            var perDay = person.StandardHoursPerWeek / 5m;
            if (days == 0)
            {
                problems.Add($"There are no working days between {Range(b.From, b.To)} for {Hours(b.Hours)} h.");
            }
            else if (b.Hours > days * perDay + 0.01m)
            {
                problems.Add($"{Hours(b.Hours)} h in {days} working day{(days == 1 ? "" : "s")} is {Hours(Math.Round(b.Hours / days, 1))} h a day; {name} works {Hours(perDay)} h a day.");
            }
        }

        return problems.Count == 0 ? null : problems;
    }

    private static string Hours(decimal hours) => hours.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string Range(DateOnly? start, DateOnly? finish) =>
        $"{start?.ToString("MMM d", CultureInfo.InvariantCulture) ?? "?"} – {finish?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "?"}";

    private static string Describe(int tasks, int added, int changed, int removed)
    {
        static string People(int n) => n == 1 ? "1 person" : $"{n} people";
        var parts = new List<string>();
        if (added > 0)
        {
            parts.Add($"assigned {People(added)}");
        }

        if (changed > 0)
        {
            parts.Add($"changed hours or dates for {People(changed)}");
        }

        if (removed > 0)
        {
            parts.Add($"took {People(removed)} off");
        }

        if (tasks > 0)
        {
            parts.Add(tasks == 1 ? "moved 1 task" : $"moved {tasks} tasks");
        }

        var text = parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return text.Length == 0 ? "Nothing needed changing." : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }
}
