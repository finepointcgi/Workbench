using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Evm;

/// <summary>Everything the earned value calculations need about one project, loaded once per operation.</summary>
public sealed class EvmProject
{
    public required Project Project { get; init; }
    public string? ContractNumber { get; init; }
    public required EvmBaseline Baseline { get; init; }
    public required List<EvmWorkPackage> WorkPackages { get; init; }
    public required Dictionary<Guid, WbsElement> Wbs { get; init; }

    /// <summary>Every fiscal period, oldest first.</summary>
    public required List<FiscalPeriod> Calendar { get; init; }

    /// <summary>Closed (not reopened) status periods, oldest first, with their snapshots.</summary>
    public required List<EvmStatusPeriod> Closed { get; init; }

    public required List<EvmVarianceAnalysis> Variances { get; init; }
    public required EvmPlanProgress Plan { get; init; }
    public required DateOnly Today { get; init; }

    public EvmStatusPeriod? ClosedFor(Guid fiscalPeriodId) => Closed.FirstOrDefault(c => c.FiscalPeriodId == fiscalPeriodId);
}

/// <summary>One work package's cumulative figures (or actual cost outside every work package) at a status date.</summary>
public sealed record EvRow(
    Guid? WorkPackageId,
    Guid WbsElementId,
    Guid? ControlAccountId,
    EarnedValueTechnique Technique,
    decimal Budget,
    decimal Bcws,
    decimal Bcwp,
    decimal Acwp,
    decimal Percent,
    string Source)
{
    public Guid Key => WorkPackageId ?? WbsElementId;
}

/// <summary>Actual cost per WBS element and how it was costed.</summary>
public sealed record EvCost(IReadOnlyDictionary<Guid, decimal> ByWbs, string Basis)
{
    public static EvCost None { get; } = new(new Dictionary<Guid, decimal>(), "");
}

/// <summary>Loads projects and turns them into status rows, period status and the S-curve.</summary>
public static class EvmEngine
{
    /// <summary>Cost from the beginning of time; costs before any fiscal year simply aren't burdened.</summary>
    private static readonly DateOnly CostFrom = new(2000, 1, 1);

    public static async Task<Project> RequireProjectAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);

    /// <summary>The project's EVM data, or null when it has no PMB.</summary>
    public static async Task<EvmProject?> LoadAsync(IWorkbenchDbContext db, Guid projectId, DateOnly today, CancellationToken ct)
    {
        var project = await RequireProjectAsync(db, projectId, ct);
        var baseline = await db.Set<EvmBaseline>().AsNoTracking().FirstOrDefaultAsync(b => b.ProjectId == projectId, ct);
        if (baseline is null)
        {
            return null;
        }

        var workPackages = await db.Set<EvmWorkPackage>().AsNoTracking().Include(w => w.Milestones)
            .Where(w => w.BaselineId == baseline.Id).ToListAsync(ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, ct);
        var calendar = (await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(ct)).OrderBy(p => p.Start).ToList();
        var closed = (await db.Set<EvmStatusPeriod>().AsNoTracking().Include(s => s.Snapshots)
                .Where(s => s.ProjectId == projectId && s.ReopenedAtUtc == null).ToListAsync(ct))
            .OrderBy(s => s.PeriodStart).ToList();
        var variances = await db.Set<EvmVarianceAnalysis>().AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        var contractNumber = project.ContractId is { } cid
            ? await db.Set<Contract>().AsNoTracking().Where(c => c.Id == cid).Select(c => c.ContractNumber).FirstOrDefaultAsync(ct)
            : null;
        return new EvmProject
        {
            Project = project,
            ContractNumber = contractNumber,
            Baseline = baseline,
            WorkPackages = workPackages.OrderBy(w => wbs.TryGetValue(w.WbsElementId, out var e) ? e.Code : "", StringComparer.Ordinal).ToList(),
            Wbs = wbs,
            Calendar = calendar,
            Closed = closed,
            Variances = variances,
            Plan = await LoadPlanAsync(db, project, ct),
            Today = today,
        };
    }

    public static async Task<EvmPlanProgress> LoadPlanAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        if (project.SheetId is not { } sheetId)
        {
            return EvmPlanProgress.Empty;
        }

        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(ct);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(ct);
        return new EvmPlanProgress(items, columns);
    }

    /// <summary>Burdened (provisional rates) actual cost per WBS element from the start through <paramref name="through"/>.</summary>
    public static async Task<EvCost> CumulativeCostAsync(BillingCostSource costs, IWorkbenchDbContext db, Guid projectId, DateOnly through, CancellationToken ct)
    {
        var result = await costs.GetProjectCostAsync(db, projectId, CostFrom, through, RateBasis.Provisional, ct);
        var byWbs = result.Lines.GroupBy(l => l.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(l => l.TotalCost));
        var basis = costs.HasIndirectRates
            ? "Actual cost (ACWP): approved labor and allowable ODCs burdened at provisional indirect rates."
            : BillingCostSource.NotConfiguredNote;
        return new EvCost(byWbs, basis);
    }

    // ---- WBS structure ----

    /// <summary>The element's ancestors from the top of the WBS down to the element itself.</summary>
    public static List<WbsElement> PathOf(IReadOnlyDictionary<Guid, WbsElement> wbs, Guid elementId)
    {
        var path = new List<WbsElement>();
        var seen = new HashSet<Guid>();
        for (var id = (Guid?)elementId; id is { } current && wbs.TryGetValue(current, out var e) && seen.Add(current); id = e.ParentId)
        {
            path.Add(e);
        }

        path.Reverse();
        return path;
    }

    /// <summary>The control account of an element: its ancestor at <paramref name="level"/>, or itself when it's shallower.</summary>
    public static Guid ControlAccountOf(IReadOnlyDictionary<Guid, WbsElement> wbs, Guid elementId, int level)
    {
        var path = PathOf(wbs, elementId);
        return path.Count >= level ? path[level - 1].Id : elementId;
    }

    public static bool IsLeaf(IReadOnlyDictionary<Guid, WbsElement> wbs, Guid elementId) => !wbs.Values.Any(w => w.ParentId == elementId);

    // ---- Periods ----

    /// <summary>The fiscal periods from the earliest work package start to the latest finish, plus any closed outside them.</summary>
    public static List<FiscalPeriod> Span(EvmProject p)
    {
        if (p.WorkPackages.Count == 0)
        {
            return p.Calendar.Where(c => p.Closed.Any(s => s.FiscalPeriodId == c.Id) || (c.Start <= p.Today && p.Today <= c.End)).ToList();
        }

        var from = p.WorkPackages.Min(w => Min(w.PlannedStart, w.PlannedFinish));
        var to = p.WorkPackages.Max(w => Max(w.PlannedStart, w.PlannedFinish));
        var closedIds = p.Closed.Select(c => c.FiscalPeriodId).ToHashSet();
        return p.Calendar.Where(c => (c.End >= from && c.Start <= to) || closedIds.Contains(c.Id)).ToList();
    }

    public static EvmPeriodState StateOf(EvmProject p, FiscalPeriod period) =>
        p.ClosedFor(period.Id) is not null ? EvmPeriodState.Closed
        : period.Start > p.Today ? EvmPeriodState.Future
        : period.End >= p.Today ? EvmPeriodState.Reporting
        : EvmPeriodState.Unstatused;

    public static EvmPeriodRefDto RefOf(EvmProject p, FiscalPeriod period)
    {
        var closed = p.ClosedFor(period.Id);
        return new EvmPeriodRefDto(period.Id, period.Name, period.Start, period.End, StateOf(p, period), closed?.ClosedAtUtc, closed?.ClosedBy);
    }

    /// <summary>The period to show by default: the one containing today, else the nearest end of the PMB's span.</summary>
    public static FiscalPeriod DefaultPeriod(EvmProject p)
    {
        var span = Span(p);
        return span.FirstOrDefault(c => c.Start <= p.Today && p.Today <= c.End)
            ?? (span.Count > 0 ? (p.Today > span[^1].End ? span[^1] : span[0]) : null)
            ?? p.Calendar.FirstOrDefault(c => c.Start <= p.Today && p.Today <= c.End)
            ?? throw new ArgumentException("No fiscal period covers today; add the fiscal year in Setup.");
    }

    public static FiscalPeriod PeriodOf(EvmProject p, Guid? fiscalPeriodId) =>
        fiscalPeriodId is { } id
            ? p.Calendar.FirstOrDefault(c => c.Id == id) ?? throw WorkbenchNotFoundException.For("Fiscal period", id)
            : DefaultPeriod(p);

    /// <summary>The latest closed status period that ended before <paramref name="period"/> started.</summary>
    public static EvmStatusPeriod? PreviousClosed(EvmProject p, FiscalPeriod period) =>
        p.Closed.LastOrDefault(c => c.PeriodEnd < period.Start);

    // ---- Rows ----

    public static List<EvRow> FrozenRows(EvmStatusPeriod closed) =>
        closed.Snapshots.Select(s => new EvRow(s.WorkPackageId, s.WbsElementId, s.ControlAccountWbsId, s.Technique, s.Budget, s.Bcws, s.Bcwp, s.Acwp,
            s.PercentComplete, "Frozen at close")).ToList();

    /// <summary>BCWS only (no progress or cost), for future and unstatused periods of the S-curve.</summary>
    public static decimal PlannedThrough(EvmProject p, DateOnly asOf) =>
        p.WorkPackages.Sum(w => EvmCalculator.PlannedValue(w.Budget, w.PlannedStart, w.PlannedFinish, asOf));

    /// <summary>Live status rows at the end of <paramref name="period"/>, with progress as it stands today.</summary>
    public static List<EvRow> LiveRows(EvmProject p, FiscalPeriod period, EvCost cost)
    {
        var asOf = period.End;
        var level = p.Baseline.ControlAccountLevel;
        var byWbs = p.WorkPackages.ToDictionary(w => w.WbsElementId);
        var progress = new Dictionary<Guid, (decimal Percent, string Source)>();
        var rows = new List<EvRow>();

        // Actual cost goes to the nearest work package at or above the WBS element it was charged to.
        var acwp = new Dictionary<Guid, decimal>();
        var unassigned = new Dictionary<Guid, decimal>();
        foreach (var (wbsId, amount) in cost.ByWbs)
        {
            var wp = PathOf(p.Wbs, wbsId).AsEnumerable().Reverse().Select(e => byWbs.GetValueOrDefault(e.Id)).FirstOrDefault(w => w is not null);
            if (wp is not null)
            {
                acwp[wp.Id] = acwp.GetValueOrDefault(wp.Id) + amount;
            }
            else
            {
                unassigned[wbsId] = unassigned.GetValueOrDefault(wbsId) + amount;
            }
        }

        foreach (var wp in p.WorkPackages.OrderBy(w => w.Technique == EarnedValueTechnique.Apportioned))
        {
            var bcws = EvmCalculator.PlannedValue(wp.Budget, wp.PlannedStart, wp.PlannedFinish, asOf);
            var (input, source) = ProgressOf(p, wp, asOf, progress);
            var planned = wp.Budget == 0 ? (asOf >= wp.PlannedFinish ? 100m : 0m) : bcws / wp.Budget * 100m;
            var percent = Math.Round(EvmCalculator.EarnedPercent(wp.Technique, input, planned), 2, MidpointRounding.AwayFromZero);
            progress[wp.Id] = (percent, source);
            // Level of effort earns exactly what was planned (no rounding through the %).
            var bcwp = wp.Technique == EarnedValueTechnique.LevelOfEffort ? bcws : EvmCalculator.Earned(wp.Budget, percent);
            rows.Add(new EvRow(wp.Id, wp.WbsElementId, wp.ControlAccountWbsId, wp.Technique, wp.Budget, bcws,
                bcwp, Money(acwp.GetValueOrDefault(wp.Id)), percent, source));
        }

        foreach (var (wbsId, amount) in unassigned.Where(u => u.Value != 0))
        {
            Guid? ca = PathOf(p.Wbs, wbsId).Count >= level ? ControlAccountOf(p.Wbs, wbsId, level) : null;
            rows.Add(new EvRow(null, wbsId, ca, EarnedValueTechnique.LevelOfEffort, 0, 0, 0, Money(amount), 0, "Actual cost outside every work package"));
        }

        return rows;
    }

    private static (EvProgress Input, string Source) ProgressOf(EvmProject p, EvmWorkPackage wp, DateOnly asOf, Dictionary<Guid, (decimal Percent, string Source)> computed)
    {
        switch (wp.Technique)
        {
            case EarnedValueTechnique.LevelOfEffort:
                return (new EvProgress(), "Level of effort (earned as planned)");
            case EarnedValueTechnique.Apportioned:
                var baseWp = p.WorkPackages.FirstOrDefault(w => w.Id == wp.ApportionedToId);
                var basePercent = baseWp is not null && computed.TryGetValue(baseWp.Id, out var b) ? b.Percent : 0m;
                var code = baseWp is not null && p.Wbs.TryGetValue(baseWp.WbsElementId, out var be) ? be.Code : "?";
                return (new EvProgress(ApportionedBasePercent: basePercent), $"Apportioned to {code} ({basePercent:0.##}%)");
            case EarnedValueTechnique.Milestones:
                var met = wp.Milestones.Where(m => MilestoneMet(p, m, asOf)).ToList();
                return (new EvProgress(Started: met.Count > 0, MilestoneWeightDone: met.Sum(m => m.Weight)),
                    wp.Milestones.Count == 0 ? "No milestones defined" : $"{met.Count} of {wp.Milestones.Count} milestones met");
        }

        if (wp.PercentCompleteOverride is { } entered)
        {
            return (new EvProgress(entered > 0, entered >= 100, entered), $"Entered {entered:0.##}%");
        }

        var itemId = p.Wbs.TryGetValue(wp.WbsElementId, out var e) ? e.ItemId : null;
        if (itemId is { } id && p.Plan.Of(id) is { } plan)
        {
            return (new EvProgress(plan.Started, plan.Complete, plan.Percent), plan.Source);
        }

        return (new EvProgress(), "No plan task linked");
    }

    public static bool MilestoneMet(EvmProject p, EvmMilestone m, DateOnly asOf) =>
        m.CompletedOn is { } on ? on <= asOf : m.ItemId is { } item && p.Plan.IsDone(item);

    private static decimal Money(decimal value) => Financials.Money.Round(value);

    // ---- Status ----

    public static EvmStatusDto BuildStatus(EvmProject p, FiscalPeriod period, IReadOnlyList<EvRow> rows, string costBasis)
    {
        var closed = p.ClosedFor(period.Id);
        var previous = PreviousClosed(p, period);
        var prev = previous is null ? new Dictionary<Guid, EvRow>() : FrozenRows(previous).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        EvmPeriodAmounts PeriodOf(IEnumerable<EvRow> group)
        {
            var list = group.ToList();
            return new EvmPeriodAmounts(
                list.Sum(r => r.Bcws - (prev.TryGetValue(r.Key, out var q) ? q.Bcws : 0)),
                list.Sum(r => r.Bcwp - (prev.TryGetValue(r.Key, out var q) ? q.Bcwp : 0)),
                list.Sum(r => r.Acwp - (prev.TryGetValue(r.Key, out var q) ? q.Acwp : 0)));
        }

        string CodeOf(Guid id) => p.Wbs.TryGetValue(id, out var e) ? e.Code : "?";
        string NameOf(Guid id) => p.Wbs.TryGetValue(id, out var e) ? e.Name : "(removed)";

        var accounts = new List<EvmControlAccountStatusDto>();
        foreach (var group in rows.Where(r => r.ControlAccountId is not null).GroupBy(r => r.ControlAccountId!.Value).OrderBy(g => CodeOf(g.Key), StringComparer.Ordinal))
        {
            var managerEac = ManagerEacFor(p, group.Key, period);
            var metrics = EvmCalculator.Metrics(group.Sum(r => r.Budget), group.Sum(r => r.Bcws), group.Sum(r => r.Bcwp), group.Sum(r => r.Acwp), managerEac);
            var reasons = EvmCalculator.VarianceReasons(metrics, p.Baseline.ThresholdPercent, p.Baseline.ThresholdAmount);
            var var = p.Variances.FirstOrDefault(v => v.ControlAccountWbsId == group.Key && v.FiscalPeriodId == period.Id);
            var complete = var is not null && !string.IsNullOrWhiteSpace(var.Cause) && !string.IsNullOrWhiteSpace(var.Impact) && !string.IsNullOrWhiteSpace(var.CorrectiveAction);
            var packages = group.OrderBy(r => CodeOf(r.WbsElementId), StringComparer.Ordinal).Select(r => new EvmWorkPackageStatusDto(
                r.WorkPackageId, r.WbsElementId, CodeOf(r.WbsElementId), NameOf(r.WbsElementId), (EvTechnique)(int)r.Technique, r.Percent, r.Source,
                PeriodOf([r]), EvmCalculator.Metrics(r.Budget, r.Bcws, r.Bcwp, r.Acwp))).ToList();
            accounts.Add(new EvmControlAccountStatusDto(group.Key, CodeOf(group.Key), NameOf(group.Key), PeriodOf(group), metrics,
                reasons.Count > 0, reasons, complete, packages));
        }

        var outside = rows.Where(r => r.ControlAccountId is null).ToList();
        var totalManager = accounts.Sum(a => a.Cumulative.EacManager) + outside.Sum(r => r.Acwp);
        var total = EvmCalculator.Metrics(rows.Sum(r => r.Budget), rows.Sum(r => r.Bcws), rows.Sum(r => r.Bcwp), rows.Sum(r => r.Acwp), totalManager);
        var ub = closed?.UndistributedBudget ?? p.Baseline.UndistributedBudget;
        var mr = closed?.ManagementReserve ?? p.Baseline.ManagementReserve;
        var pmb = rows.Sum(r => r.Budget) + ub;

        var notes = new List<string>();
        var unassigned = rows.Where(r => r.WorkPackageId is null).Sum(r => r.Acwp);
        if (unassigned != 0)
        {
            notes.Add($"{unassigned:N2} of actual cost is on WBS elements without a work package (counted in ACWP, earns nothing).");
        }

        var skipped = p.Calendar.Count(c => c.Start > (previous?.PeriodEnd ?? DateOnly.MinValue) && c.End < period.Start
            && c.End >= (Span(p).FirstOrDefault()?.Start ?? DateOnly.MaxValue));
        if (closed is null && skipped > 0)
        {
            notes.Add($"{skipped} earlier period(s) were never closed, so this period's amounts run from {(previous is null ? "the start" : $"the close of {previous.PeriodEnd:yyyy-MM}")}.");
        }

        if (closed is null && p.WorkPackages.Any(w => w.Technique is not (EarnedValueTechnique.LevelOfEffort or EarnedValueTechnique.Apportioned or EarnedValueTechnique.Milestones)
            && w.PercentCompleteOverride is null && (p.Wbs.GetValueOrDefault(w.WbsElementId)?.ItemId is not { } i || !p.Plan.Has(i))))
        {
            notes.Add("Some work packages have no plan task linked and no entered progress; they earn nothing.");
        }

        return new EvmStatusDto(p.Project.Id, p.Project.Code, p.Project.Name, p.ContractNumber, RefOf(p, period), closed is not null,
            closed?.BaselineRevision ?? p.Baseline.Revision, accounts, PeriodOf(rows), total, ub, mr, pmb, pmb + mr, unassigned,
            closed is not null ? "Frozen when the period closed." : costBasis, notes);
    }

    /// <summary>The control account manager's latest estimate at or before the period.</summary>
    public static decimal? ManagerEacFor(EvmProject p, Guid controlAccountId, FiscalPeriod period)
    {
        var starts = p.Calendar.ToDictionary(c => c.Id, c => c.Start);
        return p.Variances
            .Where(v => v.ControlAccountWbsId == controlAccountId && v.ManagerEac is not null && starts.TryGetValue(v.FiscalPeriodId, out var s) && s <= period.Start)
            .OrderBy(v => starts[v.FiscalPeriodId])
            .LastOrDefault()?.ManagerEac;
    }

    /// <summary>Rows for a period: frozen when closed, otherwise live (loading the cumulative actual cost).</summary>
    public static async Task<(List<EvRow> Rows, string Basis)> RowsAsync(EvmProject p, FiscalPeriod period, BillingCostSource costs, IWorkbenchDbContext db, CancellationToken ct)
    {
        if (p.ClosedFor(period.Id) is { } closed)
        {
            return (FrozenRows(closed), "Frozen when the period closed.");
        }

        var cost = await CumulativeCostAsync(costs, db, p.Project.Id, period.End, ct);
        return (LiveRows(p, period, cost), cost.Basis);
    }

    public static async Task<EvmSeriesDto> SeriesAsync(EvmProject p, BillingCostSource costs, IWorkbenchDbContext db, CancellationToken ct)
    {
        var points = new List<EvmSeriesPointDto>();
        foreach (var period in Span(p))
        {
            var state = StateOf(p, period);
            decimal bcws;
            decimal? bcwp = null, acwp = null;
            if (state is EvmPeriodState.Closed or EvmPeriodState.Reporting)
            {
                var (rows, _) = await RowsAsync(p, period, costs, db, ct);
                bcws = rows.Sum(r => r.Bcws);
                bcwp = rows.Sum(r => r.Bcwp);
                acwp = rows.Sum(r => r.Acwp);
            }
            else
            {
                bcws = PlannedThrough(p, period.End);
            }

            points.Add(new EvmSeriesPointDto(period.Id, period.Name, period.Start, period.End, state, bcws, bcwp, acwp,
                bcwp is { } e && acwp is > 0 ? Math.Round(e / acwp.Value, 4) : null,
                bcwp is { } e2 && bcws > 0 ? Math.Round(e2 / bcws, 4) : null));
        }

        return new EvmSeriesDto(p.Project.Id, p.WorkPackages.Sum(w => w.Budget), points);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
