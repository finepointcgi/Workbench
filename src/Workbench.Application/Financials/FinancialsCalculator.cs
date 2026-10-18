using Workbench.Contracts.Financials;

namespace Workbench.Application.Financials;

public sealed record WbsNode(Guid Id, Guid? ParentId, string Code, string Name, int Order);

/// <summary>A budget (or forecast) line reduced to hours and dollars.</summary>
public sealed record BudgetAmount(Guid WbsElementId, DateOnly? PeriodStart, decimal Hours, decimal Amount);

/// <summary>Approved hours on one work date (only approved time is passed in).</summary>
public sealed record LaborActual(Guid WbsElementId, Guid EmployeeId, Guid LaborCategoryId, DateOnly WorkDate, decimal Hours);

/// <summary>Labor actual priced at cost and at bill rate.</summary>
public sealed record CostedLabor(Guid WbsElementId, DateOnly WorkDate, decimal Hours, decimal Cost, decimal BillableValue);

/// <summary>An approved, allowable ODC (only those are passed in).</summary>
public sealed record OdcActual(Guid WbsElementId, DateOnly IncurredOn, decimal Amount);

public sealed record FinancialInputs(
    IReadOnlyList<WbsNode> Wbs,
    IReadOnlyList<BudgetAmount> Budget,
    IReadOnlyList<CostedLabor> Labor,
    IReadOnlyList<OdcActual> Odcs,

    /// <summary>When set, ETC is this forecast's remaining amounts instead of the remaining budget.</summary>
    IReadOnlyList<BudgetAmount>? Forecast = null,

    /// <summary>Forecast lines phased into periods that start before this date are already behind us and don't count as ETC.</summary>
    DateOnly? RemainingFrom = null);

public sealed record FinancialsResult(IReadOnlyList<WbsFinancialRowDto> Rows, FinancialFigures Totals);

public static class LaborCosting
{
    /// <summary>
    /// Prices each entry at the employee's cost rate effective on the work date and at the contract bill rate for its
    /// labor category on that date. Entries without a rate cost 0 and are counted in <paramref name="missingCostRateHours"/>.
    /// </summary>
    public static IReadOnlyList<CostedLabor> Cost(
        IEnumerable<LaborActual> labor,
        EffectiveRateTable<Guid> costRates,
        EffectiveRateTable<Guid>? billRates,
        out decimal missingCostRateHours)
    {
        missingCostRateHours = 0;
        var result = new List<CostedLabor>();
        foreach (var entry in labor)
        {
            var cost = costRates.Find(entry.EmployeeId, entry.WorkDate);
            if (cost is null)
            {
                missingCostRateHours += entry.Hours;
            }

            var bill = billRates?.Find(entry.LaborCategoryId, entry.WorkDate) ?? 0m;
            result.Add(new CostedLabor(entry.WbsElementId, entry.WorkDate, entry.Hours,
                Money.Round(entry.Hours * (cost ?? 0m)), Money.Round(entry.Hours * bill)));
        }

        return result;
    }
}

/// <summary>Budget vs actual vs EAC per WBS element, rolled up the WBS tree.</summary>
public static class FinancialsCalculator
{
    public static FinancialsResult Compute(FinancialInputs inputs)
    {
        var direct = new Dictionary<Guid, Amounts>();
        Amounts For(Guid id) => direct.TryGetValue(id, out var a) ? a : direct[id] = new Amounts();

        foreach (var line in inputs.Budget)
        {
            var a = For(line.WbsElementId);
            a.BudgetHours += line.Hours;
            a.BudgetAmount += line.Amount;
        }

        foreach (var entry in inputs.Labor)
        {
            var a = For(entry.WbsElementId);
            a.ActualHours += entry.Hours;
            a.LaborCost += entry.Cost;
            a.Billable += entry.BillableValue;
        }

        foreach (var odc in inputs.Odcs)
        {
            For(odc.WbsElementId).OdcCost += odc.Amount;
        }

        if (inputs.Forecast is { } forecast)
        {
            foreach (var line in forecast.Where(l => l.PeriodStart is null || inputs.RemainingFrom is null || l.PeriodStart >= inputs.RemainingFrom))
            {
                var a = For(line.WbsElementId);
                a.EtcHours += line.Hours;
                a.EtcAmount += line.Amount;
            }
        }
        else
        {
            // Remaining budget, element by element: an overrun on one task doesn't reduce another task's ETC.
            foreach (var a in direct.Values)
            {
                a.EtcHours = Math.Max(0, a.BudgetHours - a.ActualHours);
                a.EtcAmount = Math.Max(0, a.BudgetAmount - a.LaborCost - a.OdcCost);
            }
        }

        var known = inputs.Wbs.Select(w => w.Id).ToHashSet();
        var children = inputs.Wbs
            .Where(w => w.ParentId is { } p && known.Contains(p))
            .ToLookup(w => w.ParentId!.Value);
        var roots = Ordered(inputs.Wbs.Where(w => w.ParentId is not { } p || !known.Contains(p)));

        var rows = new List<WbsFinancialRowDto>();
        var total = new Amounts();
        var visited = new HashSet<Guid>();
        foreach (var root in roots)
        {
            total.Add(Visit(root, null, 0));
        }

        // Amounts on elements outside the tree (shouldn't happen) still count toward the project total.
        foreach (var (id, a) in direct.Where(d => !known.Contains(d.Key)))
        {
            total.Add(a);
        }

        return new FinancialsResult(rows, total.ToFigures());

        Amounts Visit(WbsNode node, Guid? parentId, int depth)
        {
            var index = rows.Count;
            rows.Add(null!);
            var sum = new Amounts();
            if (direct.TryGetValue(node.Id, out var own))
            {
                sum.Add(own);
            }

            var kids = visited.Add(node.Id) ? Ordered(children[node.Id]) : [];
            foreach (var child in kids)
            {
                sum.Add(Visit(child, node.Id, depth + 1));
            }

            rows[index] = new WbsFinancialRowDto(node.Id, parentId, node.Code, node.Name, depth, kids.Count == 0, sum.ToFigures());
            return sum;
        }
    }

    private static List<WbsNode> Ordered(IEnumerable<WbsNode> nodes) =>
        nodes.OrderBy(n => n.Order).ThenBy(n => n.Code, StringComparer.Ordinal).ToList();

    /// <summary>Additive amounts; percentages and variances are derived after rolling up.</summary>
    private sealed class Amounts
    {
        public decimal BudgetHours, BudgetAmount, ActualHours, LaborCost, OdcCost, EtcHours, EtcAmount, Billable;

        public void Add(Amounts o)
        {
            BudgetHours += o.BudgetHours;
            BudgetAmount += o.BudgetAmount;
            ActualHours += o.ActualHours;
            LaborCost += o.LaborCost;
            OdcCost += o.OdcCost;
            EtcHours += o.EtcHours;
            EtcAmount += o.EtcAmount;
            Billable += o.Billable;
        }

        public FinancialFigures ToFigures()
        {
            var actual = LaborCost + OdcCost;
            var eacHours = ActualHours + EtcHours;
            var eac = actual + EtcAmount;
            return new FinancialFigures(
                BudgetHours, BudgetAmount, ActualHours, LaborCost, OdcCost, actual,
                BudgetAmount == 0 ? null : Math.Round(actual / BudgetAmount, 4, MidpointRounding.AwayFromZero),
                BudgetAmount - actual,
                EtcHours, EtcAmount, eacHours, eac,
                BudgetHours - eacHours, BudgetAmount - eac,
                Billable);
        }
    }
}
