namespace Workbench.Domain.Formulas;

/// <summary>The built-in function library. Names are case-insensitive.</summary>
public static class FormulaFunctions
{
    public const string Children = "CHILDREN";

    private const int Many = int.MaxValue;

    private delegate FormulaValue Eager(IReadOnlyList<FormulaValue> args, IFormulaContext context);

    private delegate FormulaValue Lazy(IReadOnlyList<FormulaNode> args, IFormulaContext context);

    private sealed record Definition(int MinArgs, int MaxArgs, Eager? Eager = null, Lazy? Lazy = null);

    private static readonly Dictionary<string, Definition> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        [Children] = new(1, 1, Lazy: (args, ctx) => ctx.GetChildValues(((ReferenceNode)args[0]).Name)),
        ["SUM"] = new(1, Many, (args, _) => Numbers(args, out var numbers, out var error) ? FormulaValue.FromNumber(numbers.Sum()) : error),
        ["AVG"] = new(1, Many, (args, _) => Average(args)),
        ["MIN"] = new(1, Many, (args, _) => Extreme(args, max: false)),
        ["MAX"] = new(1, Many, (args, _) => Extreme(args, max: true)),
        ["COUNT"] = new(1, Many, (args, _) => FormulaValue.FromNumber(Coerce.Flatten(args).Count(v => !v.IsError && !Coerce.IsBlank(v)))),
        ["COUNTIF"] = new(2, 2, (args, _) => CountIf(args[0], args[1])),
        ["IF"] = new(2, 3, Lazy: If),
        ["AND"] = new(1, Many, (args, _) => Logical(args, and: true)),
        ["OR"] = new(1, Many, (args, _) => Logical(args, and: false)),
        ["NOT"] = new(1, 1, (args, _) => Coerce.ToBoolean(args[0], out var b, out var error) ? FormulaValue.FromBoolean(!b) : error),
        ["ROUND"] = new(1, 2, (args, _) => Round(args)),
        ["ABS"] = new(1, 1, (args, _) => Coerce.ToNumber(args[0], out var n, out var error) ? FormulaValue.FromNumber(Math.Abs(n)) : error),
        ["TODAY"] = new(0, 1, (args, ctx) => Today(args, ctx)),
        ["DATE"] = new(3, 3, (args, _) => Date(args)),
        ["NETWORKDAYS"] = new(2, 3, (args, _) => NetworkDays(args)),
        ["YEAR"] = new(1, 1, (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.Year) : error),
        ["MONTH"] = new(1, 1, (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.Month) : error),
        ["DAY"] = new(1, 1, (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.Day) : error),
        ["LEN"] = new(1, 1, (args, _) => Coerce.ToText(args[0], out var s, out var error) ? FormulaValue.FromNumber(s.Length) : error),
        ["LEFT"] = new(1, 2, (args, _) => Substring(args, fromLeft: true)),
        ["RIGHT"] = new(1, 2, (args, _) => Substring(args, fromLeft: false)),
        ["CONCATENATE"] = new(1, Many, (args, _) => Concatenate(args)),
        ["ISBLANK"] = new(1, 1, (args, _) => FormulaValue.FromBoolean(Coerce.IsBlank(args[0]))),
        ["IFERROR"] = new(2, 2, Lazy: IfError),
        ["DATEDIFF"] = new(2, 2, (args, _) => DateDiff(args)),
    };

    public static IEnumerable<string> Names => Definitions.Keys;

    public static bool IsKnown(string name) => Definitions.ContainsKey(name);

    /// <summary>Checks a call's argument count; returns a user-facing message when it's wrong.</summary>
    public static string? CheckArity(string name, int count)
    {
        if (!Definitions.TryGetValue(name, out var definition) || (count >= definition.MinArgs && count <= definition.MaxArgs))
        {
            return null;
        }

        var expected = definition switch
        {
            { MinArgs: var min, MaxArgs: var max } when min == max => $"{min} argument{(min == 1 ? "" : "s")}",
            { MaxArgs: Many } d => $"at least {d.MinArgs} argument{(d.MinArgs == 1 ? "" : "s")}",
            var d => $"{d.MinArgs} to {d.MaxArgs} arguments",
        };
        return $"{name.ToUpperInvariant()} takes {expected}, but was given {count}.";
    }

    internal static FormulaValue Call(FunctionNode node, IFormulaContext context)
    {
        if (!Definitions.TryGetValue(node.Name, out var definition))
        {
            return FormulaValue.FromError(FormulaErrors.Name);
        }

        if (CheckArity(node.Name, node.Arguments.Count) is not null)
        {
            return Coerce.ValueError;
        }

        if (definition.Lazy is { } lazy)
        {
            return lazy(node.Arguments, context);
        }

        var args = node.Arguments.Select(a => FormulaEvaluator.Eval(a, context)).ToList();
        return definition.Eager!(args, context);
    }

    /// <summary>Collects the numbers in the (flattened) arguments, skipping blanks, text, booleans and dates.</summary>
    private static bool Numbers(IReadOnlyList<FormulaValue> args, out List<double> numbers, out FormulaValue error)
    {
        numbers = [];
        error = Coerce.ValueError;
        foreach (var value in Coerce.Flatten(args))
        {
            if (value.IsError)
            {
                error = value;
                return false;
            }

            if (value.Kind == FormulaValueKind.Number)
            {
                numbers.Add(value.Number);
            }
        }

        return true;
    }

    private static FormulaValue Average(IReadOnlyList<FormulaValue> args)
    {
        if (!Numbers(args, out var numbers, out var error))
        {
            return error;
        }

        return numbers.Count == 0 ? FormulaValue.FromError(FormulaErrors.DivideByZero) : FormulaValue.FromNumber(numbers.Average());
    }

    /// <summary>MIN/MAX over numbers, or over dates when the values are all dates. Mixing the two is #VALUE!; no values is 0.</summary>
    private static FormulaValue Extreme(IReadOnlyList<FormulaValue> args, bool max)
    {
        var values = Coerce.Flatten(args).ToList();
        if (values.FirstOrDefault(v => v.IsError) is { } error)
        {
            return error;
        }

        var numbers = values.Where(v => v.Kind == FormulaValueKind.Number).Select(v => v.Number).ToList();
        var dates = values.Where(v => v.Kind == FormulaValueKind.Date).Select(v => v.Date).ToList();
        if (numbers.Count > 0 && dates.Count > 0)
        {
            return Coerce.ValueError;
        }

        if (dates.Count > 0)
        {
            return FormulaValue.FromDate(max ? dates.Max() : dates.Min());
        }

        return FormulaValue.FromNumber(numbers.Count == 0 ? 0 : max ? numbers.Max() : numbers.Min());
    }

    /// <summary>
    /// Counts values matching a criterion. A text criterion may start with a comparison operator,
    /// e.g. "&gt;5", "&lt;&gt;Done" or "&gt;=2026-01-01"; otherwise values are compared for equality (text ignoring case).
    /// </summary>
    private static FormulaValue CountIf(FormulaValue range, FormulaValue criterion)
    {
        if (criterion.IsError)
        {
            return criterion;
        }

        if (criterion.Kind == FormulaValueKind.List)
        {
            return Coerce.ValueError;
        }

        var (op, target) = ParseCriterion(criterion);
        var count = Coerce.Flatten([range])
            .Count(v => !v.IsError && Coerce.Compare(op, v, target) is { Kind: FormulaValueKind.Boolean, Boolean: true });
        return FormulaValue.FromNumber(count);
    }

    private static (BinaryOperator Op, FormulaValue Target) ParseCriterion(FormulaValue criterion)
    {
        if (criterion.Kind != FormulaValueKind.Text)
        {
            return (BinaryOperator.Equal, criterion);
        }

        (string Prefix, BinaryOperator Op)[] operators =
        [
            ("<>", BinaryOperator.NotEqual), ("<=", BinaryOperator.LessOrEqual), (">=", BinaryOperator.GreaterOrEqual),
            ("<", BinaryOperator.Less), (">", BinaryOperator.Greater), ("=", BinaryOperator.Equal),
        ];
        var text = criterion.Text;
        foreach (var (prefix, op) in operators)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                var operand = text[prefix.Length..].Trim();
                FormulaValue target = operand.Length == 0 ? FormulaValue.Blank
                    : FormulaValue.TryParseNumber(operand, out var n) ? FormulaValue.FromNumber(n)
                    : FormulaValue.TryParseDate(operand, out var d) ? FormulaValue.FromDate(d)
                    : FormulaValue.FromText(operand);
                return (op, target);
            }
        }

        return (BinaryOperator.Equal, criterion);
    }

    private static FormulaValue If(IReadOnlyList<FormulaNode> args, IFormulaContext context)
    {
        var condition = FormulaEvaluator.Eval(args[0], context);
        if (!Coerce.ToBoolean(condition, out var test, out var error))
        {
            return error;
        }

        if (test)
        {
            return FormulaEvaluator.Eval(args[1], context);
        }

        return args.Count > 2 ? FormulaEvaluator.Eval(args[2], context) : FormulaValue.Blank;
    }

    private static FormulaValue IfError(IReadOnlyList<FormulaNode> args, IFormulaContext context)
    {
        var value = FormulaEvaluator.Eval(args[0], context);
        return value.IsError ? FormulaEvaluator.Eval(args[1], context) : value;
    }

    /// <summary>AND/OR over the (flattened) arguments. Blank counts as false.</summary>
    private static FormulaValue Logical(IReadOnlyList<FormulaValue> args, bool and)
    {
        var result = and;
        foreach (var value in Coerce.Flatten(args))
        {
            if (!Coerce.ToBoolean(value, out var b, out var error))
            {
                return error;
            }

            result = and ? result && b : result || b;
        }

        return FormulaValue.FromBoolean(result);
    }

    /// <summary>Rounds half away from zero. Negative digits round to tens, hundreds, ...</summary>
    private static FormulaValue Round(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToNumber(args[0], out var number, out var error))
        {
            return error;
        }

        var digits = 0;
        if (args.Count > 1 && !Coerce.ToInteger(args[1], out digits, out error))
        {
            return error;
        }

        if (digits is > 15 or < -15)
        {
            return Coerce.ValueError;
        }

        if (digits >= 0)
        {
            return FormulaValue.FromNumber(Math.Round(number, digits, MidpointRounding.AwayFromZero));
        }

        var scale = Math.Pow(10, -digits);
        return FormulaValue.FromNumber(Math.Round(number / scale, MidpointRounding.AwayFromZero) * scale);
    }

    /// <summary>TODAY() is the current date; TODAY(n) is n days from it.</summary>
    private static FormulaValue Today(IReadOnlyList<FormulaValue> args, IFormulaContext context)
    {
        var offset = 0d;
        if (args.Count > 0 && !Coerce.ToNumber(args[0], out offset, out var error))
        {
            return error;
        }

        Coerce.AddDays(context.Today, offset, out var result);
        return result;
    }

    private static FormulaValue Date(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToInteger(args[0], out var year, out var error)
            || !Coerce.ToInteger(args[1], out var month, out error)
            || !Coerce.ToInteger(args[2], out var day, out error))
        {
            return error;
        }

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return Coerce.ValueError;
        }

        return FormulaValue.FromDate(new DateOnly(year, month, day));
    }

    /// <summary>Weekdays (Mon-Fri) from start to end, inclusive, minus any holidays. Negative when end is before start.</summary>
    private static FormulaValue NetworkDays(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToDate(args[0], out var start, out var error) || !Coerce.ToDate(args[1], out var end, out error))
        {
            return error;
        }

        var holidays = new HashSet<DateOnly>();
        if (args.Count > 2)
        {
            foreach (var value in Coerce.Flatten([args[2]]).Where(v => !Coerce.IsBlank(v)))
            {
                if (!Coerce.ToDate(value, out var holiday, out error))
                {
                    return error;
                }

                holidays.Add(holiday);
            }
        }

        var sign = 1;
        if (end < start)
        {
            (start, end) = (end, start);
            sign = -1;
        }

        var days = end.DayNumber - start.DayNumber + 1;
        var weekdays = days / 7 * 5;
        for (var d = start.AddDays(days / 7 * 7); d <= end; d = d.AddDays(1))
        {
            if (IsWeekday(d))
            {
                weekdays++;
            }
        }

        weekdays -= holidays.Count(h => h >= start && h <= end && IsWeekday(h));
        return FormulaValue.FromNumber(sign * weekdays);

        static bool IsWeekday(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    private static FormulaValue Substring(IReadOnlyList<FormulaValue> args, bool fromLeft)
    {
        if (!Coerce.ToText(args[0], out var text, out var error))
        {
            return error;
        }

        var count = 1;
        if (args.Count > 1 && !Coerce.ToInteger(args[1], out count, out error))
        {
            return error;
        }

        if (count < 0)
        {
            return Coerce.ValueError;
        }

        count = Math.Min(count, text.Length);
        return FormulaValue.FromText(fromLeft ? text[..count] : text[^count..]);
    }

    private static FormulaValue Concatenate(IReadOnlyList<FormulaValue> args)
    {
        var parts = new List<string>();
        foreach (var value in Coerce.Flatten(args))
        {
            if (!Coerce.ToText(value, out var text, out var error))
            {
                return error;
            }

            parts.Add(text);
        }

        return FormulaValue.FromText(string.Concat(parts));
    }

    /// <summary>DATEDIFF(start, end): calendar days from start to end (negative when end is earlier).</summary>
    private static FormulaValue DateDiff(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToDate(args[0], out var start, out var error) || !Coerce.ToDate(args[1], out var end, out error))
        {
            return error;
        }

        return FormulaValue.FromNumber(end.DayNumber - start.DayNumber);
    }
}
