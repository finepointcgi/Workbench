using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tasks;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>A scheduled sheet's tracked items with what the engine needs: links, the working calendar and the % column.</summary>
internal sealed record PlanState(Sheet Sheet, List<Item> Items, List<PlanLink> Links, WorkCalendar Calendar, Column? PercentColumn);

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
        return new PlanState(sheet, items, links, await CalendarAsync(db, cancellationToken), TaskRules.PercentColumn(columns));
    }

    public static async Task<WorkCalendar> CalendarAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        new(await db.Set<Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(cancellationToken));

    /// <summary>Reflows the sheet's tracked items in place and numbers a project plan's WBS codes. True when anything changed (the caller saves).</summary>
    public static async Task<bool> ReflowAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
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
        var changed = sheet.SchedulingEnabled && ScheduleEngine.Reflow(state.Items, state.Links, state.Calendar, state.PercentColumn?.Id);
        return await NumberAsync(db, project, state.Items, cancellationToken) | changed;
    }

    /// <summary>Numbers the WBS codes of a project's plan (see <see cref="WbsNumbering"/>). Sheets that aren't a project's plan are left alone.</summary>
    public static async Task<bool> NumberAsync(IWorkbenchDbContext db, Project? project, IReadOnlyCollection<Item> items, CancellationToken cancellationToken)
    {
        if (project is null)
        {
            return false;
        }

        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == project.Id)
            .Select(w => new { w.Code, w.ItemId }).ToListAsync(cancellationToken);
        var locked = elements.Where(e => e.ItemId is not null).Select(e => e.ItemId!.Value).ToHashSet();
        var taken = elements.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        return WbsNumbering.Apply(new PlanOutline(items), project.Code, locked, taken);
    }

    /// <summary>The project whose plan this sheet is, if any.</summary>
    public static Task<Project?> ProjectForAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.SheetId == sheetId, cancellationToken);

    /// <summary>Reflows and saves when anything moved.</summary>
    public static async Task ReflowAndSaveAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        if (await ReflowAsync(db, sheetId, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
