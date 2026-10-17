using Workbench.Domain;
using Workbench.Domain.Formulas;
using static Workbench.Tests.Formulas.TestRow;

namespace Workbench.Tests.Formulas;

public sealed class FormulaEvaluatorTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("2 * 3 ^ 2", "18")]
    [InlineData("2 ^ 3 ^ 2", "64")]
    [InlineData("-2 ^ 2", "4")]
    [InlineData("10 - 4 - 3", "3")]
    [InlineData("8 / 4 / 2", "1")]
    [InlineData("7 / 2", "3.5")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("1 / 3", "0.333333333333333")]
    [InlineData("-(3 - 5)", "2")]
    [InlineData("+4", "4")]
    [InlineData("2 ^ -1", "0.5")]
    [InlineData("0 * -1", "0")]
    [InlineData("1234567.5 * 2", "2469135")]
    public void Arithmetic(string formula, string expected)
    {
        Assert.Equal(expected, Evaluate(formula));
    }

    [Theory]
    [InlineData("\"a\" & \"b\"", "ab")]
    [InlineData("1 + 2 & \"x\"", "3x")]
    [InlineData("\"Total: \" & 2.50", "Total: 2.5")]
    [InlineData("\"Done: \" & TRUE", "Done: true")]
    [InlineData("\"On \" & DATE(2026, 1, 5)", "On 2026-01-05")]
    public void Concatenation_formats_invariantly(string formula, string expected)
    {
        Assert.Equal(expected, Evaluate(formula));
    }

    [Theory]
    [InlineData("1 = 1", "true")]
    [InlineData("1 <> 1", "false")]
    [InlineData("1 < 2", "true")]
    [InlineData("2 <= 2", "true")]
    [InlineData("3 > 2", "true")]
    [InlineData("2 >= 3", "false")]
    [InlineData("\"abc\" = \"ABC\"", "true")]
    [InlineData("\"apple\" < \"banana\"", "true")]
    [InlineData("DATE(2026, 1, 1) < DATE(2026, 2, 1)", "true")]
    [InlineData("DATE(2026, 1, 1) = \"2026-01-01\"", "true")]
    [InlineData("5 = \"5\"", "true")]
    [InlineData("TRUE > FALSE", "true")]
    [InlineData("1 = \"one\"", "false")]
    [InlineData("1 <> \"one\"", "true")]
    [InlineData("1 < \"one\"", "#VALUE!")]
    [InlineData("1 + 1 = 2", "true")]
    public void Comparisons(string formula, string expected)
    {
        Assert.Equal(expected, Evaluate(formula));
    }

    [Fact]
    public void Blank_acts_as_zero_empty_text_or_false()
    {
        var row = new TestRow().With("Empty", FormulaValue.Blank).With("Due", D(2026, 1, 1));

        Assert.Equal("5", row.Eval("[Empty] + 5"));
        Assert.Equal("x", row.Eval("[Empty] & \"x\""));
        Assert.Equal("true", row.Eval("[Empty] = 0"));
        Assert.Equal("true", row.Eval("[Empty] = \"\""));
        Assert.Equal("true", row.Eval("[Empty] = FALSE"));
        Assert.Equal("true", row.Eval("[Empty] < [Due]"));
        Assert.Equal("false", row.Eval("[Empty] = [Due]"));
    }

    [Fact]
    public void Date_arithmetic()
    {
        var row = new TestRow().With("Start", D(2026, 3, 30)).With("Finish", D(2026, 4, 2));

        Assert.Equal("2026-04-04", row.Eval("[Start] + 5"));
        Assert.Equal("2026-04-04", row.Eval("5 + [Start]"));
        Assert.Equal("2026-03-28", row.Eval("[Start] - 2"));
        Assert.Equal("3", row.Eval("[Finish] - [Start]"));
        Assert.Equal("#VALUE!", row.Eval("[Start] + [Finish]"));
        Assert.Equal("#VALUE!", row.Eval("1 - [Start]"));
        Assert.Equal("#VALUE!", row.Eval("[Start] * 2"));
    }

    [Theory]
    [InlineData("1 / 0", FormulaErrors.DivideByZero)]
    [InlineData("0 ^ -1", FormulaErrors.DivideByZero)]
    [InlineData("\"abc\" * 2", FormulaErrors.Value)]
    [InlineData("-\"abc\"", FormulaErrors.Value)]
    [InlineData("(-8) ^ 0.5", FormulaErrors.Value)]
    [InlineData("10 ^ 400", FormulaErrors.Value)]
    [InlineData("[Nope]", FormulaErrors.Ref)]
    [InlineData("SUM(CHILDREN([Nope]))", FormulaErrors.Ref)]
    [InlineData("NOPE(1)", FormulaErrors.Name)]
    [InlineData("ROUND()", FormulaErrors.Value)]
    [InlineData("\"5\" * 2", "10")]
    public void Errors(string formula, string expected)
    {
        Assert.Equal(expected, Evaluate(formula));
    }

    [Theory]
    [InlineData("1 / 0 + 1")]
    [InlineData("1 + 1 / 0")]
    [InlineData("\"x\" & 1 / 0")]
    [InlineData("1 / 0 = 1")]
    [InlineData("-(1 / 0)")]
    [InlineData("ABS(1 / 0)")]
    [InlineData("SUM(1, 1 / 0)")]
    [InlineData("IF(1 / 0, 1, 2)")]
    public void Errors_propagate(string formula)
    {
        Assert.Equal(FormulaErrors.DivideByZero, Evaluate(formula));
    }

    [Fact]
    public void Leftmost_error_wins()
    {
        Assert.Equal(FormulaErrors.Ref, Evaluate("[Missing] + 1 / 0"));
        Assert.Equal(FormulaErrors.DivideByZero, Evaluate("1 / 0 + [Missing]"));
    }

    [Fact]
    public void Bare_children_list_is_a_value_error()
    {
        var row = new TestRow().WithChildren("Hours", N(1), N(2));

        Assert.Equal(FormulaErrors.Value, row.Eval("CHILDREN([Hours])"));
        Assert.Equal(FormulaErrors.Value, row.Eval("CHILDREN([Hours]) + 1"));
        Assert.Equal(FormulaErrors.Value, row.Eval("IF(TRUE, CHILDREN([Hours]))"));
    }

    [Fact]
    public void References_read_typed_values_case_insensitively()
    {
        var row = new TestRow().With("Planned hours", N(8)).With("Budget", N(1200)).With("Title", T("Kickoff"));

        Assert.Equal("150", row.Eval("[Budget] / [planned HOURS]"));
        Assert.Equal("Kickoff!", row.Eval("[Title] & \"!\""));
    }

    [Theory]
    [InlineData(null, ColumnType.Number, FormulaValueKind.Blank)]
    [InlineData("  ", ColumnType.Text, FormulaValueKind.Blank)]
    [InlineData("1234.5", ColumnType.Currency, FormulaValueKind.Number)]
    [InlineData("n/a", ColumnType.Number, FormulaValueKind.Text)]
    [InlineData("2026-10-17", ColumnType.Date, FormulaValueKind.Date)]
    [InlineData("10/17/2026", ColumnType.Date, FormulaValueKind.Text)]
    [InlineData("true", ColumnType.Checkbox, FormulaValueKind.Boolean)]
    [InlineData("42", ColumnType.Text, FormulaValueKind.Text)]
    [InlineData("Done", ColumnType.Dropdown, FormulaValueKind.Text)]
    public void Cell_strings_are_read_by_column_type(string? raw, ColumnType type, FormulaValueKind expected)
    {
        Assert.Equal(expected, FormulaValue.FromCell(raw, type).Kind);
    }

    [Fact]
    public void Values_format_with_invariant_conventions()
    {
        Assert.Equal("1234.5", N(1234.5).ToCellString());
        Assert.Equal("-0.25", N(-0.25).ToCellString());
        Assert.Equal("2026-01-05", D(2026, 1, 5).ToCellString());
        Assert.Equal("true", B(true).ToCellString());
        Assert.Equal("false", B(false).ToCellString());
        Assert.Null(FormulaValue.Blank.ToCellString());
        Assert.Equal("#REF!", FormulaValue.FromError(FormulaErrors.Ref).ToCellString());
    }

    [Fact]
    public void Formatting_ignores_the_current_culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2.5", Evaluate("5 / 2"));
            Assert.Equal("1.5", FormulaValue.FromCell("1.5", ColumnType.Number).ToCellString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
