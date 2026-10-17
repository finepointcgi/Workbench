using Workbench.Application.Formulas;
using Workbench.Domain;
using Workbench.Domain.Formulas;

namespace Workbench.Tests.Formulas;

public sealed class FormulaCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private readonly List<Column> _columns = [];
    private readonly List<Item> _items = [];

    private Column AddColumn(string name, ColumnType type = ColumnType.Number, string? formula = null)
    {
        var column = new Column { Name = name, Type = type, Order = _columns.Count, Formula = formula };
        _columns.Add(column);
        return column;
    }

    private Item AddItem(string title, Item? parent = null, params (Column Column, string? Value)[] values)
    {
        var item = new Item
        {
            Title = title,
            ParentId = parent?.Id,
            Order = _items.Count(i => i.ParentId == parent?.Id),
            Values = values.ToDictionary(v => v.Column.Id.ToString(), v => v.Value),
        };
        _items.Add(item);
        return item;
    }

    private string? ValueOf(Item item, Column column) =>
        FormulaCalculator.Calculate(_columns, _items, Today)[item.Id][column.Id];

    [Fact]
    public void Computes_the_seeded_cost_per_hour_formula()
    {
        var hours = AddColumn("Planned hours");
        var budget = AddColumn("Budget", ColumnType.Currency);
        var costPerHour = AddColumn("Cost per hour", ColumnType.Currency, "[Budget] / [Planned hours]");
        var kickoff = AddItem("Kickoff", null, (hours, "8"), (budget, "1200"));
        var review = AddItem("Review", null, (hours, "60"), (budget, "9000"));
        var empty = AddItem("Empty");

        var result = FormulaCalculator.Calculate(_columns, _items, Today);

        Assert.Equal("150", result[kickoff.Id][costPerHour.Id]);
        Assert.Equal("150", result[review.Id][costPerHour.Id]);
        Assert.Equal(FormulaErrors.DivideByZero, result[empty.Id][costPerHour.Id]);
        Assert.Equal([costPerHour.Id], result[kickoff.Id].Keys);
    }

    [Fact]
    public void Returns_nothing_when_the_sheet_has_no_formulas()
    {
        AddColumn("Hours");
        AddItem("Task");

        Assert.Empty(FormulaCalculator.Calculate(_columns, _items, Today));
    }

    [Fact]
    public void Formula_columns_can_read_formula_columns_defined_after_them()
    {
        var hours = AddColumn("Hours");
        var quadrupled = AddColumn("Quadrupled", formula: "[Doubled] * 2");
        var doubled = AddColumn("Doubled", formula: "[Hours] * 2");
        var task = AddItem("Task", null, (hours, "5"));

        Assert.Equal("20", ValueOf(task, quadrupled));
        Assert.Equal("10", ValueOf(task, doubled));
    }

    [Fact]
    public void Evaluation_order_puts_dependencies_first_and_keeps_column_order_otherwise()
    {
        var c = AddColumn("C", formula: "[B] + [A]");
        var b = AddColumn("B", formula: "[A] * 2");
        var independent = AddColumn("Independent", formula: "1");
        var a = AddColumn("A", formula: "[Hours]");
        AddColumn("Hours");

        var (order, circular) = FormulaCalculator.EvaluationOrder(_columns);

        Assert.Equal([independent.Name, a.Name, b.Name, c.Name], order.Select(col => col.Name));
        Assert.Empty(circular);
    }

    [Fact]
    public void Long_dependency_chains_evaluate_correctly()
    {
        var hours = AddColumn("Hours");
        var columns = new List<Column>();
        for (var i = 10; i >= 1; i--)
        {
            columns.Add(AddColumn($"Step {i}", formula: i == 1 ? "[Hours] + 1" : $"[Step {i - 1}] + 1"));
        }

        var task = AddItem("Task", null, (hours, "0"));

        Assert.Equal("10", ValueOf(task, columns[0]));
    }

    [Fact]
    public void Self_reference_is_circular()
    {
        var self = AddColumn("Self", formula: "[Self] + 1");
        var task = AddItem("Task");

        Assert.Equal(FormulaErrors.Circular, ValueOf(task, self));
    }

    [Fact]
    public void Reference_cycles_are_circular_and_IFERROR_cannot_hide_them()
    {
        var a = AddColumn("A", formula: "IFERROR([B], 0) + 1");
        var b = AddColumn("B", formula: "[C] + 1");
        var c = AddColumn("C", formula: "[A] + 1");
        var task = AddItem("Task");

        Assert.Equal(FormulaErrors.Circular, ValueOf(task, a));
        Assert.Equal(FormulaErrors.Circular, ValueOf(task, b));
        Assert.Equal(FormulaErrors.Circular, ValueOf(task, c));
        Assert.Equal(["A", "B", "C"], FormulaCalculator.EvaluationOrder(_columns).Circular.Select(col => col.Name));
    }

    [Fact]
    public void Columns_reading_a_cycle_get_its_error_but_are_not_themselves_circular()
    {
        var a = AddColumn("A", formula: "[B]");
        AddColumn("B", formula: "[A]");
        var reader = AddColumn("Reader", formula: "[A] + 1");
        var guarded = AddColumn("Guarded", formula: "IFERROR([A], \"fallback\")");
        var task = AddItem("Task");

        Assert.Equal(FormulaErrors.Circular, ValueOf(task, reader));
        Assert.Equal("fallback", ValueOf(task, guarded));
        Assert.DoesNotContain(FormulaCalculator.EvaluationOrder(_columns).Circular, col => col.Name == "Reader");
        Assert.Equal(FormulaErrors.Circular, ValueOf(task, a));
    }

    [Fact]
    public void Children_rolls_up_direct_children_only()
    {
        var hours = AddColumn("Hours");
        var total = AddColumn("Child hours", formula: "SUM(CHILDREN([Hours]))");
        var phase = AddItem("Phase");
        var task1 = AddItem("Task 1", phase, (hours, "8"));
        var task2 = AddItem("Task 2", phase, (hours, "4"));
        AddItem("Subtask", task2, (hours, "100"));
        var other = AddItem("Other", null, (hours, "1000"));

        Assert.Equal("12", ValueOf(phase, total));
        Assert.Equal("100", ValueOf(task2, total));
        Assert.Equal("0", ValueOf(task1, total));
        Assert.Equal("0", ValueOf(other, total));
    }

    [Fact]
    public void Children_of_a_formula_column_see_computed_values()
    {
        var hours = AddColumn("Hours");
        var rate = AddColumn("Rate");
        var rollup = AddColumn("Rollup", formula: "SUM(CHILDREN([Cost]))");
        AddColumn("Cost", formula: "[Hours] * [Rate]");
        var phase = AddItem("Phase");
        AddItem("Task 1", phase, (hours, "2"), (rate, "100"));
        AddItem("Task 2", phase, (hours, "3"), (rate, "50"));

        Assert.Equal("350", ValueOf(phase, rollup));
    }

    [Fact]
    public void A_column_can_roll_up_its_own_children_recursively()
    {
        var hours = AddColumn("Hours");
        var total = AddColumn("Total", formula: "[Hours] + SUM(CHILDREN([Total]))");
        var program = AddItem("Program", null, (hours, "1"));
        var phase = AddItem("Phase", program, (hours, "2"));
        AddItem("Task 1", phase, (hours, "3"));
        AddItem("Task 2", phase, (hours, "4"));
        AddItem("Task 3", program, (hours, "5"));

        Assert.Equal("15", ValueOf(program, total));
        Assert.Equal("9", ValueOf(phase, total));
        Assert.Empty(FormulaCalculator.EvaluationOrder(_columns).Circular);
    }

    [Fact]
    public void Children_roll_ups_work_regardless_of_item_list_order()
    {
        var hours = AddColumn("Hours");
        var total = AddColumn("Total", formula: "[Hours] + SUM(CHILDREN([Total]))");
        var parent = AddItem("Parent", null, (hours, "1"));
        var child = AddItem("Child", parent, (hours, "2"));
        AddItem("Grandchild", child, (hours, "3"));
        _items.Reverse();

        Assert.Equal("6", ValueOf(parent, total));
    }

    [Fact]
    public void Children_roll_up_dates_and_status_counts()
    {
        var status = AddColumn("Status", ColumnType.Dropdown);
        var latest = AddColumn("Latest finish", ColumnType.Date, "MAX(CHILDREN([Finish]))");
        var done = AddColumn("Done", formula: "COUNTIF(CHILDREN([Status]), \"Done\") & \" of \" & COUNT(CHILDREN([Title]))");
        var phase = AddItem("Phase");
        var a = AddItem("A", phase, (status, "Done"));
        a.Finish = new DateOnly(2026, 11, 1);
        var b = AddItem("B", phase, (status, "Blocked"));
        b.Finish = new DateOnly(2026, 12, 15);

        Assert.Equal("2026-12-15", ValueOf(phase, latest));
        Assert.Equal("1 of 2", ValueOf(phase, done));
    }

    [Fact]
    public void Built_in_fields_and_typed_cells_are_available()
    {
        var due = AddColumn("Due", ColumnType.Date);
        var check = AddColumn("Approved", ColumnType.Checkbox);
        var summary = AddColumn("Summary", ColumnType.Text,
            "[Title] & \": \" & DATEDIFF([Start], [Finish]) & \"d, \" & NETWORKDAYS([Start], [Due]) & \" workdays, \" & IF([Approved], \"approved\", \"pending\")");
        var task = AddItem("Build", null, (due, "2026-10-16"), (check, "true"));
        task.Start = new DateOnly(2026, 10, 12);
        task.Finish = new DateOnly(2026, 10, 22);

        Assert.Equal("Build: 10d, 5 workdays, approved", ValueOf(task, summary));
    }

    [Fact]
    public void Sheet_columns_shadow_built_in_names()
    {
        var title = AddColumn("Title", ColumnType.Text);
        var echo = AddColumn("Echo", ColumnType.Text, "[Title]");
        var task = AddItem("Item title", null, (title, "Column value"));

        Assert.Equal("Column value", ValueOf(task, echo));
    }

    [Fact]
    public void Missing_columns_are_ref_errors_and_bad_formulas_are_name_errors()
    {
        var missing = AddColumn("Missing", formula: "[Deleted column] * 2");
        var broken = AddColumn("Broken", formula: "1 +");
        var unknown = AddColumn("Unknown", formula: "FOO(1)");
        var readsBroken = AddColumn("Reads broken", formula: "IFERROR([Broken], -1)");
        var task = AddItem("Task");

        Assert.Equal(FormulaErrors.Ref, ValueOf(task, missing));
        Assert.Equal(FormulaErrors.Name, ValueOf(task, broken));
        Assert.Equal(FormulaErrors.Name, ValueOf(task, unknown));
        Assert.Equal("-1", ValueOf(task, readsBroken));
    }

    [Fact]
    public void Today_comes_from_the_caller()
    {
        var due = AddColumn("Due", ColumnType.Date);
        var overdue = AddColumn("Overdue", ColumnType.Checkbox, "[Due] < TODAY()");
        var late = AddItem("Late", null, (due, "2026-10-01"));
        var onTime = AddItem("On time", null, (due, "2026-10-30"));

        Assert.Equal("true", ValueOf(late, overdue));
        Assert.Equal("false", ValueOf(onTime, overdue));
    }

    [Fact]
    public void Orphaned_items_are_treated_as_top_level()
    {
        var hours = AddColumn("Hours");
        var total = AddColumn("Total", formula: "[Hours] + SUM(CHILDREN([Total]))");
        var orphan = new Item { Title = "Orphan", ParentId = Guid.NewGuid(), Values = new() { [hours.Id.ToString()] = "7" } };
        _items.Add(orphan);

        Assert.Equal("7", ValueOf(orphan, total));
    }
}
