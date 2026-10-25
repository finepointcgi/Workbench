using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tasks;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>A scheduled sheet's tracked items with what the engine needs: links, the working calendar, constraints and the % column.</summary>
internal sealed record PlanState(
    Sheet Sheet, List<Item> Items, List<PlanLink> Links, WorkCalendar Calendar, Column? PercentColumn, Dictionary<Guid, PlanConstraintDto> Constraints);

/// <summary>
/// Runs <see cref="ScheduleEngine.Reflow"/> on a sheet after its items or links change, so summaries span their subtasks and
/// successors start no earlier than their links allow. Only sheets with scheduling on are touched.
/// </summary>
internal static class PlanReflow
{
    public static async Task<PlanState> LoadAsync(IWorkbenchDbContext db, Sheet sheet, CancellationToken cancellationToken, bool tracked = true)
    {
        var query = db.Items.Where(i => i.SheetId == sheet.Id);
        var items = await (tracked ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
        var ids = items.Select(i => i.Id).ToHashSet();
        var links = (await db.Set<Dependency>().AsNoTracking().Where(d => d.SheetId == sheet.Id).ToListAsync(cancellationToken))
            .Where(d => ids.Contains(d.PredecessorId) && ids.Contains(d.SuccessorId))
            .Select(d => new PlanLink(d.PredecessorId, d.SuccessorId, (DependencyKind)(int)d.Type, d.LagDays))
            .ToList();
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheet.Id).ToListAsync(cancellationToken);
        return new PlanState(sheet, items, links, await CalendarAsync(db, sheet.Id, cancellationToken), TaskRules.PercentColumn(columns),
            await ConstraintsAsync(db, sheet.Id, ids, cancellationToken));
    }

    /// <summary>The company calendar: Monday to Friday less company holidays.</summary>
    public static async Task<WorkCalendar> CalendarAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        new(await db.Set<Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(cancellationToken));

    /// <summary>The sheet's own calendar when it has one, else the company calendar.</summary>
    public static async Task<WorkCalendar> CalendarAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        var own = await db.Set<PlanCalendar>().AsNoTracking().Include(c => c.Exceptions).FirstOrDefaultAsync(c => c.SheetId == sheetId, cancellationToken);
        if (own is null)
        {
            return await CalendarAsync(db, cancellationToken);
        }

        var holidays = own.UseCompanyHolidays ? await db.Set<Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(cancellationToken) : [];
        return new WorkCalendar(
            holidays.Concat(own.Exceptions.Where(e => !e.IsWorking).Select(e => e.Date)),
            own.WorkDays,
            own.Exceptions.Where(e => e.IsWorking).Select(e => e.Date));
    }

    /// <summary>The sheet's task constraints by item id (for items that still exist).</summary>
    public static async Task<Dictionary<Guid, PlanConstraintDto>> ConstraintsAsync(
        IWorkbenchDbContext db, Guid sheetId, IReadOnlySet<Guid> itemIds, CancellationToken cancellationToken) =>
        (await db.Set<TaskConstraint>().AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken))
            .Where(c => itemIds.Contains(c.ItemId))
            .ToDictionary(c => c.ItemId, c => new PlanConstraintDto((PlanConstraintKind)(int)c.Type, c.Date));

    /// <summary>
    /// Reflows the sheet's tracked items in place and numbers a project plan's WBS codes; on a project's plan its people
    /// follow their tasks and are their assignees (<see cref="PlanPeopleSync"/>). True when anything changed (the caller saves).
    /// </summary>
    public static async Task<bool> ReflowAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        var sheet = await db.Sheets.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken);
        if (sheet is null)
        {
            return false;
        }

        var project = await ProjectForAsync(db, sheetId, cancellationToken);
        if (!sheet.SchedulingEnabled && project is null)
        {
            return false;
        }

        var state = await LoadAsync(db, sheet, cancellationToken);
        var changed = sheet.SchedulingEnabled && ScheduleEngine.Reflow(state.Items, state.Links, state.Calendar, state.PercentColumn?.Id, state.Constraints);
        changed |= await NumberAsync(db, project, state.Items, cancellationToken);

        // On a project's plan the rows are the WBS: keep the charge codes in step with them.
        if (project is not null)
        {
            changed |= await PlanWbsSync.ApplyAsync(db, project, state.Items, cancellationToken);
            changed |= await PlanPeopleSync.FollowTasksAsync(db, project, state.Items, state.Calendar, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime, cancellationToken);
            // Read from what's saved: a charge code made in this same change has nobody on it yet.
            changed |= (await PlanPeopleSync.SyncAssigneesAsync(db, project.Id, cancellationToken)).Items.Count > 0;
        }

        return changed;
    }

    /// <summary>Numbers the WBS codes of a project's plan (see <see cref="WbsNumbering"/>). Sheets that aren't a project's plan are left alone.</summary>
    public static async Task<bool> NumberAsync(IWorkbenchDbContext db, Project? project, IReadOnlyCollection<Item> items, CancellationToken cancellationToken)
    {
        if (project is null)
        {
            return false;
        }

        // Codes follow the outline until time is posted to them; then they're kept and numbering works around them.
        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == project.Id)
            .Select(w => new { w.Id, w.Code, w.ItemId }).ToListAsync(cancellationToken);
        var posted = await PlanWbsSync.PostedAsync(db, elements.Select(e => e.Id).ToList(), cancellationToken);
        var itemIds = items.Select(i => i.Id).ToHashSet();
        var locked = elements.Where(e => e.ItemId is { } i && posted.Contains(e.Id)).Select(e => e.ItemId!.Value).ToHashSet();
        var taken = elements.Where(e => e.ItemId is not { } i || !itemIds.Contains(i) || locked.Contains(i)).Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        return WbsNumbering.Apply(new PlanOutline(items), project.Code, locked, taken);
    }

    /// <summary>The project whose plan this sheet is, if any.</summary>
    public static Task<Project?> ProjectForAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.SheetId == sheetId, cancellationToken);

    /// <summary>Reflows and saves when anything moved.</summary>
    public static async Task ReflowAndSaveAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        if (await ReflowAsync(db, sheetId, cancellationToken, clock))
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
