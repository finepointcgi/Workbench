using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Costpoint;
using Workbench.Domain;
using Workbench.Domain.Costpoint;

namespace Workbench.Application.Costpoint;

public sealed class CostpointService(IWorkbenchDbContextFactory dbFactory, CostpointSyncService sync) : ICostpointService
{
    private const int MaxTake = 200;

    public async Task<CostpointStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var lastRun = await db.Set<CpSyncRun>().AsNoTracking().OrderByDescending(r => r.StartedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return new CostpointStatusDto(
            sync.SourceName,
            sync.Configured,
            lastRun?.ToDto(),
            await db.Set<CpProject>().CountAsync(cancellationToken),
            await db.Set<CpWbsElement>().CountAsync(cancellationToken),
            await db.Set<CpEmployee>().CountAsync(cancellationToken),
            await db.Set<CpActual>().CountAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<CostpointSyncRunDto>> ListSyncRunsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var runs = await db.Set<CpSyncRun>()
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(Math.Clamp(take, 1, MaxTake))
            .ToListAsync(cancellationToken);
        return runs.Select(r => r.ToDto()).ToList();
    }

    public async Task<CostpointSyncRunDto> SyncNowAsync(CancellationToken cancellationToken = default) =>
        (await sync.RunAsync(cancellationToken)).ToDto();

    public async Task<IReadOnlyList<CostpointProjectDto>> SearchProjectsAsync(string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var projects = db.Set<CpProject>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLower();
            projects = projects.Where(p => p.ProjectId.ToLower().Contains(q) || p.Name.ToLower().Contains(q));
        }

        return await projects
            .OrderBy(p => p.ProjectId)
            .Take(Math.Clamp(take, 1, MaxTake))
            .Select(p => new CostpointProjectDto(p.ProjectId, p.Name, p.OrganizationId, p.Start, p.End, p.Active))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CostpointWbsDto>> SearchWbsAsync(string? projectId, string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wbs = db.Set<CpWbsElement>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var project = projectId.Trim();
            wbs = wbs.Where(w => w.ProjectId == project);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLower();
            wbs = wbs.Where(w => w.WbsId.ToLower().Contains(q) || w.Name.ToLower().Contains(q));
        }

        return await wbs
            .OrderBy(w => w.ProjectId)
            .ThenBy(w => w.WbsId)
            .Take(Math.Clamp(take, 1, MaxTake))
            .Select(w => new CostpointWbsDto(w.ProjectId, w.WbsId, w.ParentWbsId, w.Name, w.Level, w.ChargeAllowed))
            .ToListAsync(cancellationToken);
    }

    public async Task<PlanVsActualDto> GetPlanVsActualAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var lastSynced = await db.Set<CpSyncRun>()
            .AsNoTracking()
            .Where(r => r.Status == CpSyncStatus.Succeeded)
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(r => r.FinishedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (sheet.CostpointProjectId is not { } projectId)
        {
            var empty = PlanVsActualCalculator.Compute(columns, [], [], [], []);
            return new PlanVsActualDto(sheetId, null, null, [], empty.Totals, lastSynced, ["This sheet isn't linked to a Costpoint project."]);
        }

        var project = await db.Set<CpProject>().AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId, cancellationToken);
        var wbs = await db.Set<CpWbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(cancellationToken);
        var budgets = await db.Set<CpBudget>().AsNoTracking().Where(b => b.ProjectId == projectId).ToListAsync(cancellationToken);
        var actuals = await db.Set<CpActual>().AsNoTracking().Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);

        var (rows, totals, notes) = PlanVsActualCalculator.Compute(columns, items, wbs, budgets, actuals);
        if (project is null)
        {
            notes = [$"Costpoint project {projectId} isn't in the mirror yet. Run a Costpoint sync.", .. notes];
        }

        return new PlanVsActualDto(sheetId, projectId, project?.Name, rows, totals, lastSynced, notes);
    }
}

internal static class CostpointMapping
{
    public static CostpointSyncRunDto ToDto(this CpSyncRun r) =>
        new(r.Id, r.Source, r.StartedAtUtc, r.FinishedAtUtc, (CostpointSyncStatus)(int)r.Status,
            r.Projects, r.WbsElements, r.Employees, r.LaborCategories, r.Budgets, r.Actuals,
            r.Inserted, r.Updated, r.Deleted, r.Skipped, r.Error);
}
