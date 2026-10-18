using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;

namespace Workbench.Application.Evm;

/// <summary>PMB reads shared by the EVM services.</summary>
public static class EvmBaselines
{
    public static async Task<bool> HasClosedPeriodsAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Set<EvmStatusPeriod>().AnyAsync(s => s.ProjectId == projectId && s.ReopenedAtUtc == null, ct);

    public static async Task<EvmBaselineDto?> ToDtoAsync(IWorkbenchDbContext db, Guid projectId, DateOnly today, IReadOnlyList<string> notes, CancellationToken ct)
    {
        var baseline = await db.Set<EvmBaseline>().AsNoTracking().FirstOrDefaultAsync(b => b.ProjectId == projectId, ct);
        if (baseline is null)
        {
            return null;
        }

        var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var wps = await db.Set<EvmWorkPackage>().AsNoTracking().Include(w => w.Milestones).Where(w => w.BaselineId == baseline.Id).ToListAsync(ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, ct);
        var versionName = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.Id == baseline.BudgetVersionId).Select(v => v.Name).FirstOrDefaultAsync(ct) ?? "?";
        var scheduleName = baseline.ScheduleBaselineId is { } sid
            ? await db.Set<Baseline>().AsNoTracking().Where(b => b.Id == sid).Select(b => b.Name).FirstOrDefaultAsync(ct)
            : null;
        var plan = await EvmEngine.LoadPlanAsync(db, project, ct);

        string Code(Guid id) => wbs.TryGetValue(id, out var e) ? e.Code : "?";
        string Name(Guid id) => wbs.TryGetValue(id, out var e) ? e.Name : "(removed)";

        var packages = wps.OrderBy(w => Code(w.WbsElementId), StringComparer.Ordinal).Select(w => new EvmWorkPackageDto(
            w.Id, w.WbsElementId, Code(w.WbsElementId), Name(w.WbsElementId), w.ControlAccountWbsId, Code(w.ControlAccountWbsId),
            (EvTechnique)(int)w.Technique, w.Budget, w.PlannedStart, w.PlannedFinish, w.ApportionedToId, w.PercentCompleteOverride,
            wbs.GetValueOrDefault(w.WbsElementId)?.ItemId,
            w.Milestones.OrderBy(m => m.Order).Select(m => new EvmMilestoneDto(m.Id, m.Name, m.Weight, m.Order, m.ItemId, m.PlannedDate, m.CompletedOn,
                m.CompletedOn is { } on ? on <= today : m.ItemId is { } i && plan.IsDone(i))).ToList())).ToList();
        var accounts = wps.GroupBy(w => w.ControlAccountWbsId).OrderBy(g => Code(g.Key), StringComparer.Ordinal)
            .Select(g => new EvmControlAccountDto(g.Key, Code(g.Key), Name(g.Key), g.Sum(w => w.Budget), g.Count())).ToList();
        var distributed = wps.Sum(w => w.Budget);
        var pmb = distributed + baseline.UndistributedBudget;
        return new EvmBaselineDto(baseline.Id, projectId, project.Code, baseline.BudgetVersionId, versionName, baseline.ScheduleBaselineId, scheduleName,
            baseline.ControlAccountLevel, baseline.Revision, distributed, baseline.UndistributedBudget, pmb, baseline.ManagementReserve,
            pmb + baseline.ManagementReserve, baseline.ThresholdPercent, baseline.ThresholdAmount,
            await HasClosedPeriodsAsync(db, projectId, ct), baseline.CreatedAtUtc, baseline.CreatedBy, accounts, packages, notes);
    }

    public static object Describe(EvmWorkPackage wp) => new
    {
        wp.WbsElementId,
        Technique = wp.Technique.ToString(),
        wp.Budget,
        wp.PlannedStart,
        wp.PlannedFinish,
        wp.ApportionedToId,
        Milestones = wp.Milestones.OrderBy(m => m.Order).Select(m => new { m.Name, m.Weight }).ToList(),
    };

    /// <summary>One milestone per plan subtask (equal weights), or a single "complete" milestone on the task itself.</summary>
    public static List<EvmMilestone> MilestonesFromPlan(EvmWorkPackage wp, WbsElement element, EvmPlanProgress plan)
    {
        var children = element.ItemId is { } itemId ? plan.ChildrenOf(itemId) : [];
        if (children.Count == 0)
        {
            return [new EvmMilestone { WorkPackageId = wp.Id, Name = "Work complete", Weight = 100m, ItemId = element.ItemId, PlannedDate = wp.PlannedFinish }];
        }

        var each = Math.Round(100m / children.Count, 2, MidpointRounding.ToZero);
        return children.Select((c, i) => new EvmMilestone
        {
            WorkPackageId = wp.Id,
            Name = c.Title.Length > 200 ? c.Title[..200] : c.Title,
            Weight = i == children.Count - 1 ? 100m - each * (children.Count - 1) : each,
            Order = i,
            ItemId = c.Id,
            PlannedDate = c.Finish,
        }).ToList();
    }
}

/// <summary>Which project an EVM record belongs to (for permission checks).</summary>
public static class EvmLookup
{
    public static async Task<Guid?> ProjectOfWorkPackageAsync(IWorkbenchDbContext db, Guid workPackageId, CancellationToken ct) =>
        await (from w in db.Set<EvmWorkPackage>().AsNoTracking()
               join b in db.Set<EvmBaseline>().AsNoTracking() on w.BaselineId equals b.Id
               where w.Id == workPackageId
               select (Guid?)b.ProjectId).FirstOrDefaultAsync(ct);

    public static async Task<Guid?> ProjectOfMilestoneAsync(IWorkbenchDbContext db, Guid milestoneId, CancellationToken ct)
    {
        var wpId = await db.Set<EvmMilestone>().AsNoTracking().Where(m => m.Id == milestoneId).Select(m => (Guid?)m.WorkPackageId).FirstOrDefaultAsync(ct);
        return wpId is { } id ? await ProjectOfWorkPackageAsync(db, id, ct) : null;
    }

    public static async Task<Guid?> ProjectOfChangeRequestAsync(IWorkbenchDbContext db, Guid changeRequestId, CancellationToken ct) =>
        await db.Set<EvmChangeRequest>().AsNoTracking().Where(c => c.Id == changeRequestId).Select(c => (Guid?)c.ProjectId).FirstOrDefaultAsync(ct);
}
