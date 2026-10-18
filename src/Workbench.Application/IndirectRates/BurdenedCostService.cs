using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.IndirectRates;

/// <summary>
/// Burdened cost per WBS element: approved direct labor (hours × effective cost rate) and allowable direct ODCs, burdened
/// in cascade order with the rates of each cost's fiscal year. Costs are summed per element and fiscal year before the
/// rates apply (so a range inside one period matches that period's allocation to the cent).
/// </summary>
public sealed class BurdenedCostService(IWorkbenchDbContextFactory dbFactory) : IBurdenedCostService
{
    public async Task<BurdenedCostResult> GetProjectCostAsync(Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The date range ends before it starts.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var book = await RateBook.LoadAsync(db, cancellationToken);
        var costs = await DirectCosts.LoadAsync(db, projectId, from, to, [], cancellationToken);
        return await BurdenAsync(db, book, costs, (IndirectRateBasis)(int)basis, cancellationToken);
    }

    internal static async Task<BurdenedCostResult> BurdenAsync(IWorkbenchDbContext db, RateBook book, IReadOnlyList<DirectCost> costs, IndirectRateBasis basis, CancellationToken ct)
    {
        var wbsIds = costs.Select(c => c.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var sets = new Dictionary<int, RateSet>();
        RateSet? SetFor(DateOnly date)
        {
            if (book.YearOf(date) is not { } year)
            {
                return null;
            }

            return sets.TryGetValue(year.Year, out var set) ? set : sets[year.Year] = book.For(year.Year, basis);
        }

        var lines = new List<BurdenedCostLine>();
        foreach (var element in costs.GroupBy(c => c.WbsElementId))
        {
            var w = wbs[element.Key];
            var burden = Burden.Zero;
            foreach (var year in element.GroupBy(c => SetFor(c.Date)))
            {
                if (year.Key is { } set)
                {
                    burden = burden.Add(book.Apply(set, year.Sum(c => c.Labor), year.Sum(c => c.Odc)));
                }
            }

            lines.Add(new BurdenedCostLine(w.ProjectId, w.Id, w.Code, w.ClinId,
                element.Sum(c => c.Hours), element.Sum(c => c.Labor), burden.Fringe, burden.Overhead, burden.GeneralAndAdministrative,
                element.Sum(c => c.Odc), element.Sum(c => c.Unallowable)));
        }

        return new BurdenedCostResult(
            lines.OrderBy(l => l.WbsCode, StringComparer.Ordinal).ToList(),
            sets.Values.OrderBy(s => s.FiscalYear).Select(s => s.ToContract()).ToList());
    }
}

/// <summary>Project financials at burdened cost, built on the direct figures the financials compute.</summary>
public sealed class BurdenedFinancialsService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IBurdenedFinancialsService
{
    public async Task<BurdenedFinancialsDto> GetProjectAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var data = await FinancialsLoader.LoadProjectAsync(db, projectId, budgetVersionId, forecastVersionId, today, cancellationToken);
        var direct = FinancialsCalculator.Compute(data.ToInputs());
        var book = await RateBook.LoadAsync(db, cancellationToken);
        var notes = new List<string>();
        var costs = await DirectCosts.LoadAsync(db, projectId, DateOnly.MinValue, DateOnly.MaxValue, notes, cancellationToken);
        var burdened = await BurdenedCostService.BurdenAsync(db, book, costs, IndirectRateBasis.Actual, cancellationToken);
        var burdenByWbs = burdened.Lines.ToDictionary(l => l.WbsElementId, l => new Burden(l.Fringe, l.Overhead, l.GeneralAndAdministrative));

        // Future labor is burdened at the current fiscal year's labor multiplier when an element has no actuals yet.
        var currentYear = book.YearOf(today)?.Year ?? book.Years.LastOrDefault()?.Year;
        var current = currentYear is { } cy ? book.For(cy, IndirectRateBasis.Actual) : null;
        var laborMultiplier = current is null ? 1m : BurdenCascade.LaborMultiplier(book.Steps, current.Of);

        // Own (not rolled-up) amounts per element, then roll up the burdened figures in reverse tree order.
        var rows = direct.Rows;
        var childSums = new Dictionary<Guid, (decimal Actual, decimal Etc)>();
        foreach (var row in rows)
        {
            if (row.ParentId is { } p)
            {
                var s = childSums.GetValueOrDefault(p);
                childSums[p] = (s.Actual + row.Figures.ActualCost, s.Etc + row.Figures.EtcAmount);
            }
        }

        var own = new Dictionary<Guid, Totals>();
        foreach (var row in rows)
        {
            var c = childSums.GetValueOrDefault(row.WbsElementId);
            var actual = row.Figures.ActualCost - c.Actual;
            var etc = row.Figures.EtcAmount - c.Etc;
            var burden = burdenByWbs.GetValueOrDefault(row.WbsElementId) ?? Burden.Zero;
            var multiplier = actual != 0 ? (actual + burden.Total) / actual : laborMultiplier;
            own[row.WbsElementId] = new Totals(burden, Money.Round(etc * multiplier));
        }

        var rolled = own.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var row in rows.Reverse())
        {
            if (row.ParentId is { } p && rolled.TryGetValue(p, out var parent))
            {
                rolled[p] = parent.Add(rolled[row.WbsElementId]);
            }
        }

        BurdenedFiguresDto Figures(Contracts.Financials.FinancialFigures f, Totals t) =>
            new(f.BudgetAmount, f.ActualCost, t.Burden.Fringe, t.Burden.Overhead, t.Burden.GeneralAndAdministrative, f.EtcAmount, t.Etc, f.EacAmount);

        var resultRows = rows.Select(r => new BurdenedWbsRowDto(r.WbsElementId, r.ParentId, r.Code, r.Name, r.Depth, r.IsLeaf, Figures(r.Figures, rolled[r.WbsElementId]))).ToList();
        var total = rows.Where(r => r.ParentId is null || !rolled.ContainsKey(r.ParentId.Value)).Aggregate(new Totals(Burden.Zero, 0), (acc, r) => acc.Add(rolled[r.WbsElementId]));

        if (!book.HasRates)
        {
            notes.Add("No indirect rates are set up, so burdened cost equals direct cost.");
        }
        else
        {
            notes.Add("Actuals are burdened at actual rates where a year has them, otherwise provisional; the estimate to complete at the burden experienced so far. Budgets are direct cost.");
        }

        var unallowable = costs.Sum(c => c.Unallowable);
        if (unallowable != 0)
        {
            notes.Add($"{unallowable:C} of unallowable cost is excluded and never burdened.");
        }

        return new BurdenedFinancialsDto(projectId, book.HasRates, burdened.Rates, Figures(direct.Totals, total), resultRows, notes);
    }

    private sealed record Totals(Burden Burden, decimal Etc)
    {
        public Totals Add(Totals o) => new(Burden.Add(o.Burden), Etc + o.Etc);
    }
}
