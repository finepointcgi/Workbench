using Workbench.Application.Formulas;
using Workbench.Domain;
using Workbench.Domain.Formulas;

namespace Workbench.Tests.Formulas;

/// <summary>Ranges, cross-sheet references, column renames and plain-language messages.</summary>
public sealed class FormulaReferenceTests
{
    [Theory]
    [InlineData("[Status]:[Status]", "[Status]:[Status]")]
    [InlineData("COUNTIF([Status]:[Status], \"Done\")", "COUNTIF([Status]:[Status], \"Done\")")]
    [InlineData("{Open risks}", "{Open risks}")]
    [InlineData("[Hours]@row * 2", "(* [Hours] 2)")]
    [InlineData("COUNTIFS([Due]:[Due], @cell < TODAY())", "COUNTIFS([Due]:[Due], (< @cell TODAY()))")]
    public void Parses_ranges_references_and_cells(string formula, string tree) =>
        Assert.Equal(tree, FormulaParser.Parse(formula).ToString());

    [Theory]
    [InlineData("{Open", "Missing '}'")]
    [InlineData("{ }", "Empty sheet reference")]
    [InlineData("[A]:", "a column after ':'")]
    public void Reference_syntax_errors_explain_themselves(string formula, string fragment) =>
        Assert.Contains(fragment, Assert.Throws<FormulaParseException>(() => FormulaParser.Parse(formula)).Message);

    [Fact]
    public void Collects_every_kind_of_reference()
    {
        var formula = Formula.Parse("[A] + SUM(CHILDREN([B])) + COUNTIF([C]:[D], 1) + COUNT({Risks})");

        Assert.Equal(["A"], formula.References);
        Assert.Equal(["B"], formula.ChildReferences);
        Assert.Equal([("C", "D")], formula.ColumnRanges);
        Assert.Equal(["Risks"], formula.SheetReferences);
        Assert.Equal(["A", "B", "C", "D"], formula.AllColumnNames.Order());
    }

    [Theory]
    [InlineData("[Budget] / [Planned hours]", "[Cost] / [Planned hours]")]
    [InlineData("SUM(CHILDREN([budget])) + [ Budget ]@row", "SUM(CHILDREN([Cost])) + [Cost]@row")]
    [InlineData("SUMIF([Budget]:[Budget], \">0\")", "SUMIF([Cost]:[Cost], \">0\")")]
    [InlineData("\"[Budget]\" & [Budgeted]", "\"[Budget]\" & [Budgeted]")]
    [InlineData("[Budget] +", "[Cost] +")]
    [InlineData("\"unterminated [Budget]", "\"unterminated [Budget]")]
    public void Renaming_a_column_rewrites_only_its_references(string before, string after) =>
        Assert.Equal(after, Formula.RenameColumn(before, "Budget", "Cost"));

    [Fact]
    public void Validation_suggests_the_closest_name()
    {
        Assert.Contains("Did you mean [Budget]?", Formula.Validate("[Budgte] * 2", ["Budget", "Hours"]));
        Assert.Contains("Did you mean SUM?", Formula.Validate("SUMM(1)", []));
        Assert.Contains("Did you mean COUNTIFS?", Formula.Validate("COUNTIFFS([A]:[A], 1)", ["A"]));
        Assert.Contains("There is no column named [Owner]", Formula.Validate("COUNTIF([Owner]:[Owner], 1)", ["A"]));
    }

    [Fact]
    public void Validation_checks_sheet_reference_names_when_given()
    {
        Assert.Null(Formula.Validate("COUNT({Risks})", [], ["risks"]));
        Assert.Null(Formula.Validate("COUNT({Risks})", []));
        var problem = Formula.Validate("COUNT({Riskz})", [], ["Risks"]);
        Assert.Contains("no cross-sheet reference named {Riskz}", problem);
        Assert.Contains("Did you mean {Risks}?", problem);
    }

    [Theory]
    [InlineData(FormulaErrors.Ref)]
    [InlineData(FormulaErrors.DivideByZero)]
    [InlineData(FormulaErrors.Value)]
    [InlineData(FormulaErrors.Name)]
    [InlineData(FormulaErrors.Circular)]
    [InlineData(FormulaErrors.NoMatch)]
    [InlineData(FormulaErrors.NoAccess)]
    public void Every_error_has_a_plain_explanation(string code)
    {
        Assert.True(FormulaErrors.IsError(code));
        Assert.DoesNotContain("#", FormulaErrors.Explain(code));
    }

    // ---- Calculator ----

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

    private IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string?>> Calculate(FormulaInputs? inputs = null) =>
        FormulaCalculator.Calculate(_columns, _items, new DateOnly(2026, 10, 14), inputs);

    [Fact]
    public void Column_ranges_read_every_row_in_outline_order()
    {
        var status = AddColumn("Status", ColumnType.Dropdown);
        var open = AddColumn("Open count", formula: "COUNTIF([Status]:[Status], \"Open\")");
        var first = AddColumn("First title", ColumnType.Text, "INDEX([Title]:[Title], 1)");
        var phase = AddItem("Phase", null, (status, "Open"));
        AddItem("Child", phase, (status, "Done"));
        AddItem("Second", null, (status, "Open"));

        var result = Calculate();

        Assert.All(_items, i => Assert.Equal("2", result[i.Id][open.Id]));
        Assert.All(_items, i => Assert.Equal("Phase", result[i.Id][first.Id]));
    }

    [Fact]
    public void A_range_over_a_formula_column_sees_it_finished_on_every_row()
    {
        var hours = AddColumn("Hours");
        var cost = AddColumn("Cost", formula: "[Hours] * 100");
        var share = AddColumn("Share", formula: "[Cost] / SUM([Cost]:[Cost])");
        var a = AddItem("A", null, (hours, "1"));
        var b = AddItem("B", null, (hours, "3"));

        var result = Calculate();

        Assert.Equal("0.25", result[a.Id][share.Id]);
        Assert.Equal("0.75", result[b.Id][share.Id]);
        Assert.Equal("300", result[b.Id][cost.Id]);
    }

    [Fact]
    public void A_column_ranging_over_itself_is_circular()
    {
        var total = AddColumn("Total", formula: "SUM([Total]:[Total])");
        var other = AddColumn("Other", formula: "[Loop] + 1");
        var loop = AddColumn("Loop", formula: "COUNT([Other]:[Other])");
        var task = AddItem("Task");

        var result = Calculate();

        Assert.Equal(FormulaErrors.Circular, result[task.Id][total.Id]);
        Assert.Equal(FormulaErrors.Circular, result[task.Id][loop.Id]);
        Assert.Equal(FormulaErrors.Circular, result[task.Id][other.Id]);
    }

    [Fact]
    public void Multi_column_ranges_follow_column_order_and_vlookup_reads_them()
    {
        var code = AddColumn("Code", ColumnType.Text);
        var rate = AddColumn("Rate");
        var lookup = AddColumn("PM rate", formula: "VLOOKUP(\"PM\", [Code]:[Rate], 2, FALSE)");
        AddItem("Row 1", null, (code, "ENG"), (rate, "95"));
        var pm = AddItem("Row 2", null, (code, "PM"), (rate, "150"));

        Assert.Equal("150", Calculate()[pm.Id][lookup.Id]);
    }

    [Fact]
    public void Sheet_references_and_lookup_types_come_from_the_inputs()
    {
        var looked = AddColumn("Looked-up due", ColumnType.Lookup);
        var late = AddColumn("Late", ColumnType.Checkbox, "[Looked-up due] < TODAY()");
        var risks = AddColumn("Open risks", formula: "COUNTIF({Risk status}, \"Open\")");
        var task = AddItem("Task", null, (looked, "2026-10-01"));

        var result = Calculate(new FormulaInputs(
            new Dictionary<string, FormulaValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Risk status"] = FormulaValue.FromList([FormulaValue.FromText("Open"), FormulaValue.FromText("Open")]),
            },
            new Dictionary<Guid, ColumnType> { [looked.Id] = ColumnType.Date }));

        Assert.Equal("true", result[task.Id][late.Id]);
        Assert.Equal("2", result[task.Id][risks.Id]);
    }
}
