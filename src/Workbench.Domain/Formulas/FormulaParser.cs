namespace Workbench.Domain.Formulas;

/// <summary>
/// Recursive-descent parser. Precedence, lowest to highest (all binary operators are left-associative, as in Excel):
/// comparisons (= &lt;&gt; &lt; &lt;= &gt; &gt;=), &amp;, + -, * /, ^, unary - +.
/// Unary minus binds tighter than ^, so <c>-2^2</c> is 4, matching Excel.
/// </summary>
public sealed class FormulaParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _index;

    private FormulaParser(IReadOnlyList<Token> tokens) => _tokens = tokens;

    private Token Current => _tokens[_index];

    /// <summary>Parses formula text. A leading <c>=</c> is optional.</summary>
    public static FormulaNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = FormulaTokenizer.Tokenize(text);
        var parser = new FormulaParser(tokens);
        if (parser.Current.Kind == TokenKind.Equal)
        {
            parser._index++;
        }

        if (parser.Current.Kind == TokenKind.End)
        {
            throw new FormulaParseException("The formula is empty.", parser.Current.Position);
        }

        var root = parser.ParseComparison();
        if (parser.Current.Kind != TokenKind.End)
        {
            throw parser.Unexpected("an operator or the end of the formula");
        }

        return root;
    }

    private FormulaNode ParseComparison()
    {
        var left = ParseConcatenation();
        while (true)
        {
            BinaryOperator? op = Current.Kind switch
            {
                TokenKind.Equal => BinaryOperator.Equal,
                TokenKind.NotEqual => BinaryOperator.NotEqual,
                TokenKind.Less => BinaryOperator.Less,
                TokenKind.LessOrEqual => BinaryOperator.LessOrEqual,
                TokenKind.Greater => BinaryOperator.Greater,
                TokenKind.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
                _ => null,
            };
            if (op is null)
            {
                return left;
            }

            var position = Advance().Position;
            left = new BinaryNode(op.Value, left, ParseConcatenation(), position);
        }
    }

    private FormulaNode ParseConcatenation()
    {
        var left = ParseAdditive();
        while (Current.Kind == TokenKind.Ampersand)
        {
            var position = Advance().Position;
            left = new BinaryNode(BinaryOperator.Concatenate, left, ParseAdditive(), position);
        }

        return left;
    }

    private FormulaNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var token = Advance();
            var op = token.Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            left = new BinaryNode(op, left, ParseMultiplicative(), token.Position);
        }

        return left;
    }

    private FormulaNode ParseMultiplicative()
    {
        var left = ParsePower();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash)
        {
            var token = Advance();
            var op = token.Kind == TokenKind.Star ? BinaryOperator.Multiply : BinaryOperator.Divide;
            left = new BinaryNode(op, left, ParsePower(), token.Position);
        }

        return left;
    }

    private FormulaNode ParsePower()
    {
        var left = ParseUnary();
        while (Current.Kind == TokenKind.Caret)
        {
            var position = Advance().Position;
            left = new BinaryNode(BinaryOperator.Power, left, ParseUnary(), position);
        }

        return left;
    }

    private FormulaNode ParseUnary()
    {
        if (Current.Kind is TokenKind.Minus or TokenKind.Plus)
        {
            var token = Advance();
            var op = token.Kind == TokenKind.Minus ? UnaryOperator.Negate : UnaryOperator.Plus;
            return new UnaryNode(op, ParseUnary(), token.Position);
        }

        return ParsePrimary();
    }

    private FormulaNode ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberNode(token.Number, token.Position);
            case TokenKind.String:
                Advance();
                return new TextNode(token.Text, token.Position);
            case TokenKind.Reference:
                Advance();
                if (Current.Kind == TokenKind.Colon)
                {
                    Advance();
                    if (Current.Kind != TokenKind.Reference)
                    {
                        throw Unexpected($"a column after ':', like [{token.Text}]:[{token.Text}] for the whole column");
                    }

                    var to = Advance();
                    return new ColumnRangeNode(token.Text, to.Text, token.Position);
                }

                return new ReferenceNode(token.Text, token.Position);
            case TokenKind.SheetReference:
                Advance();
                return new SheetRangeNode(token.Text, token.Position);
            case TokenKind.CurrentCell:
                Advance();
                return new CurrentCellNode(token.Position);
            case TokenKind.LeftParen:
            {
                Advance();
                var inner = ParseComparison();
                Expect(TokenKind.RightParen, "')'");
                return inner;
            }

            case TokenKind.Identifier:
                return ParseIdentifier();
            default:
                throw Unexpected("a value, column reference or function");
        }
    }

    private FormulaNode ParseIdentifier()
    {
        var token = Advance();
        var name = token.Text.ToUpperInvariant();
        if (Current.Kind != TokenKind.LeftParen)
        {
            return name switch
            {
                "TRUE" => new BooleanNode(true, token.Position),
                "FALSE" => new BooleanNode(false, token.Position),
                _ => throw new FormulaParseException(
                    $"Unknown name '{token.Text}' at position {token.Position + 1}. Put column names in square brackets, like [{token.Text}], and follow function names with '('.",
                    token.Position),
            };
        }

        Advance();
        var arguments = new List<FormulaNode>();
        if (Current.Kind != TokenKind.RightParen)
        {
            arguments.Add(ParseComparison());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                arguments.Add(ParseComparison());
            }
        }

        Expect(TokenKind.RightParen, $"',' or ')' to close {name}(");

        if (name == FormulaFunctions.Children && (arguments.Count != 1 || arguments[0] is not ReferenceNode))
        {
            throw new FormulaParseException(
                $"CHILDREN at position {token.Position + 1} takes one column reference, like CHILDREN([Budget]).", token.Position);
        }

        return new FunctionNode(name, arguments, token.Position);
    }

    private Token Advance() => _tokens[_index++];

    private void Expect(TokenKind kind, string description)
    {
        if (Current.Kind != kind)
        {
            throw Unexpected(description);
        }

        Advance();
    }

    private FormulaParseException Unexpected(string expected) =>
        Current.Kind == TokenKind.End
            ? new FormulaParseException($"The formula ends too early: expected {expected}.", Current.Position)
            : new FormulaParseException($"Unexpected '{Current.Text}' at position {Current.Position + 1}: expected {expected}.", Current.Position);
}
