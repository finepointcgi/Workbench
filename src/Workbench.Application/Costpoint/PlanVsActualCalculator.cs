using System.Globalization;
using Workbench.Contracts.Costpoint;
using Workbench.Domain;
using Workbench.Domain.Costpoint;

namespace Workbench.Application.Costpoint;

/// <summary>
/// Plan-vs-actual math, kept free of EF so it can be tested directly.
/// <list type="bullet">
/// <item>One row per distinct WBS id on the sheet's items.</item>
/// <item>Planned hours / budget sum the "Planned hours" (Number) and "Budget" (Currency) columns over
/// <b>leaf</b> items mapped to that WBS. Parent rows are treated as summaries, so a rolled-up parent value
/// isn't counted twice.</item>
/// <item>Costpoint budget and actuals include the WBS element and its descendants in the Costpoint WBS tree.
/// Per WBS element, whole-life (TOTAL) budgets are used when present, otherwise the per-period budgets are summed.</item>
/// <item>The totals row counts each Costpoint WBS element once, even if several rows include it.</item>
/// </list>
/// </summary>
public static class PlanVsActualCalculator
{
    public const string PlannedHoursColumn = "Planned hours";
    public const string BudgetColumn = "Budget";

    public static (IReadOnlyList<PlanVsActualRow> Rows, PlanVsActualRow Totals, IReadOnlyList<string> Notes) Compute(
        IReadOnlyCollection<Column> columns,
        IReadOnlyCollection<Item> items,
        IReadOnlyCollection<CpWbsElement> wbs,
        IReadOnlyCollection<CpBudget> budgets,
        IReadOnlyCollection<CpActual> actuals)
    {
        var notes = new List<string>();
        var hoursColumn = FindColumn(columns, PlannedHoursColumn, ColumnType.Number, notes);
        var budgetColumn = FindColumn(columns, BudgetColumn, ColumnType.Currency, notes);

        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
        var leaves = items.Where(i => !parents.Contains(i.Id) && i.CostpointWbsId is not null).ToList();
        var wbsIds = items.Select(i => i.CostpointWbsId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var names = wbs.GroupBy(w => w.WbsId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var children = wbs.Where(w => w.ParentWbsId is not null).ToLookup(w => w.ParentWbsId!, w => w.WbsId, StringComparer.Ordinal);
        var budgetByWbs = budgets.Where(b => b.WbsId.Length > 0).GroupBy(b => b.WbsId, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => g.Any(b => b.FiscalPeriod == CpBudget.TotalPeriod)
                ? (Hours: g.Where(b => b.FiscalPeriod == CpBudget.TotalPeriod).Sum(b => b.Hours), Amount: g.Where(b => b.FiscalPeriod == CpBudget.TotalPeriod).Sum(b => b.Amount))
                : (Hours: g.Sum(b => b.Hours), Amount: g.Sum(b => b.Amount)),
            StringComparer.Ordinal);
        var actualByWbs = actuals.GroupBy(a => a.WbsId, StringComparer.Ordinal).ToDictionary(
            g => g.Key, g => (Hours: g.Sum(a => a.Hours), Cost: g.Sum(a => a.Cost)), StringComparer.Ordinal);

        var rows = new List<PlanVsActualRow>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wbsId in wbsIds)
        {
            if (!names.ContainsKey(wbsId))
            {
                notes.Add($"WBS {wbsId} isn't in the Costpoint mirror for this project.");
            }

            var subtree = Subtree(wbsId, children);
            covered.UnionWith(subtree);
            var mapped = leaves.Where(i => i.CostpointWbsId == wbsId).ToList();
            var (budgetHours, budgetAmount, actualHours, actualCost) = CostpointTotals(subtree, budgetByWbs, actualByWbs);
            rows.Add(new PlanVsActualRow(
                wbsId,
                names.GetValueOrDefault(wbsId),
                mapped.Count,
                Sum(mapped, hoursColumn),
                Sum(mapped, budgetColumn),
                budgetHours,
                budgetAmount,
                actualHours,
                actualCost));
        }

        var all = CostpointTotals(covered, budgetByWbs, actualByWbs);
        var totals = new PlanVsActualRow(
            "Total",
            null,
            rows.Sum(r => r.ItemCount),
            rows.Sum(r => r.PlannedHours),
            rows.Sum(r => r.PlannedBudget),
            all.BudgetHours,
            all.BudgetAmount,
            all.ActualHours,
            all.ActualCost);
        return (rows, totals, notes);
    }

    private static Column? FindColumn(IReadOnlyCollection<Column> columns, string name, ColumnType type, List<string> notes)
    {
        var column = columns.FirstOrDefault(c => c.Type == type && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            notes.Add($"Add a {type} column named \"{name}\" to plan against Costpoint.");
        }

        return column;
    }

    private static decimal Sum(IEnumerable<Item> items, Column? column)
    {
        if (column is null)
        {
            return 0;
        }

        var key = column.Id.ToString();
        return items.Sum(i => i.Values.TryGetValue(key, out var value)
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : 0);
    }

    private static HashSet<string> Subtree(string root, ILookup<string, string> children)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { root };
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var current))
        {
            foreach (var child in children[current])
            {
                if (result.Add(child))
                {
                    pending.Push(child);
                }
            }
        }

        return result;
    }

    private static (decimal BudgetHours, decimal BudgetAmount, decimal ActualHours, decimal ActualCost) CostpointTotals(
        IEnumerable<string> wbsIds,
        Dictionary<string, (decimal Hours, decimal Amount)> budgets,
        Dictionary<string, (decimal Hours, decimal Cost)> actuals)
    {
        decimal budgetHours = 0, budgetAmount = 0, actualHours = 0, actualCost = 0;
        foreach (var id in wbsIds)
        {
            if (budgets.TryGetValue(id, out var b))
            {
                budgetHours += b.Hours;
                budgetAmount += b.Amount;
            }

            if (actuals.TryGetValue(id, out var a))
            {
                actualHours += a.Hours;
                actualCost += a.Cost;
            }
        }

        return (budgetHours, budgetAmount, actualHours, actualCost);
    }
}
