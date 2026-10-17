using System.Globalization;

namespace Workbench.Domain.Formulas;

/// <summary>Type conversions used by operators and functions. Each returns false with an error value on failure.</summary>
internal static class Coerce
{
    public static FormulaValue ValueError { get; } = FormulaValue.FromError(FormulaErrors.Value);

    /// <summary>Blank is 0, booleans are 1/0 and numeric text is read as a number.</summary>
    public static bool ToNumber(FormulaValue value, out double number, out FormulaValue error)
    {
        error = ValueError;
        number = 0;
        switch (value.Kind)
        {
            case FormulaValueKind.Number:
                number = value.Number;
                return true;
            case FormulaValueKind.Blank:
                return true;
            case FormulaValueKind.Boolean:
                number = value.Boolean ? 1 : 0;
                return true;
            case FormulaValueKind.Text when FormulaValue.TryParseNumber(value.Text, out number):
                return true;
            case FormulaValueKind.Error:
                error = value;
                return false;
            default:
                return false;
        }
    }

    public static bool ToText(FormulaValue value, out string text, out FormulaValue error)
    {
        error = value.IsError ? value : ValueError;
        text = value.Kind switch
        {
            FormulaValueKind.Blank => "",
            FormulaValueKind.Error or FormulaValueKind.List => null!,
            _ => value.ToCellString()!,
        };
        return text is not null;
    }

    /// <summary>Blank is false, numbers are true when non-zero, and the text "true"/"false" is read as a boolean.</summary>
    public static bool ToBoolean(FormulaValue value, out bool boolean, out FormulaValue error)
    {
        error = ValueError;
        boolean = false;
        switch (value.Kind)
        {
            case FormulaValueKind.Boolean:
                boolean = value.Boolean;
                return true;
            case FormulaValueKind.Blank:
                return true;
            case FormulaValueKind.Number:
                boolean = value.Number != 0;
                return true;
            case FormulaValueKind.Text when bool.TryParse(value.Text.Trim(), out boolean):
                return true;
            case FormulaValueKind.Error:
                error = value;
                return false;
            default:
                return false;
        }
    }

    /// <summary>Dates, or text in <c>yyyy-MM-dd</c> form.</summary>
    public static bool ToDate(FormulaValue value, out DateOnly date, out FormulaValue error)
    {
        error = value.IsError ? value : ValueError;
        date = default;
        if (value.Kind == FormulaValueKind.Date)
        {
            date = value.Date;
            return true;
        }

        return value.Kind == FormulaValueKind.Text && FormulaValue.TryParseDate(value.Text, out date);
    }

    /// <summary>Whole-number argument such as a digit count or day offset; fractions are truncated.</summary>
    public static bool ToInteger(FormulaValue value, out int integer, out FormulaValue error)
    {
        integer = 0;
        if (!ToNumber(value, out var number, out error))
        {
            return false;
        }

        number = Math.Truncate(number);
        if (number is < int.MinValue or > int.MaxValue)
        {
            error = ValueError;
            return false;
        }

        integer = (int)number;
        return true;
    }

    public static bool AddDays(DateOnly date, double days, out FormulaValue result)
    {
        var whole = Math.Truncate(days);
        var target = date.DayNumber + whole;
        if (target < DateOnly.MinValue.DayNumber || target > DateOnly.MaxValue.DayNumber)
        {
            result = ValueError;
            return false;
        }

        result = FormulaValue.FromDate(DateOnly.FromDayNumber((int)target));
        return true;
    }

    /// <summary>Spreads list arguments (from CHILDREN) into their items.</summary>
    public static IEnumerable<FormulaValue> Flatten(IEnumerable<FormulaValue> values) =>
        values.SelectMany(v => v.Kind == FormulaValueKind.List ? v.Items : [v]);

    public static bool IsBlank(FormulaValue value) =>
        value.Kind == FormulaValueKind.Blank || (value.Kind == FormulaValueKind.Text && value.Text.Length == 0);

    /// <summary>
    /// Orders two non-error values, or returns null when they can't be compared (for example a date and a word).
    /// Text compares case-insensitively; blank acts as 0, "" or false next to a number, text or boolean, and sorts before any date.
    /// Numeric or yyyy-MM-dd text compares with numbers or dates.
    /// </summary>
    public static int? Order(FormulaValue left, FormulaValue right)
    {
        if (left.Kind == FormulaValueKind.Blank && right.Kind == FormulaValueKind.Blank)
        {
            return 0;
        }

        if (left.Kind == FormulaValueKind.Blank)
        {
            return right.Kind == FormulaValueKind.Date ? -1 : Order(EmptyLike(right), right);
        }

        if (right.Kind == FormulaValueKind.Blank)
        {
            return left.Kind == FormulaValueKind.Date ? 1 : Order(left, EmptyLike(left));
        }

        return (left.Kind, right.Kind) switch
        {
            (FormulaValueKind.Number, FormulaValueKind.Number) => left.Number.CompareTo(right.Number),
            (FormulaValueKind.Text, FormulaValueKind.Text) =>
                Math.Sign(string.Compare(left.Text, right.Text, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase)),
            (FormulaValueKind.Boolean, FormulaValueKind.Boolean) => left.Boolean.CompareTo(right.Boolean),
            (FormulaValueKind.Date, FormulaValueKind.Date) => left.Date.CompareTo(right.Date),
            (FormulaValueKind.Number, FormulaValueKind.Text) when FormulaValue.TryParseNumber(right.Text, out var n) => left.Number.CompareTo(n),
            (FormulaValueKind.Text, FormulaValueKind.Number) when FormulaValue.TryParseNumber(left.Text, out var n) => n.CompareTo(right.Number),
            (FormulaValueKind.Date, FormulaValueKind.Text) when FormulaValue.TryParseDate(right.Text, out var d) => left.Date.CompareTo(d),
            (FormulaValueKind.Text, FormulaValueKind.Date) when FormulaValue.TryParseDate(left.Text, out var d) => d.CompareTo(right.Date),
            _ => null,
        };
    }

    /// <summary>Applies a comparison operator. Incomparable values are unequal, and ordering them is #VALUE!.</summary>
    public static FormulaValue Compare(BinaryOperator op, FormulaValue left, FormulaValue right)
    {
        if (left.IsError)
        {
            return left;
        }

        if (right.IsError)
        {
            return right;
        }

        if (left.Kind == FormulaValueKind.List || right.Kind == FormulaValueKind.List)
        {
            return ValueError;
        }

        var order = Order(left, right);
        if (order is null)
        {
            return op switch
            {
                BinaryOperator.Equal => FormulaValue.False,
                BinaryOperator.NotEqual => FormulaValue.True,
                _ => ValueError,
            };
        }

        return FormulaValue.FromBoolean(op switch
        {
            BinaryOperator.Equal => order == 0,
            BinaryOperator.NotEqual => order != 0,
            BinaryOperator.Less => order < 0,
            BinaryOperator.LessOrEqual => order <= 0,
            BinaryOperator.Greater => order > 0,
            BinaryOperator.GreaterOrEqual => order >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        });
    }

    private static FormulaValue EmptyLike(FormulaValue value) => value.Kind switch
    {
        FormulaValueKind.Number => FormulaValue.FromNumber(0),
        FormulaValueKind.Text => FormulaValue.FromText(""),
        FormulaValueKind.Boolean => FormulaValue.False,
        _ => value,
    };
}
