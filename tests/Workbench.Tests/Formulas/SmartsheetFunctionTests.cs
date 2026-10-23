using Workbench.Domain.Formulas;
using static Workbench.Tests.Formulas.TestRow;

namespace Workbench.Tests.Formulas;

/// <summary>The Smartsheet-style functions added in wave E: conditional counts and sums, lookups, dates and text.</summary>
public sealed class SmartsheetFunctionTests
{
    /// <summary>Four rows: status, owner, hours, due date.</summary>
    private static TestRow Sheet() => new TestRow()
        .WithRange("Status", T("Done"), T("Open"), T("done"), FormulaValue.Blank)
        .WithRange("Owner", T("Sam"), T("Pat"), T("Sam"), T("Pat"))
        .WithRange("Hours", N(8), N(60), N(40), N(2))
        .WithRange("Due", D(2026, 10, 1), D(2026, 10, 20), D(2026, 11, 2), FormulaValue.Blank);

    [Theory]
    [InlineData("COUNTIFS([Status]:[Status], \"Done\")", "2")]
    [InlineData("COUNTIFS([Status]:[Status], \"Done\", [Owner]:[Owner], \"Sam\")", "2")]
    [InlineData("COUNTIFS([Owner]:[Owner], \"Pat\", [Hours]:[Hours], \">10\")", "1")]
    [InlineData("COUNTIFS([Due]:[Due], @cell < TODAY())", "1")]
    [InlineData("COUNTIFS([Due]:[Due], ISBLANK(@cell))", "1")]
    [InlineData("COUNTIFS([Status]:[Status], \"<>Done\", [Owner]:[Owner], \"Pat\")", "2")]
    [InlineData("COUNTIF([Status]:[Status], \"done\")", "2")]
    [InlineData("COUNTIF([Hours]@row, 1)", "0")]
    [InlineData("COUNTIF([Hours]:[Hours], \"<10\")", "2")]
    public void Countifs(string formula, string expected) => Assert.Equal(expected, Sheet().With("Hours", N(8)).Eval(formula));

    [Theory]
    [InlineData("SUMIF([Owner]:[Owner], \"Sam\", [Hours]:[Hours])", "48")]
    [InlineData("SUMIF([Hours]:[Hours], \">10\")", "100")]
    [InlineData("SUMIFS([Hours]:[Hours], [Owner]:[Owner], \"Pat\", [Status]:[Status], \"Open\")", "60")]
    [InlineData("SUMIFS([Hours]:[Hours], [Due]:[Due], @cell >= DATE(2026, 10, 15))", "100")]
    [InlineData("AVERAGEIF([Owner]:[Owner], \"Sam\", [Hours]:[Hours])", "24")]
    [InlineData("AVERAGEIFS([Hours]:[Hours], [Owner]:[Owner], \"Nobody\")", FormulaErrors.DivideByZero)]
    [InlineData("MAX(COLLECT([Due]:[Due], [Owner]:[Owner], \"Sam\"))", "2026-11-02")]
    [InlineData("JOIN(COLLECT([Owner]:[Owner], [Hours]:[Hours], \">=40\"))", "Pat, Sam")]
    [InlineData("COUNT(DISTINCT([Owner]:[Owner]))", "2")]
    [InlineData("COUNTBLANK([Status]:[Status])", "1")]
    [InlineData("SUMIFS([Hours]:[Hours], [Owner]:[Owner])", FormulaErrors.Value)]
    public void Sums_and_averages_with_conditions(string formula, string expected) => Assert.Equal(expected, Sheet().Eval(formula));

    [Fact]
    public void Ranges_of_different_sizes_are_value_errors()
    {
        var row = Sheet().WithRange("Short", N(1));

        Assert.Equal(FormulaErrors.Value, row.Eval("COUNTIFS([Owner]:[Owner], \"Sam\", [Short]:[Short], 1)"));
        Assert.Equal(FormulaErrors.Value, row.Eval("SUMIF([Owner]:[Owner], \"Sam\", [Short]:[Short])"));
    }

    [Theory]
    [InlineData("INDEX([Owner]:[Owner], 2)", "Pat")]
    [InlineData("INDEX([Owner]:[Owner], MATCH(60, [Hours]:[Hours], 0))", "Pat")]
    [InlineData("MATCH(\"sam\", [Owner]:[Owner], 0)", "1")]
    [InlineData("MATCH(45, [Hours]:[Hours])", "3")]
    [InlineData("MATCH(45, [Hours]:[Hours], -1)", "2")]
    [InlineData("MATCH(\"Nobody\", [Owner]:[Owner], 0)", FormulaErrors.NoMatch)]
    [InlineData("INDEX([Owner]:[Owner], 9)", FormulaErrors.Value)]
    [InlineData("IFERROR(INDEX([Owner]:[Owner], MATCH(1000, [Hours]:[Hours], 0)), \"none\")", "none")]
    public void Index_and_match(string formula, string expected) => Assert.Equal(expected, Sheet().Eval(formula));

    [Fact]
    public void Vlookup_reads_a_multi_column_range()
    {
        var row = new TestRow();
        row.Ranges["Code:Rate"] = FormulaValue.FromTable(
            [T("ENG1"), T("Engineer I"), N(95), T("ENG2"), T("Engineer II"), N(120), T("PM"), T("Project manager"), N(150)], 3);

        Assert.Equal("120", row.Eval("VLOOKUP(\"eng2\", [Code]:[Rate], 3, FALSE)"));
        Assert.Equal("Project manager", row.Eval("VLOOKUP(\"PM\", [Code]:[Rate], 2, false)"));
        Assert.Equal(FormulaErrors.NoMatch, row.Eval("VLOOKUP(\"QA\", [Code]:[Rate], 2, FALSE)"));
        Assert.Equal(FormulaErrors.Value, row.Eval("VLOOKUP(\"PM\", [Code]:[Rate], 4, FALSE)"));
        Assert.Equal("150", row.Eval("INDEX([Code]:[Rate], 3, 3)"));
    }

    [Fact]
    public void Cross_sheet_references_read_like_ranges()
    {
        var row = new TestRow();
        row.References["Risk status"] = FormulaValue.FromList([T("Open"), T("Closed"), T("Open")]);
        row.References["Hidden"] = FormulaValue.FromError(FormulaErrors.NoAccess);

        Assert.Equal("2", row.Eval("COUNTIF({Risk status}, \"Open\")"));
        Assert.Equal(FormulaErrors.NoAccess, row.Eval("COUNTIF({Hidden}, \"Open\")"));
        Assert.Equal(FormulaErrors.Ref, row.Eval("SUM({Missing})"));
    }

    [Theory]
    [InlineData("WORKDAY(DATE(2026, 10, 16), 1)", "2026-10-19")]
    [InlineData("WORKDAY(DATE(2026, 10, 14), 5)", "2026-10-21")]
    [InlineData("WORKDAY(DATE(2026, 10, 19), -1)", "2026-10-16")]
    [InlineData("WORKDAY(DATE(2026, 10, 14), 2, DATE(2026, 10, 15))", "2026-10-19")]
    [InlineData("NETDAYS(DATE(2026, 10, 14), DATE(2026, 10, 14))", "1")]
    [InlineData("NETDAYS(DATE(2026, 10, 1), DATE(2026, 10, 31))", "31")]
    [InlineData("NETWORKDAY(DATE(2026, 10, 12), DATE(2026, 10, 16))", "5")]
    [InlineData("WEEKDAY(DATE(2026, 10, 18))", "1")]
    [InlineData("WEEKDAY(TODAY())", "4")]
    [InlineData("WEEKNUMBER(DATE(2026, 1, 1))", "1")]
    [InlineData("YEARDAY(DATE(2026, 2, 1))", "32")]
    [InlineData("WORKDAY(\"soon\", 1)", FormulaErrors.Value)]
    public void Dates(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Theory]
    [InlineData("MID(\"Workbench\", 5, 5)", "bench")]
    [InlineData("MID(\"abc\", 9, 2)", "")]
    [InlineData("FIND(\"b\", \"abcb\")", "2")]
    [InlineData("FIND(\"b\", \"abcb\", 3)", "4")]
    [InlineData("FIND(\"B\", \"abc\")", "0")]
    [InlineData("UPPER(\"Mixed\")", "MIXED")]
    [InlineData("LOWER(\"Mixed\")", "mixed")]
    [InlineData("TRIM(\"  two   words \")", "two words")]
    [InlineData("SUBSTITUTE(\"a-b-c\", \"-\", \"/\")", "a/b/c")]
    [InlineData("SUBSTITUTE(\"a-b-c\", \"-\", \"/\", 2)", "a-b/c")]
    [InlineData("REPLACE(\"2026-Q1\", 6, 2, \"Q2\")", "2026-Q2")]
    [InlineData("VALUE(\"$1,200.50\")", "1200.5")]
    [InlineData("VALUE(\"(40)\")", "-40")]
    [InlineData("VALUE(\"15%\")", "0.15")]
    [InlineData("VALUE(\"many\")", FormulaErrors.Value)]
    [InlineData("CONTAINS(\"risk\", \"High RISK item\")", "true")]
    [InlineData("HAS(\"Red, Green\", \"green\")", "true")]
    [InlineData("HAS(\"Red, Green\", \"Gre\")", "false")]
    public void Text(string formula, string expected) => Assert.Equal(expected, Evaluate(formula));

    [Theory]
    [InlineData("ISERROR(1 / 0)", "true")]
    [InlineData("ISNUMBER(4)", "true")]
    [InlineData("ISNUMBER(\"4\")", "false")]
    [InlineData("ISTEXT(\"x\")", "true")]
    [InlineData("ISDATE(TODAY())", "true")]
    [InlineData("ISBOOLEAN(FALSE)", "true")]
    [InlineData("AVERAGE(1, 2, 3)", "2")]
    [InlineData("MEDIAN(5, 1, 3, 2)", "2.5")]
    [InlineData("LARGE([Hours]:[Hours], 2)", "40")]
    [InlineData("SMALL([Hours]:[Hours], 1)", "2")]
    [InlineData("PRODUCT(2, 3, 4)", "24")]
    [InlineData("ROUNDUP(2.01, 1)", "2.1")]
    [InlineData("ROUNDUP(-2.01)", "-3")]
    [InlineData("ROUNDDOWN(2.99, 1)", "2.9")]
    [InlineData("INT(-2.5)", "-3")]
    [InlineData("MOD(10, 3)", "1")]
    [InlineData("MOD(-1, 3)", "2")]
    [InlineData("MOD(1, 0)", FormulaErrors.DivideByZero)]
    [InlineData("CEILING(7, 5)", "10")]
    [InlineData("FLOOR(7, 5)", "5")]
    [InlineData("SQRT(16)", "4")]
    [InlineData("SQRT(-1)", FormulaErrors.Value)]
    public void Math_and_tests(string formula, string expected) => Assert.Equal(expected, Sheet().Eval(formula));

    [Fact]
    public void At_cell_outside_a_criterion_is_reported_when_saving()
    {
        var problem = Formula.Validate("@cell + 1", []);

        Assert.NotNull(problem);
        Assert.Contains("only works inside a criterion", problem);
        Assert.Null(Formula.Validate("COUNTIFS([Due]:[Due], @cell < TODAY())", ["Due"]));
    }

    [Fact]
    public void Every_function_has_help()
    {
        Assert.All(FormulaFunctions.Catalog, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Description));
            Assert.StartsWith(f.Name + "(", f.Syntax);
        });
        Assert.Equal(FormulaFunctions.Names.Count(), FormulaFunctions.Catalog.Count);
    }
}
