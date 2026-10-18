namespace Workbench.Application.Financials;

/// <summary>A plan-sheet row reduced to what budgeting needs.</summary>
public sealed record PlanSheetRow(Guid ItemId, Guid? ParentId, decimal? PlannedHours, decimal? Budget);

/// <summary>
/// Sums the "Planned hours" and "Budget" values of a plan item and its subtasks. A row whose subtasks have values
/// takes their sum (its own value is treated as a rollup and not counted twice); a row without uses its own value.
/// </summary>
public static class PlanSheetBudget
{
    public static (decimal Hours, decimal Budget) Sum(IReadOnlyList<PlanSheetRow> rows, Guid itemId)
    {
        var children = rows.Where(r => r.ParentId is not null).ToLookup(r => r.ParentId!.Value);
        var row = rows.FirstOrDefault(r => r.ItemId == itemId);
        if (row is null)
        {
            return (0m, 0m);
        }

        var (hours, budget) = SumOne(row, children, []);
        return (hours ?? 0m, budget ?? 0m);
    }

    /// <summary>Null means no row in the subtree has a value.</summary>
    private static (decimal? Hours, decimal? Budget) SumOne(PlanSheetRow row, ILookup<Guid, PlanSheetRow> children, HashSet<Guid> seen)
    {
        if (!seen.Add(row.ItemId))
        {
            return (null, null);
        }

        decimal? hours = null, budget = null;
        foreach (var child in children[row.ItemId])
        {
            var (h, b) = SumOne(child, children, seen);
            hours = h is null ? hours : (hours ?? 0) + h;
            budget = b is null ? budget : (budget ?? 0) + b;
        }

        return (hours ?? row.PlannedHours, budget ?? row.Budget);
    }
}
