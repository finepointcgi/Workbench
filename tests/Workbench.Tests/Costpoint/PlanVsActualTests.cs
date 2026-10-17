using Workbench.Application.Costpoint;
using Workbench.Domain;
using Workbench.Domain.Costpoint;

namespace Workbench.Tests.Costpoint;

public sealed class PlanVsActualTests
{
    private readonly Column _hours = new() { Name = "Planned hours", Type = ColumnType.Number };
    private readonly Column _budget = new() { Name = "Budget", Type = ColumnType.Currency };

    private Item Leaf(string wbs, string? hours, string? budget, Guid? parentId = null) => new()
    {
        Title = "Task",
        ParentId = parentId,
        CostpointWbsId = wbs,
        Values = new() { [_hours.Id.ToString()] = hours, [_budget.Id.ToString()] = budget },
    };

    private static CpWbsElement Wbs(string id, string? parent = null) => new() { ProjectId = "P", WbsId = id, ParentWbsId = parent, Name = id + " name" };

    private static CpActual Actual(string wbs, string period, decimal hours, decimal cost) =>
        new() { ProjectId = "P", WbsId = wbs, FiscalPeriod = period, Hours = hours, Cost = cost };

    private static CpBudget Budget(string wbs, string period, decimal hours, decimal amount) =>
        new() { ProjectId = "P", WbsId = wbs, FiscalPeriod = period, Hours = hours, Amount = amount };

    [Fact]
    public void Sums_plan_per_wbs_against_actuals_with_variance()
    {
        var parent = Leaf("A", "999", "99999");
        Item[] items =
        [
            parent,
            Leaf("A", "8", "1200", parent.Id),
            Leaf("A", "2.5", "300", parent.Id),
            Leaf("B", "60", "9000"),
            Leaf("B", "not a number", null),
        ];
        CpActual[] actuals = [Actual("A", "2026-09", 6, 870), Actual("A", "2026-10", 4, 580), Actual("B", "2026-10", 15, 2175)];
        CpBudget[] budgets = [Budget("A", CpBudget.TotalPeriod, 16, 2400), Budget("B", "2026-09", 40, 6000), Budget("B", "2026-10", 40, 6000)];

        var (rows, totals, notes) = PlanVsActualCalculator.Compute([_hours, _budget], items, [Wbs("A"), Wbs("B")], budgets, actuals);

        Assert.Empty(notes);
        var a = rows.Single(r => r.WbsId == "A");
        Assert.Equal((2, 10.5m, 1500m), (a.ItemCount, a.PlannedHours, a.PlannedBudget)); // parent row is a summary, not counted
        Assert.Equal((10m, 1450m), (a.ActualHours, a.ActualCost));
        Assert.Equal((0.5m, 50m), (a.HoursVariance, a.CostVariance));
        Assert.Equal((16m, 2400m), (a.BudgetHours, a.BudgetAmount));
        Assert.Equal(1450m / 1500m, a.Burn);

        var b = rows.Single(r => r.WbsId == "B");
        Assert.Equal((60m, 9000m, 15m, 2175m), (b.PlannedHours, b.PlannedBudget, b.ActualHours, b.ActualCost));
        Assert.Equal((80m, 12000m), (b.BudgetHours, b.BudgetAmount)); // no TOTAL row: per-period budgets summed

        Assert.Equal((4, 70.5m, 10500m, 25m, 3625m), (totals.ItemCount, totals.PlannedHours, totals.PlannedBudget, totals.ActualHours, totals.ActualCost));
        Assert.Equal(10500m - 3625m, totals.CostVariance);
    }

    [Fact]
    public void Costpoint_figures_roll_up_descendant_wbs_once_in_totals()
    {
        Item[] items = [Leaf("A", "10", "1000"), Leaf("A.1", "5", "500")];
        CpActual[] actuals = [Actual("A.1", "2026-10", 3, 300), Actual("A.2", "2026-10", 4, 400)];

        var (rows, totals, _) = PlanVsActualCalculator.Compute([_hours, _budget], items, [Wbs("A"), Wbs("A.1", "A"), Wbs("A.2", "A")], [], actuals);

        Assert.Equal(700m, rows.Single(r => r.WbsId == "A").ActualCost);
        Assert.Equal(300m, rows.Single(r => r.WbsId == "A.1").ActualCost);
        Assert.Equal(700m, totals.ActualCost);
        Assert.Equal(300m, rows.Single(r => r.WbsId == "A").CostVariance);
    }

    [Fact]
    public void Missing_columns_and_unknown_wbs_are_reported()
    {
        var (rows, _, notes) = PlanVsActualCalculator.Compute([], [Leaf("Z", "1", "1")], [], [], []);

        Assert.Equal(0m, rows.Single().PlannedHours);
        Assert.Null(rows.Single().Burn);
        Assert.Equal(3, notes.Count);
        Assert.Contains(notes, n => n.Contains("Planned hours"));
        Assert.Contains(notes, n => n.Contains("WBS Z"));
    }
}
