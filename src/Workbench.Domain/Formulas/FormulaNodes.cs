using System.Globalization;

namespace Workbench.Domain.Formulas;

public enum UnaryOperator
{
    Negate,
    Plus,
}

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Power,
    Concatenate,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>
/// A node in a parsed formula. <see cref="ToString"/> prints a fully parenthesized prefix form,
/// e.g. <c>(+ 1 (* 2 3))</c>, which makes the parse tree easy to inspect and test.
/// </summary>
public abstract record FormulaNode(int Position);

public sealed record NumberNode(double Value, int Position) : FormulaNode(Position)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record TextNode(string Value, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"\"{Value.Replace("\"", "\"\"")}\"";
}

public sealed record BooleanNode(bool Value, int Position) : FormulaNode(Position)
{
    public override string ToString() => Value ? "TRUE" : "FALSE";
}

/// <summary>A <c>[Column Name]</c> reference to the current row.</summary>
public sealed record ReferenceNode(string Name, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"[{Name}]";
}

public sealed record UnaryNode(UnaryOperator Operator, FormulaNode Operand, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"({(Operator == UnaryOperator.Negate ? "neg" : "pos")} {Operand})";
}

public sealed record BinaryNode(BinaryOperator Operator, FormulaNode Left, FormulaNode Right, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"({Symbol(Operator)} {Left} {Right})";

    public static string Symbol(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Power => "^",
        BinaryOperator.Concatenate => "&",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.Less => "<",
        BinaryOperator.LessOrEqual => "<=",
        BinaryOperator.Greater => ">",
        BinaryOperator.GreaterOrEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}

/// <summary>A function call. <see cref="Name"/> is upper-cased.</summary>
public sealed record FunctionNode(string Name, IReadOnlyList<FormulaNode> Arguments, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"{Name}({string.Join(", ", Arguments)})";
}

/// <summary>
/// A whole-column range on this sheet, <c>[Status]:[Status]</c>, or several adjacent columns, <c>[Task]:[Owner]</c>
/// (every row, in sheet order). Used by COUNTIFS, SUMIFS, INDEX, MATCH, VLOOKUP and the other range functions.
/// </summary>
public sealed record ColumnRangeNode(string From, string To, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"[{From}]:[{To}]";
}

/// <summary>A named cross-sheet reference, <c>{Open risks}</c>: columns of another sheet, chosen by name on this sheet.</summary>
public sealed record SheetRangeNode(string Name, int Position) : FormulaNode(Position)
{
    public override string ToString() => $"{{{Name}}}";
}

/// <summary><c>@cell</c>: inside a criterion, the value being tested (as in Smartsheet).</summary>
public sealed record CurrentCellNode(int Position) : FormulaNode(Position)
{
    public override string ToString() => "@cell";
}
