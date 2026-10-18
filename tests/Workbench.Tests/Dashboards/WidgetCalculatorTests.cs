using Workbench.Application.Dashboards;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using static Workbench.Tests.MyWork.MyWorkCalculatorTests;

namespace Workbench.Tests.Dashboards;

public sealed class WidgetCalculatorTests
{
    private static readonly ColumnDto Status = new(Guid.NewGuid(), "Status", ColumnKind.Dropdown, 0, ["Not started", "In progress", "Done"], null);
    private static readonly ColumnDto Owner = new(Guid.NewGuid(), "Owner", ColumnKind.Person, 1, [], null);
    private static readonly ColumnDto Hours = new(Guid.NewGuid(), "Hours", ColumnKind.Number, 2, [], null);
    private static readonly ColumnDto Budget = new(Guid.NewGuid(), "Budget", ColumnKind.Currency, 3, [], null);
    private static readonly ColumnDto Notes = new(Guid.NewGuid(), "Notes", ColumnKind.Text, 4, [], null);

    private static readonly SheetDetail Sheet = Sheet(true, Status, Owner, Hours, Budget, Notes);

    private static readonly ItemDto Parent = Item("Phase", null, (Hours, "1000"), (Budget, "99999"));

    private static readonly IReadOnlyList<ItemDto> Items =
    [
        Parent,
        Child("Design", new DateOnly(2026, 10, 20), (Status, "In progress"), (Owner, "bo@example.com"), (Hours, "10"), (Budget, "1500")),
        Child("Build", new DateOnly(2026, 10, 15), (Status, "in progress"), (Owner, "al@example.com"), (Hours, "30"), (Budget, "4500.50")),
        Child("Test", null, (Status, "Done"), (Owner, "al@example.com"), (Hours, "n/a")),
        Child("Ship", new DateOnly(2026, 11, 1), (Hours, "5")),
        Child("Legacy", new DateOnly(2026, 9, 1), (Status, "On hold"), (Owner, "bo@example.com"), (Hours, "2")),
    ];

    private static ItemDto Child(string title, DateOnly? finish, params (ColumnDto, string?)[] values) =>
        Item(title, finish, values) with { ParentId = Parent.Id };

    [Fact]
    public void Metric_counts_leaf_items_with_an_optional_filter()
    {
        var all = WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id));
        Assert.Equal((5m, "Items"), (all.Value, all.Label)); // the parent row is a summary

        var inProgress = WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, FilterColumnId: Status.Id, FilterValue: "In Progress "));
        Assert.Equal((2m, "Items where Status = In Progress"), (inProgress.Value, inProgress.Label));

        var blank = WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, FilterColumnId: Status.Id));
        Assert.Equal(1m, blank.Value); // "Ship" has no status
    }

    [Fact]
    public void Metric_sums_and_averages_numbers_skipping_blanks_and_text()
    {
        var sum = WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, MetricAggregate.Sum, Budget.Id));
        Assert.Equal((6000.50m, "Sum of Budget", true), (sum.Value, sum.Label, sum.IsCurrency));

        var average = WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, MetricAggregate.Average, Hours.Id,
            FilterColumnId: Owner.Id, FilterValue: "al@example.com"));
        Assert.Equal(30m, average.Value); // "n/a" is skipped, not counted as zero
        Assert.Equal(2, average.ItemCount);
        Assert.False(average.IsCurrency);
    }

    [Fact]
    public void Metric_reports_bad_columns()
    {
        Assert.Throws<WidgetConfigException>(() =>
            WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, MetricAggregate.Sum, Notes.Id)));
        Assert.Throws<WidgetConfigException>(() =>
            WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, MetricAggregate.Sum, Guid.NewGuid())));
        Assert.Throws<WidgetConfigException>(() =>
            WidgetCalculator.Metric(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, MetricAggregate.Average)));
    }

    [Fact]
    public void Chart_groups_by_dropdown_in_option_order_with_extras_and_blanks_last()
    {
        var chart = WidgetCalculator.Chart(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, Chart: ChartKind.Donut, GroupColumnId: Status.Id));

        Assert.Equal(ChartKind.Donut, chart.Kind);
        Assert.Equal(["Not started", "In progress", "Done", "On hold", "(none)"], chart.Labels);
        Assert.Equal([0d, 2d, 1d, 1d, 1d], chart.Values);
        Assert.Equal("Items", chart.SeriesName);
    }

    [Fact]
    public void Chart_sums_a_number_column_by_person()
    {
        var chart = WidgetCalculator.Chart(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, ValueColumnId: Hours.Id, GroupColumnId: Owner.Id));

        Assert.Equal(["al@example.com", "bo@example.com", "(none)"], chart.Labels);
        Assert.Equal([30d, 12d, 5d], chart.Values);
        Assert.Equal("Hours", chart.SeriesName);
    }

    [Fact]
    public void Chart_needs_a_dropdown_or_person_column()
    {
        var ex = Assert.Throws<WidgetConfigException>(() =>
            WidgetCalculator.Chart(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, GroupColumnId: Hours.Id)));
        Assert.Contains("Dropdown or Person", ex.Message);
    }

    [Fact]
    public void Item_list_filters_and_sorts_by_finish_with_undated_last()
    {
        var list = WidgetCalculator.ItemList(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, MaxItems: 3));

        Assert.Equal(5, list.Total);
        Assert.Equal(["Legacy", "Build", "Design"], list.Items.Select(i => i.Title));
        Assert.Equal("in progress", list.Items[1].Status);

        var mine = WidgetCalculator.ItemList(Sheet, Items, new WidgetConfig(Sheet.Summary.Id, FilterColumnId: Owner.Id, FilterValue: "al@example.com"));
        Assert.Equal(["Build", "Test"], mine.Items.Select(i => i.Title));
    }

    [Fact]
    public void Schedule_health_counts_critical_and_late_tasks_and_lists_next_milestones()
    {
        var today = new DateOnly(2026, 10, 16);
        ItemDto[] items =
        [
            Item("Late", new DateOnly(2026, 10, 10), (Status, "In progress")) with { Start = new DateOnly(2026, 10, 1) },
            Item("Late but done", new DateOnly(2026, 10, 12), (Status, "Done")) with { Start = new DateOnly(2026, 10, 1) },
            Item("Past milestone", new DateOnly(2026, 10, 1)) with { Start = new DateOnly(2026, 10, 1) },
            .. Enumerable.Range(1, 6).Select(i => Item($"M{i}", today.AddDays(i * 7)) with { Start = today.AddDays(i * 7) }),
            Item("Today", today) with { Start = today },
            Item("Long task", new DateOnly(2026, 12, 1)) with { Start = today },
        ];
        var analysis = new ScheduleAnalysis(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 1), [items[0].Id, items[^1].Id],
            items.Select(i => new ItemSlack(i.Id, i.Start!.Value, i.Finish!.Value, 0, false)).ToList());

        var health = WidgetCalculator.ScheduleHealth(Sheet, items, analysis, today);

        Assert.Equal((items.Length, 2, 2, new DateOnly(2026, 12, 1)), (health.TaskCount, health.CriticalTasks, health.LateTasks, health.ProjectFinish));
        Assert.Equal(["Today", "M1", "M2", "M3", "M4"], health.NextMilestones.Select(m => m.Title)); // late = "Late" + past milestone
    }

    [Theory]
    [InlineData("2026-10-14", 351, 0.519)] // about halfway through a two-year period
    [InlineData("2025-01-01", 729, 0.0)]   // before it starts: the whole period is left
    [InlineData("2027-12-31", 0, 1.0)]     // after it ends
    public void Contract_funding_reports_value_and_period_of_performance(string today, int daysLeft, double elapsed)
    {
        var contract = new ContractDto(Guid.NewGuid(), Guid.NewGuid(), "Army", "W56-0001", "Logistics support", ContractKind.TimeAndMaterials,
            ContractState.Active, true, new DateOnly(2025, 10, 1), new DateOnly(2027, 9, 30), 2_000_000m, 500_000m);
        ProjectDto[] projects =
        [
            new(Guid.NewGuid(), "P-200", "Phase 2", contract.Id, contract.ContractNumber, null, ProjectState.Active, null, null, null),
            new(Guid.NewGuid(), "P-100", "Phase 1", contract.Id, contract.ContractNumber, null, ProjectState.Active, null, null, null),
            new(Guid.NewGuid(), "X-1", "Other", Guid.NewGuid(), "OTHER", null, ProjectState.Active, null, null, null),
        ];

        var data = WidgetCalculator.ContractFunding(contract, projects, DateOnly.Parse(today));

        Assert.Equal(("W56-0001", "Army", "Active", 2_000_000m, 500_000m), (data.ContractNumber, data.CustomerName, data.Status, data.CeilingValue, data.FundedValue));
        Assert.Equal(0.25m, data.FundedShare);
        Assert.Equal(daysLeft, data.DaysLeft);
        Assert.Equal(elapsed, data.PeriodElapsed, 0.01);
        Assert.Equal(["P-100", "P-200"], data.Projects);
    }
}
