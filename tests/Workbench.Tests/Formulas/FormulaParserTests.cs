using Workbench.Domain.Formulas;

namespace Workbench.Tests.Formulas;

public sealed class FormulaParserTests
{
    private static string Tree(string formula) => FormulaParser.Parse(formula).ToString();

    [Fact]
    public void Tokenizer_reads_every_token_kind()
    {
        var kinds = FormulaTokenizer.Tokenize("[Planned hours] 1.5 \"hi\" SUM + - * / ^ & = <> < <= > >= ( ) ,").Select(t => t.Kind);

        Assert.Equal(
        [
            TokenKind.Reference, TokenKind.Number, TokenKind.String, TokenKind.Identifier, TokenKind.Plus, TokenKind.Minus,
            TokenKind.Star, TokenKind.Slash, TokenKind.Caret, TokenKind.Ampersand, TokenKind.Equal, TokenKind.NotEqual,
            TokenKind.Less, TokenKind.LessOrEqual, TokenKind.Greater, TokenKind.GreaterOrEqual, TokenKind.LeftParen,
            TokenKind.RightParen, TokenKind.Comma, TokenKind.End,
        ], kinds);
    }

    [Fact]
    public void Tokenizer_keeps_reference_names_and_string_escapes()
    {
        var tokens = FormulaTokenizer.Tokenize("[ Planned hours ] \"say \"\"hi\"\"\" .25");

        Assert.Equal("Planned hours", tokens[0].Text);
        Assert.Equal("say \"hi\"", tokens[1].Text);
        Assert.Equal(0.25, tokens[2].Number);
    }

    [Theory]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("(1 + 2) * 3", "(* (+ 1 2) 3)")]
    [InlineData("2 * 3 ^ 2", "(* 2 (^ 3 2))")]
    [InlineData("2 ^ 3 ^ 2", "(^ (^ 2 3) 2)")]
    [InlineData("-2 ^ 2", "(^ (neg 2) 2)")]
    [InlineData("10 - 4 - 3", "(- (- 10 4) 3)")]
    [InlineData("8 / 4 / 2", "(/ (/ 8 4) 2)")]
    [InlineData("1 + 2 & \"x\"", "(& (+ 1 2) \"x\")")]
    [InlineData("\"a\" & \"b\" = \"ab\"", "(= (& \"a\" \"b\") \"ab\")")]
    [InlineData("[A] + 1 > [B] * 2", "(> (+ [A] 1) (* [B] 2))")]
    [InlineData("1 < 2 = TRUE", "(= (< 1 2) TRUE)")]
    [InlineData("--3", "(neg (neg 3))")]
    [InlineData("2 ^ -1", "(^ 2 (neg 1))")]
    public void Operator_precedence_and_associativity(string formula, string expected)
    {
        Assert.Equal(expected, Tree(formula));
    }

    [Fact]
    public void Parses_function_calls_with_nested_arguments()
    {
        Assert.Equal("IF((> [Budget] 100), \"High\", ROUND((/ [Budget] 3), 2))",
            Tree("if([Budget] > 100, \"High\", Round([Budget] / 3, 2))"));
    }

    [Fact]
    public void Parses_zero_argument_calls_and_booleans_case_insensitively()
    {
        Assert.Equal("(= TODAY() TRUE)", Tree("today() = true"));
        Assert.Equal("FALSE", Tree("False"));
    }

    [Fact]
    public void Leading_equals_sign_is_optional()
    {
        Assert.Equal(Tree("[A] * 2"), Tree("=[A] * 2"));
    }

    [Fact]
    public void Formula_collects_row_and_child_references()
    {
        var formula = Formula.Parse("[Hours] * [Rate] + SUM(CHILDREN([Total])) + [hours]");

        Assert.Equal(["Hours", "Rate"], formula.References.Order());
        Assert.Equal(["Total"], formula.ChildReferences);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("=", "empty")]
    [InlineData("1 +", "ends too early")]
    [InlineData("(1 + 2", "')'")]
    [InlineData("1 + 2)", "Unexpected ')'")]
    [InlineData("[Budget", "Missing ']'")]
    [InlineData("[]", "Empty column reference")]
    [InlineData("\"open", "closing quote")]
    [InlineData("Budget * 2", "square brackets, like [Budget]")]
    [InlineData("1 # 2", "Unexpected character '#'")]
    [InlineData("SUM(1,)", "expected a value")]
    [InlineData("SUM(1 2)", "',' or ')'")]
    [InlineData("CHILDREN(1)", "CHILDREN([Budget])")]
    [InlineData("CHILDREN()", "CHILDREN([Budget])")]
    [InlineData("1 2", "Unexpected '2'")]
    public void Syntax_errors_have_helpful_messages(string formula, string expectedFragment)
    {
        var ex = Assert.Throws<FormulaParseException>(() => FormulaParser.Parse(formula));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Parse_errors_report_position()
    {
        var ex = Assert.Throws<FormulaParseException>(() => FormulaParser.Parse("[A] + @"));

        Assert.Equal(6, ex.Position);
        Assert.Contains("position 7", ex.Message);
    }

    [Fact]
    public void Validate_accepts_known_columns_and_built_ins()
    {
        Assert.Null(Formula.Validate("IF([Start] > TODAY(), [title], SUM(CHILDREN([Budget])))", ["Budget"]));
    }

    [Theory]
    [InlineData("SUMM(1)", "Unknown function SUMM")]
    [InlineData("ROUND()", "ROUND takes 1 to 2 arguments, but was given 0")]
    [InlineData("IF(TRUE, 1, 2, 3)", "IF takes 2 to 3 arguments")]
    [InlineData("NOT(TRUE, FALSE)", "NOT takes 1 argument, but was given 2")]
    [InlineData("SUM()", "at least 1 argument")]
    [InlineData("1 + ABS(LEN())", "LEN takes 1 argument")]
    [InlineData("[Missing] + 1", "There is no column named [Missing]")]
    [InlineData("SUM(CHILDREN([Missing]))", "There is no column named [Missing]")]
    [InlineData("1 +", "ends too early")]
    public void Validate_reports_problems(string formula, string expectedFragment)
    {
        var problem = Formula.Validate(formula, ["Budget"]);

        Assert.NotNull(problem);
        Assert.Contains(expectedFragment, problem);
    }
}
