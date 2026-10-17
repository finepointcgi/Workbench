using Workbench.Domain.Formulas;
using static Workbench.Tests.Formulas.TestRow;

namespace Workbench.Tests.Formulas;

public sealed class FormulaFunctionTests
{
    /// <summary>A parent row whose children have hours 8, 60, 40 and one blank, statuses and finish dates.</summary>
    private static TestRow Parent() => new TestRow()
        .WithChildren("Hours", N(8), N(60), N(40), FormulaValue.Blank)
        .WithChildren("Status", T("Done"), T("In progress"), T("done"), FormulaValue.Blank)
        .WithChildren("Finish", D(2026, 1, 10), D(2026, 3, 1), D(2026, 2, 15))
        .WithChildren("Flag", B(true), B(true), FormulaValue.Blank)
        .WithChildren("Broken", N(1), FormulaValue.FromError(FormulaErrors.DivideByZero))
        .With("Empty", FormulaValue.Blank);

    [Theory]
    [InlineData("SUM(1, 2, 3)", "6")]
    [InlineData("SUM(CHILDREN([Hours]))", "108")]
    [InlineData("SUM(CHILDREN([Hours]), 2)", "110")]
    [InlineData("SUM(CHILDREN([Status]))", "0")]
    [InlineData("SUM(CHILDREN([Broken]))", FormulaErrors.DivideByZero)]
    [InlineData("SUM(CHILDREN([Empty]))", "0")]
    public void Sum(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Theory]
    [InlineData("AVG(1, 2, 4)", "2.33333333333333")]
    [InlineData("AVG(CHILDREN([Hours]))", "36")]
    [InlineData("AVG(CHILDREN([Empty]))", FormulaErrors.DivideByZero)]
    [InlineData("AVG(CHILDREN([Status]))", FormulaErrors.DivideByZero)]
    public void Avg(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Theory]
    [InlineData("MIN(3, 1, 2)", "1")]
    [InlineData("MAX(3, 1, 2)", "3")]
    [InlineData("MIN(CHILDREN([Hours]))", "8")]
    [InlineData("MAX(CHILDREN([Hours]))", "60")]
    [InlineData("MIN(CHILDREN([Finish]))", "2026-01-10")]
    [InlineData("MAX(CHILDREN([Finish]))", "2026-03-01")]
    [InlineData("MAX(CHILDREN([Empty]))", "0")]
    [InlineData("MAX(1, DATE(2026, 1, 1))", FormulaErrors.Value)]
    [InlineData("MIN(CHILDREN([Broken]))", FormulaErrors.DivideByZero)]
    public void Min_and_max(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Theory]
    [InlineData("COUNT(CHILDREN([Hours]))", "3")]
    [InlineData("COUNT(CHILDREN([Status]))", "3")]
    [InlineData("COUNT(CHILDREN([Broken]))", "1")]
    [InlineData("COUNT(1, \"\", \"a\")", "2")]
    public void Count_counts_non_blank_values(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Theory]
    [InlineData("COUNTIF(CHILDREN([Status]), \"Done\")", "2")]
    [InlineData("COUNTIF(CHILDREN([Status]), \"<>Done\")", "2")]
    [InlineData("COUNTIF(CHILDREN([Status]), \"\")", "1")]
    [InlineData("COUNTIF(CHILDREN([Hours]), \">10\")", "2")]
    [InlineData("COUNTIF(CHILDREN([Hours]), \">=40\")", "2")]
    [InlineData("COUNTIF(CHILDREN([Hours]), \"<= 8\")", "2")]
    [InlineData("COUNTIF(CHILDREN([Hours]), 60)", "1")]
    [InlineData("COUNTIF(CHILDREN([Hours]), \"=60\")", "1")]
    [InlineData("COUNTIF(CHILDREN([Finish]), \">2026-02-01\")", "2")]
    [InlineData("COUNTIF(CHILDREN([Flag]), TRUE)", "2")]
    [InlineData("COUNTIF(CHILDREN([Broken]), 1)", "1")]
    [InlineData("COUNTIF(5, 5)", "1")]
    [InlineData("COUNTIF(CHILDREN([Hours]), 1 / 0)", FormulaErrors.DivideByZero)]
    public void CountIf(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Theory]
    [InlineData("IF(TRUE, \"yes\", \"no\")", "yes")]
    [InlineData("IF(FALSE, \"yes\", \"no\")", "no")]
    [InlineData("IF(1 > 2, \"yes\")", null)]
    [InlineData("IF(0, 1, 2)", "2")]
    [InlineData("IF(\"true\", 1, 2)", "1")]
    [InlineData("IF(\"maybe\", 1, 2)", FormulaErrors.Value)]
    [InlineData("IF(TRUE, 1, 1 / 0)", "1")]
    [InlineData("IF(FALSE, 1 / 0, 2)", "2")]
    public void If_evaluates_only_the_chosen_branch(string formula, string? expected) => Assert.Equal(expected, Evaluate(formula));

    [Theory]
    [InlineData("AND(TRUE, TRUE)", "true")]
    [InlineData("AND(TRUE, FALSE)", "false")]
    [InlineData("AND(1, 2 > 1)", "true")]
    [InlineData("OR(FALSE, FALSE)", "false")]
    [InlineData("OR(FALSE, TRUE)", "true")]
    [InlineData("AND(CHILDREN([Flag]))", "false")]
    [InlineData("OR(CHILDREN([Flag]))", "true")]
    [InlineData("AND(TRUE, \"x\")", FormulaErrors.Value)]
    [InlineData("OR(TRUE, 1 / 0)", FormulaErrors.DivideByZero)]
    [InlineData("NOT(TRUE)", "false")]
    [InlineData("NOT(0)", "true")]
    [InlineData("NOT([Empty])", "true")]
    public void Logical(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Theory]
    [InlineData("ROUND(2.5)", "3")]
    [InlineData("ROUND(-2.5)", "-3")]
    [InlineData("ROUND(3.14159, 2)", "3.14")]
    [InlineData("ROUND(2.675, 2)", "2.68")]
    [InlineData("ROUND(1234.5, -2)", "1200")]
    [InlineData("ROUND(1250, -2)", "1300")]
    [InlineData("ROUND(1.5, 0.9)", "2")]
    [InlineData("ROUND(\"x\")", FormulaErrors.Value)]
    [InlineData("ABS(-4.5)", "4.5")]
    [InlineData("ABS(3)", "3")]
    [InlineData("ABS(\"x\")", FormulaErrors.Value)]
    public void Round_and_abs(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Fact]
    public void Today_uses_the_context_date()
    {
        var row = new TestRow { Today = new DateOnly(2026, 12, 31) };

        Assert.Equal("2026-12-31", row.Eval("TODAY()"));
        Assert.Equal("2027-01-07", row.Eval("TODAY(7)"));
        Assert.Equal("2026-12-30", row.Eval("TODAY() - 1"));
    }

    [Theory]
    [InlineData("DATE(2026, 2, 28)", "2026-02-28")]
    [InlineData("DATE(2028, 2, 29)", "2028-02-29")]
    [InlineData("DATE(2026, 2, 29)", FormulaErrors.Value)]
    [InlineData("DATE(2026, 13, 1)", FormulaErrors.Value)]
    [InlineData("DATE(0, 1, 1)", FormulaErrors.Value)]
    [InlineData("DATE(\"x\", 1, 1)", FormulaErrors.Value)]
    [InlineData("YEAR(DATE(2026, 7, 4))", "2026")]
    [InlineData("MONTH(DATE(2026, 7, 4))", "7")]
    [InlineData("DAY(DATE(2026, 7, 4))", "4")]
    [InlineData("YEAR(\"2025-12-31\")", "2025")]
    [InlineData("YEAR(42)", FormulaErrors.Value)]
    [InlineData("MONTH(\"soon\")", FormulaErrors.Value)]
    public void Date_parts(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Fact]
    public void Date_parts_of_blank_are_value_errors()
    {
        Assert.Equal(FormulaErrors.Value, Parent().Eval("DAY([Empty])"));
    }

    [Theory]
    // 2026-10-12 is a Monday.
    [InlineData("NETWORKDAYS(DATE(2026, 10, 12), DATE(2026, 10, 16))", "5")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 12), DATE(2026, 10, 18))", "5")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 12), DATE(2026, 10, 19))", "6")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 17), DATE(2026, 10, 18))", "0")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 14), DATE(2026, 10, 14))", "1")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 16), DATE(2026, 10, 12))", "-5")]
    [InlineData("NETWORKDAYS(DATE(2026, 1, 1), DATE(2026, 12, 31))", "261")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 12), DATE(2026, 10, 16), DATE(2026, 10, 14))", "4")]
    [InlineData("NETWORKDAYS(DATE(2026, 10, 12), DATE(2026, 10, 16), DATE(2026, 10, 17))", "5")]
    [InlineData("NETWORKDAYS(\"2026-10-12\", \"2026-10-16\")", "5")]
    [InlineData("NETWORKDAYS(1, DATE(2026, 10, 16))", FormulaErrors.Value)]
    public void NetworkDays(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Fact]
    public void NetworkDays_accepts_a_holiday_list()
    {
        var row = new TestRow().WithChildren("Holiday", D(2026, 10, 13), D(2026, 10, 15), FormulaValue.Blank);

        Assert.Equal("3", row.Eval("NETWORKDAYS(DATE(2026, 10, 12), DATE(2026, 10, 16), CHILDREN([Holiday]))"));
    }

    [Theory]
    [InlineData("DATEDIFF(DATE(2026, 1, 1), DATE(2026, 3, 1))", "59")]
    [InlineData("DATEDIFF(DATE(2026, 3, 1), DATE(2026, 1, 1))", "-59")]
    [InlineData("DATEDIFF(\"2028-02-28\", \"2028-03-01\")", "2")]
    [InlineData("DATEDIFF(1, 2)", FormulaErrors.Value)]
    public void DateDiff(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Theory]
    [InlineData("LEN(\"hello\")", "5")]
    [InlineData("LEN(\"\")", "0")]
    [InlineData("LEN(12.5)", "4")]
    [InlineData("LEFT(\"hello\", 2)", "he")]
    [InlineData("LEFT(\"hello\")", "h")]
    [InlineData("LEFT(\"hi\", 10)", "hi")]
    [InlineData("LEFT(\"hi\", 0)", "")]
    [InlineData("LEFT(\"hi\", -1)", FormulaErrors.Value)]
    [InlineData("RIGHT(\"hello\", 3)", "llo")]
    [InlineData("RIGHT(\"hello\")", "o")]
    [InlineData("RIGHT(1001.01, 2)", "01")]
    [InlineData("CONCATENATE(\"a\", 1, TRUE, DATE(2026, 1, 2))", "a1true2026-01-02")]
    [InlineData("CONCATENATE(\"x\", 1 / 0)", FormulaErrors.DivideByZero)]
    public void Text_functions(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Fact]
    public void Concatenate_joins_children()
    {
        Assert.Equal("DoneIn progressdone", Parent().Eval("CONCATENATE(CHILDREN([Status]))"));
    }

    [Theory]
    [InlineData("ISBLANK([Empty])", "true")]
    [InlineData("ISBLANK(\"\")", "true")]
    [InlineData("ISBLANK(0)", "false")]
    [InlineData("ISBLANK(\"x\")", "false")]
    [InlineData("ISBLANK(1 / 0)", "false")]
    [InlineData("IFERROR(1 / 0, \"n/a\")", "n/a")]
    [InlineData("IFERROR(10 / 4, \"n/a\")", "2.5")]
    [InlineData("IFERROR([Missing], 0)", "0")]
    [InlineData("IFERROR(NOPE(), 1)", "1")]
    [InlineData("IFERROR(1 / 0, 1 / 0)", FormulaErrors.DivideByZero)]
    public void IsBlank_and_IfError(string formula, string expected) => Assert.Equal(expected, Parent().Eval(formula));

    [Fact]
    public void Function_names_are_case_insensitive()
    {
        Assert.Equal("6", Evaluate("sum(1, 2, Round(2.6))"));
    }

    [Fact]
    public void Every_documented_function_is_registered()
    {
        string[] expected =
        [
            "SUM", "AVG", "MIN", "MAX", "COUNT", "COUNTIF", "IF", "AND", "OR", "NOT", "ROUND", "ABS", "TODAY", "DATE",
            "NETWORKDAYS", "YEAR", "MONTH", "DAY", "LEN", "LEFT", "RIGHT", "CONCATENATE", "ISBLANK", "IFERROR", "DATEDIFF", "CHILDREN",
        ];

        Assert.Equal(expected.Order(), FormulaFunctions.Names.Order());
    }

    [Fact]
    public void Wrong_argument_count_is_a_value_error_at_runtime()
    {
        Assert.Equal(FormulaErrors.Value, Evaluate("LEFT(\"a\", 1, 2)"));
        Assert.Equal(FormulaErrors.Value, Evaluate("TODAY(1, 2)"));
    }
}
