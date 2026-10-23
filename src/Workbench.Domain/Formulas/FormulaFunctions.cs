using System.Globalization;
using System.Text;

namespace Workbench.Domain.Formulas;

/// <summary>A function in the formula help: its name, how to call it and what it does, in plain words.</summary>
public sealed record FormulaFunctionInfo(string Name, string Category, string Syntax, string Description);

/// <summary>The built-in function library. Names are case-insensitive.</summary>
public static class FormulaFunctions
{
    public const string Children = "CHILDREN";

    private const int Many = int.MaxValue;

    private const string MathCategory = "Math";
    private const string Conditional = "Count and sum with conditions";
    private const string Logic = "Logic";
    private const string Lookup = "Look up values";
    private const string Dates = "Dates";
    private const string TextCategory = "Text";
    private const string Hierarchy = "Hierarchy";

    private delegate FormulaValue Eager(IReadOnlyList<FormulaValue> args, IFormulaContext context);

    private delegate FormulaValue Lazy(IReadOnlyList<FormulaNode> args, IFormulaContext context);

    /// <summary><paramref name="Criteria"/>: which argument positions are criteria (where <c>@cell</c> may appear).</summary>
    private sealed record Definition(
        int MinArgs, int MaxArgs, string Category, string Syntax, string Description,
        Eager? Eager = null, Lazy? Lazy = null, Func<int, bool>? Criteria = null);

    private static readonly Dictionary<string, Definition> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        [Children] = new(1, 1, Hierarchy, "CHILDREN([Column])", "The values of a column in this row's child rows, for SUM, COUNT, MAX and the like.",
            Lazy: (args, ctx) => ctx.GetChildValues(((ReferenceNode)args[0]).Name)),

        // Math
        ["SUM"] = new(1, Many, MathCategory, "SUM(value1, value2, ...)", "Adds up numbers. Text and blanks are skipped.",
            (args, _) => Numbers(args, out var numbers, out var error) ? FormulaValue.FromNumber(numbers.Sum()) : error),
        ["AVG"] = new(1, Many, MathCategory, "AVG(value1, value2, ...)", "The average of the numbers.", (args, _) => Average(args)),
        ["AVERAGE"] = new(1, Many, MathCategory, "AVERAGE(value1, value2, ...)", "The average of the numbers (same as AVG).", (args, _) => Average(args)),
        ["MEDIAN"] = new(1, Many, MathCategory, "MEDIAN(value1, value2, ...)", "The middle number.", (args, _) => Median(args)),
        ["MIN"] = new(1, Many, MathCategory, "MIN(value1, value2, ...)", "The smallest number or earliest date.", (args, _) => Extreme(args, max: false)),
        ["MAX"] = new(1, Many, MathCategory, "MAX(value1, value2, ...)", "The largest number or latest date.", (args, _) => Extreme(args, max: true)),
        ["LARGE"] = new(2, 2, MathCategory, "LARGE(range, n)", "The n-th largest number (LARGE(range, 1) is the largest).", (args, _) => Nth(args, largest: true)),
        ["SMALL"] = new(2, 2, MathCategory, "SMALL(range, n)", "The n-th smallest number.", (args, _) => Nth(args, largest: false)),
        ["PRODUCT"] = new(1, Many, MathCategory, "PRODUCT(value1, value2, ...)", "Multiplies the numbers together.",
            (args, _) => Numbers(args, out var numbers, out var error) ? FormulaValue.FromNumber(numbers.Aggregate(1d, (a, b) => a * b)) : error),
        ["ROUND"] = new(1, 2, MathCategory, "ROUND(number, [digits])", "Rounds to the given number of decimal places (halves away from zero).",
            (args, _) => Round(args, MidpointRounding.AwayFromZero)),
        ["ROUNDUP"] = new(1, 2, MathCategory, "ROUNDUP(number, [digits])", "Rounds away from zero.", (args, _) => Round(args, MidpointRounding.AwayFromZero, up: true)),
        ["ROUNDDOWN"] = new(1, 2, MathCategory, "ROUNDDOWN(number, [digits])", "Rounds toward zero.", (args, _) => Round(args, MidpointRounding.ToZero)),
        ["INT"] = new(1, 1, MathCategory, "INT(number)", "Rounds down to a whole number.", (args, _) => Map(args[0], Math.Floor)),
        ["ABS"] = new(1, 1, MathCategory, "ABS(number)", "The number without its sign.", (args, _) => Map(args[0], Math.Abs)),
        ["SQRT"] = new(1, 1, MathCategory, "SQRT(number)", "The square root.", (args, _) => Map(args[0], n => n < 0 ? double.NaN : Math.Sqrt(n))),
        ["MOD"] = new(2, 2, MathCategory, "MOD(number, divisor)", "The remainder after dividing.", (args, _) => Mod(args)),
        ["CEILING"] = new(1, 2, MathCategory, "CEILING(number, [multiple])", "Rounds up to the next multiple (1 if left out).", (args, _) => ToMultiple(args, Math.Ceiling)),
        ["FLOOR"] = new(1, 2, MathCategory, "FLOOR(number, [multiple])", "Rounds down to the multiple below (1 if left out).", (args, _) => ToMultiple(args, Math.Floor)),

        // Counting and summing with conditions
        ["COUNT"] = new(1, Many, Conditional, "COUNT(value1, value2, ...)", "How many values aren't blank.",
            (args, _) => FormulaValue.FromNumber(Coerce.Flatten(args).Count(v => !v.IsError && !Coerce.IsBlank(v)))),
        ["COUNTBLANK"] = new(1, Many, Conditional, "COUNTBLANK(range)", "How many values are blank.",
            (args, _) => FormulaValue.FromNumber(Coerce.Flatten(args).Count(Coerce.IsBlank))),
        ["COUNTIF"] = new(2, 2, Conditional, "COUNTIF(range, criterion)",
            "How many values match, e.g. COUNTIF([Status]:[Status], \"Done\") or COUNTIF([Hours]:[Hours], \">40\").",
            Lazy: (args, ctx) => CountIfs(args, ctx, first: 0), Criteria: i => i == 1),
        ["COUNTIFS"] = new(2, Many, Conditional, "COUNTIFS(range1, criterion1, range2, criterion2, ...)",
            "How many rows match every condition. A criterion can test @cell, e.g. COUNTIFS([Due]:[Due], @cell < TODAY(), [Status]:[Status], \"<>Done\").",
            Lazy: (args, ctx) => CountIfs(args, ctx, first: 0), Criteria: i => i % 2 == 1),
        ["SUMIF"] = new(2, 3, Conditional, "SUMIF(range, criterion, [sum_range])",
            "Adds the numbers in sum_range (or range) on rows where range matches the criterion.",
            Lazy: (args, ctx) => AggregateIf(args, ctx, sum: true), Criteria: i => i == 1),
        ["SUMIFS"] = new(3, Many, Conditional, "SUMIFS(sum_range, range1, criterion1, ...)", "Adds the numbers in sum_range on rows that match every condition.",
            Lazy: (args, ctx) => AggregateIfs(args, ctx, sum: true), Criteria: i => i >= 2 && i % 2 == 0),
        ["AVERAGEIF"] = new(2, 3, Conditional, "AVERAGEIF(range, criterion, [average_range])", "The average of the numbers on rows that match.",
            Lazy: (args, ctx) => AggregateIf(args, ctx, sum: false), Criteria: i => i == 1),
        ["AVERAGEIFS"] = new(3, Many, Conditional, "AVERAGEIFS(average_range, range1, criterion1, ...)", "The average of the numbers on rows that match every condition.",
            Lazy: (args, ctx) => AggregateIfs(args, ctx, sum: false), Criteria: i => i >= 2 && i % 2 == 0),
        ["COLLECT"] = new(3, Many, Conditional, "COLLECT(range, criterion_range1, criterion1, ...)",
            "The values in range on rows that match every condition, for MAX, JOIN and the like: MAX(COLLECT([Due]:[Due], [Owner]:[Owner], \"Sam\")).",
            Lazy: Collect, Criteria: i => i >= 2 && i % 2 == 0),

        // Logic
        ["IF"] = new(2, 3, Logic, "IF(condition, value_if_true, [value_if_false])", "One value when the condition is true, another when it's false.", Lazy: If),
        ["AND"] = new(1, Many, Logic, "AND(condition1, condition2, ...)", "True when every condition is true.", (args, _) => Logical(args, and: true)),
        ["OR"] = new(1, Many, Logic, "OR(condition1, condition2, ...)", "True when any condition is true.", (args, _) => Logical(args, and: false)),
        ["NOT"] = new(1, 1, Logic, "NOT(condition)", "True when the condition is false.",
            (args, _) => Coerce.ToBoolean(args[0], out var b, out var error) ? FormulaValue.FromBoolean(!b) : error),
        ["IFERROR"] = new(2, 2, Logic, "IFERROR(value, value_if_error)", "The value, or the fallback when the value is an error.", Lazy: IfError),
        ["ISBLANK"] = new(1, 1, Logic, "ISBLANK(value)", "True when the value is empty.", (args, _) => FormulaValue.FromBoolean(Coerce.IsBlank(args[0]))),
        ["ISERROR"] = new(1, 1, Logic, "ISERROR(value)", "True when the value is an error.", (args, _) => FormulaValue.FromBoolean(args[0].IsError)),
        ["ISNUMBER"] = new(1, 1, Logic, "ISNUMBER(value)", "True when the value is a number.", (args, _) => Is(args[0], FormulaValueKind.Number)),
        ["ISTEXT"] = new(1, 1, Logic, "ISTEXT(value)", "True when the value is text (not blank).", (args, _) => FormulaValue.FromBoolean(args[0].Kind == FormulaValueKind.Text && args[0].Text.Length > 0)),
        ["ISDATE"] = new(1, 1, Logic, "ISDATE(value)", "True when the value is a date.", (args, _) => Is(args[0], FormulaValueKind.Date)),
        ["ISBOOLEAN"] = new(1, 1, Logic, "ISBOOLEAN(value)", "True when the value is TRUE or FALSE (a checkbox).", (args, _) => Is(args[0], FormulaValueKind.Boolean)),
        ["CONTAINS"] = new(2, 2, Logic, "CONTAINS(text, range)", "True when the text appears anywhere in the range (ignoring case).", (args, _) => Contains(args)),
        ["HAS"] = new(2, 2, Logic, "HAS(range, value)", "True when one of the values in the range is exactly the value (comma-separated lists count item by item).",
            (args, _) => Has(args)),

        // Looking up values
        ["INDEX"] = new(2, 3, Lookup, "INDEX(range, row, [column])", "The value at a row (and column) of a range, counting from 1.", (args, _) => Index(args)),
        ["MATCH"] = new(2, 3, Lookup, "MATCH(value, range, [match_type])",
            "The position of a value in a range: 0 for an exact match, 1 (the default) for the largest value at or below it, -1 for the smallest at or above it.",
            (args, _) => Match(args)),
        ["VLOOKUP"] = new(3, 4, Lookup, "VLOOKUP(value, range, column, [approximate])",
            "Finds the value in the range's first column and returns the given column of that row. Use FALSE for an exact match.",
            (args, _) => VLookup(args)),
        ["JOIN"] = new(1, 2, Lookup, "JOIN(range, [separator])", "Joins the values that aren't blank, separated by \", \" (or the separator).", (args, _) => Join(args)),
        ["DISTINCT"] = new(1, 1, Lookup, "DISTINCT(range)", "The values without repeats, for COUNT or JOIN.", (args, _) => Distinct(args[0])),

        // Dates
        ["TODAY"] = new(0, 1, Dates, "TODAY([days])", "Today's date, or the date that many days from today.", (args, ctx) => Today(args, ctx)),
        ["DATE"] = new(3, 3, Dates, "DATE(year, month, day)", "A date from its parts.", (args, _) => Date(args)),
        ["YEAR"] = new(1, 1, Dates, "YEAR(date)", "The year of a date.",
            (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.Year) : error),
        ["MONTH"] = new(1, 1, Dates, "MONTH(date)", "The month of a date (1 to 12).",
            (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.Month) : error),
        ["DAY"] = new(1, 1, Dates, "DAY(date)", "The day of the month (1 to 31).",
            (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.Day) : error),
        ["WEEKDAY"] = new(1, 1, Dates, "WEEKDAY(date)", "The day of the week, 1 (Sunday) to 7 (Saturday).",
            (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber((int)d.DayOfWeek + 1) : error),
        ["WEEKNUMBER"] = new(1, 1, Dates, "WEEKNUMBER(date)", "The ISO week number (1 to 53).",
            (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue))) : error),
        ["YEARDAY"] = new(1, 1, Dates, "YEARDAY(date)", "The day of the year (1 to 366).",
            (args, _) => Coerce.ToDate(args[0], out var d, out var error) ? FormulaValue.FromNumber(d.DayOfYear) : error),
        ["DATEDIFF"] = new(2, 2, Dates, "DATEDIFF(start, end)", "Calendar days from start to end.", (args, _) => DateDiff(args)),
        ["NETDAYS"] = new(2, 2, Dates, "NETDAYS(start, end)", "Calendar days from start to end, counting both.", (args, _) => NetDays(args)),
        ["NETWORKDAYS"] = new(2, 3, Dates, "NETWORKDAYS(start, end, [holidays])", "Working days (Monday to Friday) from start to end, counting both, less holidays.",
            (args, _) => NetworkDays(args)),
        ["NETWORKDAY"] = new(2, 3, Dates, "NETWORKDAY(start, end, [holidays])", "Same as NETWORKDAYS.", (args, _) => NetworkDays(args)),
        ["WORKDAY"] = new(2, 3, Dates, "WORKDAY(date, days, [holidays])", "The date that many working days (Monday to Friday) after the date, skipping holidays.",
            (args, _) => WorkDay(args)),

        // Text
        ["LEN"] = new(1, 1, TextCategory, "LEN(text)", "How many characters.",
            (args, _) => Coerce.ToText(args[0], out var s, out var error) ? FormulaValue.FromNumber(s.Length) : error),
        ["LEFT"] = new(1, 2, TextCategory, "LEFT(text, [count])", "The first characters.", (args, _) => Substring(args, fromLeft: true)),
        ["RIGHT"] = new(1, 2, TextCategory, "RIGHT(text, [count])", "The last characters.", (args, _) => Substring(args, fromLeft: false)),
        ["MID"] = new(3, 3, TextCategory, "MID(text, start, count)", "Characters from the middle, starting at position start (from 1).", (args, _) => Mid(args)),
        ["FIND"] = new(2, 3, TextCategory, "FIND(search, text, [start])", "Where the search text starts (from 1, matching case), or 0 when it isn't there.",
            (args, _) => Find(args)),
        ["UPPER"] = new(1, 1, TextCategory, "UPPER(text)", "The text in capitals.", (args, _) => MapText(args[0], s => s.ToUpperInvariant())),
        ["LOWER"] = new(1, 1, TextCategory, "LOWER(text)", "The text in lower case.", (args, _) => MapText(args[0], s => s.ToLowerInvariant())),
        ["TRIM"] = new(1, 1, TextCategory, "TRIM(text)", "The text without extra spaces.",
            (args, _) => MapText(args[0], s => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)))),
        ["SUBSTITUTE"] = new(3, 4, TextCategory, "SUBSTITUTE(text, old, new, [which])", "Replaces old text with new (every time, or only the n-th time).",
            (args, _) => Substitute(args)),
        ["REPLACE"] = new(4, 4, TextCategory, "REPLACE(text, start, count, new)", "Replaces count characters from position start with new text.", (args, _) => Replace(args)),
        ["CONCATENATE"] = new(1, Many, TextCategory, "CONCATENATE(text1, text2, ...)", "Joins text together (the & operator does the same).", (args, _) => Concatenate(args)),
        ["VALUE"] = new(1, 1, TextCategory, "VALUE(text)", "Reads text such as \"1,200.50\" or \"$40\" as a number.", (args, _) => ToValue(args[0])),
    };

    public static IEnumerable<string> Names => Definitions.Keys;

    /// <summary>Every function, by category then name, for the formula help.</summary>
    public static IReadOnlyList<FormulaFunctionInfo> Catalog { get; } = Definitions
        .Select(d => new FormulaFunctionInfo(d.Key, d.Value.Category, d.Value.Syntax, d.Value.Description))
        .OrderBy(f => f.Category, StringComparer.Ordinal)
        .ThenBy(f => f.Name, StringComparer.Ordinal)
        .ToList();

    public static bool IsKnown(string name) => Definitions.ContainsKey(name);

    /// <summary>True when argument <paramref name="index"/> of <paramref name="name"/> is a criterion, where <c>@cell</c> may be used.</summary>
    public static bool IsCriterionArgument(string name, int index) =>
        Definitions.TryGetValue(name, out var definition) && definition.Criteria?.Invoke(index) == true;

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
        return $"{name.ToUpperInvariant()} takes {expected}, but was given {count}. Use it like {definition.Syntax}.";
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

    private static FormulaValue Median(IReadOnlyList<FormulaValue> args)
    {
        if (!Numbers(args, out var numbers, out var error))
        {
            return error;
        }

        if (numbers.Count == 0)
        {
            return Coerce.ValueError;
        }

        numbers.Sort();
        var middle = numbers.Count / 2;
        return FormulaValue.FromNumber(numbers.Count % 2 == 1 ? numbers[middle] : (numbers[middle - 1] + numbers[middle]) / 2);
    }

    private static FormulaValue Nth(IReadOnlyList<FormulaValue> args, bool largest)
    {
        if (!Numbers([args[0]], out var numbers, out var error) || !Coerce.ToInteger(args[1], out var n, out error))
        {
            return error;
        }

        if (n < 1 || n > numbers.Count)
        {
            return Coerce.ValueError;
        }

        numbers.Sort();
        return FormulaValue.FromNumber(largest ? numbers[^n] : numbers[n - 1]);
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

    private static FormulaValue Map(FormulaValue value, Func<double, double> map) =>
        Coerce.ToNumber(value, out var n, out var error) ? FormulaValue.FromNumber(map(n)) : error;

    private static FormulaValue MapText(FormulaValue value, Func<string, string> map) =>
        Coerce.ToText(value, out var s, out var error) ? FormulaValue.FromText(map(s)) : error;

    private static FormulaValue Is(FormulaValue value, FormulaValueKind kind) => FormulaValue.FromBoolean(value.Kind == kind);

    private static FormulaValue Mod(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToNumber(args[0], out var n, out var error) || !Coerce.ToNumber(args[1], out var d, out error))
        {
            return error;
        }

        // The result takes the divisor's sign, as in Excel and Smartsheet.
        return d == 0 ? FormulaValue.FromError(FormulaErrors.DivideByZero) : FormulaValue.FromNumber(n - d * Math.Floor(n / d));
    }

    private static FormulaValue ToMultiple(IReadOnlyList<FormulaValue> args, Func<double, double> round)
    {
        if (!Coerce.ToNumber(args[0], out var n, out var error))
        {
            return error;
        }

        var multiple = 1d;
        if (args.Count > 1 && !Coerce.ToNumber(args[1], out multiple, out error))
        {
            return error;
        }

        return multiple == 0 ? FormulaValue.FromNumber(0) : FormulaValue.FromNumber(round(n / multiple) * multiple);
    }

    // ---- Criteria (COUNTIF(S), SUMIF(S), AVERAGEIF(S), COLLECT) ----

    /// <summary>
    /// A criterion: a value to compare with (text may start with an operator, e.g. "&gt;5", "&lt;&gt;Done" or
    /// "&gt;=2026-01-01"; otherwise values are compared for equality, text ignoring case), or an expression using
    /// <c>@cell</c> that is tested for each value.
    /// </summary>
    private sealed class Criterion
    {
        private readonly FormulaNode? _expression;
        private readonly IFormulaContext _context;
        private readonly BinaryOperator _op;
        private readonly FormulaValue _target = FormulaValue.Blank;

        private Criterion(IFormulaContext context, FormulaNode expression)
        {
            _context = context;
            _expression = expression;
        }

        private Criterion(IFormulaContext context, BinaryOperator op, FormulaValue target)
        {
            _context = context;
            _op = op;
            _target = target;
        }

        /// <summary>The criterion, or null with <paramref name="error"/> when its value is an error or a list.</summary>
        public static Criterion? Build(FormulaNode node, IFormulaContext context, out FormulaValue error)
        {
            error = Coerce.ValueError;
            if (UsesCell(node))
            {
                return new Criterion(context, node);
            }

            var value = FormulaEvaluator.Eval(node, context);
            if (value.IsError)
            {
                error = value;
                return null;
            }

            if (value.Kind == FormulaValueKind.List)
            {
                return null;
            }

            var (op, target) = ParseCriterion(value);
            return new Criterion(context, op, target);
        }

        /// <summary>
        /// Errors never match. A blank cell never passes an @cell comparison such as <c>@cell &lt; TODAY()</c> (rows with no
        /// date aren't overdue); text criteria such as "&lt;=8" keep treating blank as 0.
        /// </summary>
        public bool Matches(FormulaValue cell)
        {
            if (cell.IsError)
            {
                return false;
            }

            if (Coerce.IsBlank(cell)
                && _expression is BinaryNode { Left: CurrentCellNode } or BinaryNode { Right: CurrentCellNode }
                && IsOrdering(((BinaryNode)_expression).Operator))
            {
                return false;
            }

            if (_expression is not null)
            {
                var result = FormulaEvaluator.Eval(_expression, new CellContext(_context, cell));
                return Coerce.ToBoolean(result, out var b, out _) && b;
            }

            return Coerce.Compare(_op, cell, _target) is { Kind: FormulaValueKind.Boolean, Boolean: true };
        }

        private static bool IsOrdering(BinaryOperator op) =>
            op is BinaryOperator.Less or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual;

        private static bool UsesCell(FormulaNode node) => node switch
        {
            CurrentCellNode => true,
            FunctionNode f => f.Arguments.Any(UsesCell),
            UnaryNode u => UsesCell(u.Operand),
            BinaryNode b => UsesCell(b.Left) || UsesCell(b.Right),
            _ => false,
        };
    }

    /// <summary>A range argument's cells: a list's items, or a single value as a one-cell range.</summary>
    private static IReadOnlyList<FormulaValue> Cells(FormulaValue value) => value.Kind == FormulaValueKind.List ? value.Items : [value];

    /// <summary>
    /// Evaluates (range, criterion) pairs starting at <paramref name="first"/> and returns, per position, whether every
    /// criterion matched; null with <paramref name="error"/> when an argument is an error or the ranges differ in size.
    /// </summary>
    private static bool[]? MatchAll(IReadOnlyList<FormulaNode> args, IFormulaContext context, int first, int? size, out FormulaValue error)
    {
        error = Coerce.ValueError;
        bool[]? matches = null;
        for (var i = first; i + 1 < args.Count; i += 2)
        {
            var range = FormulaEvaluator.Eval(args[i], context);
            if (range.IsError)
            {
                error = range;
                return null;
            }

            var cells = Cells(range);
            size ??= cells.Count;
            if (cells.Count != size)
            {
                return null;
            }

            var criterion = Criterion.Build(args[i + 1], context, out error);
            if (criterion is null)
            {
                return null;
            }

            matches ??= Enumerable.Repeat(true, cells.Count).ToArray();
            for (var j = 0; j < cells.Count; j++)
            {
                matches[j] = matches[j] && criterion.Matches(cells[j]);
            }
        }

        if ((args.Count - first) % 2 != 0)
        {
            return null; // a range without its criterion
        }

        return matches ?? [];
    }

    private static FormulaValue CountIfs(IReadOnlyList<FormulaNode> args, IFormulaContext context, int first)
    {
        var matches = MatchAll(args, context, first, null, out var error);
        return matches is null ? error : FormulaValue.FromNumber(matches.Count(m => m));
    }

    /// <summary>SUMIF/AVERAGEIF(range, criterion, [values]).</summary>
    private static FormulaValue AggregateIf(IReadOnlyList<FormulaNode> args, IFormulaContext context, bool sum)
    {
        var matches = MatchAll(args.Take(2).ToList(), context, 0, null, out var error);
        if (matches is null)
        {
            return error;
        }

        var values = FormulaEvaluator.Eval(args.Count > 2 ? args[2] : args[0], context);
        return Aggregate(values, matches, sum);
    }

    /// <summary>SUMIFS/AVERAGEIFS(values, range1, criterion1, ...).</summary>
    private static FormulaValue AggregateIfs(IReadOnlyList<FormulaNode> args, IFormulaContext context, bool sum)
    {
        var values = FormulaEvaluator.Eval(args[0], context);
        if (values.IsError)
        {
            return values;
        }

        var matches = MatchAll(args, context, 1, Cells(values).Count, out var error);
        return matches is null ? error : Aggregate(values, matches, sum);
    }

    private static FormulaValue Aggregate(FormulaValue values, bool[] matches, bool sum)
    {
        if (values.IsError)
        {
            return values;
        }

        var cells = Cells(values);
        if (cells.Count != matches.Length)
        {
            return Coerce.ValueError;
        }

        var picked = cells.Where((_, i) => matches[i]).ToList();
        if (picked.FirstOrDefault(v => v.IsError) is { } error)
        {
            return error;
        }

        var numbers = picked.Where(v => v.Kind == FormulaValueKind.Number).Select(v => v.Number).ToList();
        if (sum)
        {
            return FormulaValue.FromNumber(numbers.Sum());
        }

        return numbers.Count == 0 ? FormulaValue.FromError(FormulaErrors.DivideByZero) : FormulaValue.FromNumber(numbers.Average());
    }

    private static FormulaValue Collect(IReadOnlyList<FormulaNode> args, IFormulaContext context)
    {
        var values = FormulaEvaluator.Eval(args[0], context);
        if (values.IsError)
        {
            return values;
        }

        var cells = Cells(values);
        var matches = MatchAll(args, context, 1, cells.Count, out var error);
        return matches is null ? error : FormulaValue.FromList(cells.Where((_, i) => matches[i]).ToList());
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

    // ---- Logic ----

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

    private static FormulaValue Contains(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToText(args[0], out var search, out var error))
        {
            return error;
        }

        foreach (var cell in Cells(args[1]).Where(c => !c.IsError))
        {
            if (Coerce.ToText(cell, out var text, out _) && text.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return FormulaValue.True;
            }
        }

        return FormulaValue.False;
    }

    private static FormulaValue Has(IReadOnlyList<FormulaValue> args)
    {
        var target = args[1];
        if (target.IsError)
        {
            return target;
        }

        foreach (var cell in Cells(args[0]).Where(c => !c.IsError && !Coerce.IsBlank(c)))
        {
            if (Coerce.Order(cell, target) == 0)
            {
                return FormulaValue.True;
            }

            if (cell.Kind == FormulaValueKind.Text && Coerce.ToText(target, out var wanted, out _)
                && cell.Text.Split(',', StringSplitOptions.TrimEntries).Contains(wanted.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return FormulaValue.True;
            }
        }

        return FormulaValue.False;
    }

    // ---- Looking up values ----

    private static FormulaValue Index(IReadOnlyList<FormulaValue> args)
    {
        var range = args[0];
        if (range.IsError)
        {
            return range;
        }

        if (!Coerce.ToInteger(args[1], out var row, out var error))
        {
            return error;
        }

        var column = 1;
        if (args.Count > 2 && !Coerce.ToInteger(args[2], out column, out error))
        {
            return error;
        }

        if (range.Kind != FormulaValueKind.List)
        {
            return row == 1 && column == 1 ? range : Coerce.ValueError;
        }

        if (range.Width == 1 && args.Count == 2)
        {
            return row >= 1 && row <= range.Items.Count ? range.Items[row - 1] : Coerce.ValueError;
        }

        return row >= 1 && row <= range.RowCount && column >= 1 && column <= range.Width ? range.At(row - 1, column - 1) : Coerce.ValueError;
    }

    private static FormulaValue Match(IReadOnlyList<FormulaValue> args)
    {
        var search = args[0];
        if (search.IsError)
        {
            return search;
        }

        if (args[1].IsError)
        {
            return args[1];
        }

        var type = 1;
        if (args.Count > 2 && !Coerce.ToInteger(args[2], out type, out var error))
        {
            return error;
        }

        var cells = Cells(args[1]);
        var position = type switch
        {
            0 => Exact(cells, search),
            > 0 => Closest(cells, search, below: true),
            _ => Closest(cells, search, below: false),
        };
        return position < 0 ? FormulaValue.FromError(FormulaErrors.NoMatch) : FormulaValue.FromNumber(position + 1);
    }

    private static FormulaValue VLookup(IReadOnlyList<FormulaValue> args)
    {
        var search = args[0];
        var range = args[1];
        if (search.IsError)
        {
            return search;
        }

        if (range.IsError)
        {
            return range;
        }

        if (!Coerce.ToInteger(args[2], out var column, out var error))
        {
            return error;
        }

        var approximate = true;
        if (args.Count > 3 && !Coerce.ToBoolean(args[3], out approximate, out error))
        {
            return error;
        }

        if (range.Kind != FormulaValueKind.List || column < 1 || column > range.Width)
        {
            return Coerce.ValueError;
        }

        var keys = Enumerable.Range(0, range.RowCount).Select(r => range.At(r, 0)).ToList();
        var row = approximate ? Closest(keys, search, below: true) : Exact(keys, search);
        return row < 0 ? FormulaValue.FromError(FormulaErrors.NoMatch) : range.At(row, column - 1);
    }

    private static int Exact(IReadOnlyList<FormulaValue> cells, FormulaValue search)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (!cells[i].IsError && !Coerce.IsBlank(cells[i]) && Coerce.Order(cells[i], search) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The position of the largest value at or below <paramref name="search"/> (or smallest at or above it).</summary>
    private static int Closest(IReadOnlyList<FormulaValue> cells, FormulaValue search, bool below)
    {
        var best = -1;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsError || Coerce.IsBlank(cell) || Coerce.Order(cell, search) is not { } order || (below ? order > 0 : order < 0))
            {
                continue;
            }

            if (best < 0 || (Coerce.Order(cell, cells[best]) is { } versus && (below ? versus > 0 : versus < 0)))
            {
                best = i;
            }
        }

        return best;
    }

    private static FormulaValue Join(IReadOnlyList<FormulaValue> args)
    {
        var separator = ", ";
        if (args.Count > 1 && !Coerce.ToText(args[1], out separator, out var error))
        {
            return error;
        }

        var parts = new List<string>();
        foreach (var value in Cells(args[0]).Where(v => !Coerce.IsBlank(v)))
        {
            if (!Coerce.ToText(value, out var text, out error))
            {
                return error;
            }

            parts.Add(text);
        }

        return FormulaValue.FromText(string.Join(separator, parts));
    }

    private static FormulaValue Distinct(FormulaValue range)
    {
        if (range.IsError)
        {
            return range;
        }

        var distinct = new List<FormulaValue>();
        foreach (var cell in Cells(range).Where(c => !c.IsError && !Coerce.IsBlank(c)))
        {
            if (!distinct.Any(d => Coerce.Order(d, cell) == 0))
            {
                distinct.Add(cell);
            }
        }

        return FormulaValue.FromList(distinct);
    }

    // ---- Dates ----

    /// <summary>Rounds half away from zero (or toward/away from zero). Negative digits round to tens, hundreds, ...</summary>
    private static FormulaValue Round(IReadOnlyList<FormulaValue> args, MidpointRounding mode, bool up = false)
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

        var scale = Math.Pow(10, digits);
        if (up)
        {
            // Away from zero: the ceiling of the magnitude, ignoring binary noise such as 2.1 * 10 = 21.000000000000004.
            var scaled = Math.Round(Math.Abs(number) * scale, 9);
            return FormulaValue.FromNumber(Math.Sign(number) * Math.Ceiling(scaled) / scale);
        }

        if (mode == MidpointRounding.ToZero)
        {
            return FormulaValue.FromNumber(Math.Truncate(Math.Round(number * scale, 9)) / scale);
        }

        if (digits >= 0)
        {
            return FormulaValue.FromNumber(Math.Round(number, digits, mode));
        }

        var tens = Math.Pow(10, -digits);
        return FormulaValue.FromNumber(Math.Round(number / tens, mode) * tens);
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

    private static bool Holidays(IReadOnlyList<FormulaValue> args, int index, out HashSet<DateOnly> holidays, out FormulaValue error)
    {
        holidays = [];
        error = Coerce.ValueError;
        if (args.Count <= index)
        {
            return true;
        }

        foreach (var value in Coerce.Flatten([args[index]]).Where(v => !Coerce.IsBlank(v)))
        {
            if (!Coerce.ToDate(value, out var holiday, out error))
            {
                return false;
            }

            holidays.Add(holiday);
        }

        return true;
    }

    private static bool IsWeekday(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>Weekdays (Mon-Fri) from start to end, inclusive, minus any holidays. Negative when end is before start.</summary>
    private static FormulaValue NetworkDays(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToDate(args[0], out var start, out var error) || !Coerce.ToDate(args[1], out var end, out error))
        {
            return error;
        }

        if (!Holidays(args, 2, out var holidays, out error))
        {
            return error;
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
    }

    /// <summary>The date <c>days</c> working days from <c>date</c> (backwards when negative), skipping weekends and holidays.</summary>
    private static FormulaValue WorkDay(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToDate(args[0], out var date, out var error) || !Coerce.ToInteger(args[1], out var days, out error))
        {
            return error;
        }

        if (!Holidays(args, 2, out var holidays, out error))
        {
            return error;
        }

        if (Math.Abs(days) > 100_000)
        {
            return Coerce.ValueError;
        }

        var step = days < 0 ? -1 : 1;
        for (var left = Math.Abs(days); left > 0;)
        {
            if (date.DayNumber + step < DateOnly.MinValue.DayNumber || date.DayNumber + step > DateOnly.MaxValue.DayNumber)
            {
                return Coerce.ValueError;
            }

            date = date.AddDays(step);
            if (IsWeekday(date) && !holidays.Contains(date))
            {
                left--;
            }
        }

        return FormulaValue.FromDate(date);
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

    /// <summary>NETDAYS(start, end): calendar days counting both ends (1 for the same day).</summary>
    private static FormulaValue NetDays(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToDate(args[0], out var start, out var error) || !Coerce.ToDate(args[1], out var end, out error))
        {
            return error;
        }

        var days = end.DayNumber - start.DayNumber;
        return FormulaValue.FromNumber(days >= 0 ? days + 1 : days - 1);
    }

    // ---- Text ----

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

    private static FormulaValue Mid(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToText(args[0], out var text, out var error)
            || !Coerce.ToInteger(args[1], out var start, out error)
            || !Coerce.ToInteger(args[2], out var count, out error))
        {
            return error;
        }

        if (start < 1 || count < 0)
        {
            return Coerce.ValueError;
        }

        if (start > text.Length)
        {
            return FormulaValue.FromText("");
        }

        return FormulaValue.FromText(text.Substring(start - 1, Math.Min(count, text.Length - start + 1)));
    }

    private static FormulaValue Find(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToText(args[0], out var search, out var error) || !Coerce.ToText(args[1], out var text, out error))
        {
            return error;
        }

        var start = 1;
        if (args.Count > 2 && !Coerce.ToInteger(args[2], out start, out error))
        {
            return error;
        }

        if (start < 1 || start > text.Length + 1)
        {
            return Coerce.ValueError;
        }

        return FormulaValue.FromNumber(text.IndexOf(search, start - 1, StringComparison.Ordinal) + 1);
    }

    private static FormulaValue Substitute(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToText(args[0], out var text, out var error)
            || !Coerce.ToText(args[1], out var old, out error)
            || !Coerce.ToText(args[2], out var replacement, out error))
        {
            return error;
        }

        if (old.Length == 0)
        {
            return FormulaValue.FromText(text);
        }

        if (args.Count < 4)
        {
            return FormulaValue.FromText(text.Replace(old, replacement, StringComparison.Ordinal));
        }

        if (!Coerce.ToInteger(args[3], out var which, out error))
        {
            return error;
        }

        if (which < 1)
        {
            return Coerce.ValueError;
        }

        var index = -1;
        for (var n = 0; n < which; n++)
        {
            index = text.IndexOf(old, index + 1, StringComparison.Ordinal);
            if (index < 0)
            {
                return FormulaValue.FromText(text);
            }
        }

        return FormulaValue.FromText(string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + old.Length)));
    }

    private static FormulaValue Replace(IReadOnlyList<FormulaValue> args)
    {
        if (!Coerce.ToText(args[0], out var text, out var error)
            || !Coerce.ToInteger(args[1], out var start, out error)
            || !Coerce.ToInteger(args[2], out var count, out error)
            || !Coerce.ToText(args[3], out var replacement, out error))
        {
            return error;
        }

        if (start < 1 || count < 0)
        {
            return Coerce.ValueError;
        }

        var from = Math.Min(start - 1, text.Length);
        var length = Math.Min(count, text.Length - from);
        return FormulaValue.FromText(new StringBuilder(text).Remove(from, length).Insert(from, replacement).ToString());
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

    private static FormulaValue ToValue(FormulaValue value)
    {
        switch (value.Kind)
        {
            case FormulaValueKind.Number or FormulaValueKind.Error:
                return value;
            case FormulaValueKind.Blank:
                return FormulaValue.FromNumber(0);
            case FormulaValueKind.Text:
                var cleaned = value.Text.Trim().Replace("$", "").Replace(",", "").Replace(" ", "");
                var negative = cleaned.StartsWith('(') && cleaned.EndsWith(')');
                if (negative)
                {
                    cleaned = cleaned[1..^1];
                }

                var percent = cleaned.EndsWith('%');
                if (percent)
                {
                    cleaned = cleaned[..^1];
                }

                if (!FormulaValue.TryParseNumber(cleaned, out var number))
                {
                    return Coerce.ValueError;
                }

                number = (negative ? -number : number) / (percent ? 100 : 1);
                return FormulaValue.FromNumber(number);
            default:
                return Coerce.ValueError;
        }
    }
}
