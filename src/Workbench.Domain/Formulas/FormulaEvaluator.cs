namespace Workbench.Domain.Formulas;

/// <summary>The row a formula is evaluated against.</summary>
public interface IFormulaContext
{
    DateOnly Today { get; }

    /// <summary>The current row's value for a column or built-in field, or a #REF! error when there is no such name.</summary>
    FormulaValue GetValue(string name);

    /// <summary>A list of the direct children's values for a column, or a #REF! error when there is no such name.</summary>
    FormulaValue GetChildValues(string name);
}

public static class FormulaEvaluator
{
    /// <summary>
    /// Evaluates a parsed formula. Never throws for bad data: problems come back as error values
    /// (#REF!, #DIV/0!, #VALUE!, #NAME?). A list that reaches the top level (e.g. a bare CHILDREN) is #VALUE!.
    /// </summary>
    public static FormulaValue Evaluate(FormulaNode node, IFormulaContext context)
    {
        var result = Eval(node, context);
        return result.Kind == FormulaValueKind.List ? Coerce.ValueError : result;
    }

    internal static FormulaValue Eval(FormulaNode node, IFormulaContext context) => node switch
    {
        NumberNode n => FormulaValue.FromNumber(n.Value),
        TextNode t => FormulaValue.FromText(t.Value),
        BooleanNode b => FormulaValue.FromBoolean(b.Value),
        ReferenceNode r => context.GetValue(r.Name),
        UnaryNode u => EvalUnary(u, context),
        BinaryNode b => EvalBinary(b, context),
        FunctionNode f => FormulaFunctions.Call(f, context),
        _ => throw new ArgumentOutOfRangeException(nameof(node)),
    };

    private static FormulaValue EvalUnary(UnaryNode node, IFormulaContext context)
    {
        var operand = Eval(node.Operand, context);
        if (!Coerce.ToNumber(operand, out var number, out var error))
        {
            return error;
        }

        return FormulaValue.FromNumber(node.Operator == UnaryOperator.Negate ? -number : number);
    }

    private static FormulaValue EvalBinary(BinaryNode node, IFormulaContext context)
    {
        var left = Eval(node.Left, context);
        var right = Eval(node.Right, context);
        switch (node.Operator)
        {
            case BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
                or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual:
                return Coerce.Compare(node.Operator, left, right);
            case BinaryOperator.Concatenate:
                if (!Coerce.ToText(left, out var leftText, out var error) || !Coerce.ToText(right, out var rightText, out error))
                {
                    return error;
                }

                return FormulaValue.FromText(leftText + rightText);
        }

        if (left.IsError)
        {
            return left;
        }

        if (right.IsError)
        {
            return right;
        }

        if (TryDateArithmetic(node.Operator, left, right, out var dateResult))
        {
            return dateResult;
        }

        if (!Coerce.ToNumber(left, out var a, out var numberError) || !Coerce.ToNumber(right, out var b, out numberError))
        {
            return numberError;
        }

        return node.Operator switch
        {
            BinaryOperator.Add => FormulaValue.FromNumber(a + b),
            BinaryOperator.Subtract => FormulaValue.FromNumber(a - b),
            BinaryOperator.Multiply => FormulaValue.FromNumber(a * b),
            BinaryOperator.Divide => b == 0 ? FormulaValue.FromError(FormulaErrors.DivideByZero) : FormulaValue.FromNumber(a / b),
            BinaryOperator.Power => a == 0 && b < 0 ? FormulaValue.FromError(FormulaErrors.DivideByZero) : FormulaValue.FromNumber(Math.Pow(a, b)),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };
    }

    /// <summary>date + days, days + date and date - days give a date; date - date gives the number of days between them.</summary>
    private static bool TryDateArithmetic(BinaryOperator op, FormulaValue left, FormulaValue right, out FormulaValue result)
    {
        result = Coerce.ValueError;
        var leftDate = left.Kind == FormulaValueKind.Date;
        var rightDate = right.Kind == FormulaValueKind.Date;
        if (!leftDate && !rightDate)
        {
            return false;
        }

        switch (op)
        {
            case BinaryOperator.Subtract when leftDate && rightDate:
                result = FormulaValue.FromNumber(left.Date.DayNumber - right.Date.DayNumber);
                return true;
            case BinaryOperator.Add or BinaryOperator.Subtract when leftDate && !rightDate:
                if (Coerce.ToNumber(right, out var days, out result))
                {
                    Coerce.AddDays(left.Date, op == BinaryOperator.Add ? days : -days, out result);
                }

                return true;
            case BinaryOperator.Add when rightDate && !leftDate:
                if (Coerce.ToNumber(left, out var offset, out result))
                {
                    Coerce.AddDays(right.Date, offset, out result);
                }

                return true;
            default:
                return true;
        }
    }
}
