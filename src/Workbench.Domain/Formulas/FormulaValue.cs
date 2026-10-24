using System.Globalization;

namespace Workbench.Domain.Formulas;

public enum FormulaValueKind
{
    Blank,
    Number,
    Text,
    Boolean,
    Date,
    Error,

    /// <summary>A set of values, such as the result of <c>CHILDREN([Column])</c>. Only aggregate functions accept it.</summary>
    List,
}

/// <summary>Error values shown in formula cells.</summary>
public static class FormulaErrors
{
    public const string Ref = "#REF!";
    public const string DivideByZero = "#DIV/0!";
    public const string Value = "#VALUE!";
    public const string Name = "#NAME?";
    public const string Circular = "#CIRCULAR!";

    /// <summary>MATCH, VLOOKUP or INDEX found nothing.</summary>
    public const string NoMatch = "#NO MATCH";

    /// <summary>The formula reads a sheet (or a column of one) the viewer can't see.</summary>
    public const string NoAccess = "#NO ACCESS";

    /// <summary>What an error value means, in plain words, for the cell's tooltip and the formula editor.</summary>
    public static string Explain(string? code) => code switch
    {
        Ref => "This formula uses a column or reference that doesn't exist. It may have been deleted, or renamed outside Workbench.",
        DivideByZero => "This formula divides by zero, or by a blank cell, so there's no answer.",
        Value => "A value isn't the right kind here: for example text where a number or date is needed.",
        Name => "This formula can't be read. Open the column's formula to fix it.",
        Circular => "Two or more formulas depend on each other, so none of them can be worked out.",
        NoMatch => "The value you looked for isn't in the range.",
        NoAccess => "This formula reads another sheet that you don't have access to, so its value is hidden from you.",
        _ => "This formula couldn't be worked out.",
    };

    /// <summary>True for every error value a formula can produce.</summary>
    public static bool IsError(string? value) =>
        value is Ref or DivideByZero or Value or Name or Circular or NoMatch or NoAccess;
}

/// <summary>An immutable value produced or consumed by the formula evaluator.</summary>
public sealed class FormulaValue : IEquatable<FormulaValue>
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    private readonly double _number;
    private readonly string? _text;
    private readonly bool _boolean;
    private readonly DateOnly _date;
    private readonly IReadOnlyList<FormulaValue>? _items;
    private readonly int _width;

    private FormulaValue(FormulaValueKind kind, double number = 0, string? text = null, bool boolean = false,
        DateOnly date = default, IReadOnlyList<FormulaValue>? items = null, int width = 1)
    {
        Kind = kind;
        _number = number;
        _text = text;
        _boolean = boolean;
        _date = date;
        _items = items;
        _width = width;
    }

    public static FormulaValue Blank { get; } = new(FormulaValueKind.Blank);

    public static FormulaValue True { get; } = new(FormulaValueKind.Boolean, boolean: true);

    public static FormulaValue False { get; } = new(FormulaValueKind.Boolean, boolean: false);

    public FormulaValueKind Kind { get; }

    public double Number => Kind == FormulaValueKind.Number ? _number : throw WrongKind(FormulaValueKind.Number);

    public string Text => Kind == FormulaValueKind.Text ? _text! : throw WrongKind(FormulaValueKind.Text);

    public bool Boolean => Kind == FormulaValueKind.Boolean ? _boolean : throw WrongKind(FormulaValueKind.Boolean);

    public DateOnly Date => Kind == FormulaValueKind.Date ? _date : throw WrongKind(FormulaValueKind.Date);

    /// <summary>The error code, such as <see cref="FormulaErrors.Ref"/>.</summary>
    public string Error => Kind == FormulaValueKind.Error ? _text! : throw WrongKind(FormulaValueKind.Error);

    public IReadOnlyList<FormulaValue> Items => Kind == FormulaValueKind.List ? _items! : throw WrongKind(FormulaValueKind.List);

    /// <summary>How many columns a list has: 1 for a column or CHILDREN, more for a multi-column range (items row by row).</summary>
    public int Width => Kind == FormulaValueKind.List ? _width : throw WrongKind(FormulaValueKind.List);

    /// <summary>How many rows a list has.</summary>
    public int RowCount => Kind == FormulaValueKind.List ? _items!.Count / _width : throw WrongKind(FormulaValueKind.List);

    /// <summary>A list's value at a zero-based row and column.</summary>
    public FormulaValue At(int row, int column) => Items[row * _width + column];

    public bool IsError => Kind == FormulaValueKind.Error;

    public static FormulaValue FromNumber(double value) =>
        double.IsFinite(value) ? new(FormulaValueKind.Number, number: value == 0 ? 0 : value) : FromError(FormulaErrors.Value);

    public static FormulaValue FromText(string value) => new(FormulaValueKind.Text, text: value);

    public static FormulaValue FromBoolean(bool value) => value ? True : False;

    public static FormulaValue FromDate(DateOnly value) => new(FormulaValueKind.Date, date: value);

    public static FormulaValue FromError(string code) => new(FormulaValueKind.Error, text: code);

    public static FormulaValue FromList(IReadOnlyList<FormulaValue> items) => new(FormulaValueKind.List, items: items);

    /// <summary>A multi-column range: <paramref name="cells"/> row by row, <paramref name="width"/> per row.</summary>
    public static FormulaValue FromTable(IReadOnlyList<FormulaValue> cells, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        if (cells.Count % width != 0)
        {
            throw new ArgumentException("A table needs the same number of cells on every row.", nameof(cells));
        }

        return new(FormulaValueKind.List, items: cells, width: width);
    }

    /// <summary>
    /// Reads a stored cell value (see docs/CONVENTIONS.md) as a typed value. Empty cells are blank, and values that
    /// don't match the column type (for example "n/a" in a number column) are read as text.
    /// </summary>
    public static FormulaValue FromCell(string? raw, ColumnType type)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Blank;
        }

        switch (type)
        {
            case ColumnType.Number or ColumnType.Currency when TryParseNumber(raw, out var number):
                return FromNumber(number);
            case ColumnType.Date when TryParseDate(raw, out var date):
                return FromDate(date);
            case ColumnType.Checkbox when bool.TryParse(raw.Trim(), out var boolean):
                return FromBoolean(boolean);
            case ColumnType.Lookup:
                // A looked-up value whose source type isn't known here: read numbers and dates as such.
                return TryParseNumber(raw, out var n) ? FromNumber(n) : TryParseDate(raw, out var d) ? FromDate(d) : FromText(raw);
            case ColumnType.MultiSelect:
                // "Design, Build": what the cell shows, so HAS/CONTAINS and text functions read it naturally.
                return FromText(string.Join(", ", raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            case ColumnType.Hyperlink:
                // The address: [label](url) reads as url.
                var link = raw.Trim();
                return FromText(link.StartsWith('[') && link.EndsWith(')') && link.LastIndexOf("](", StringComparison.Ordinal) is var at and > 0 ? link[(at + 2)..^1] : link);
            case ColumnType.Symbol:
                // Stars are their count; other symbols their label ("Red", "High", "Flagged").
                return TryParseNumber(raw, out var stars) ? FromNumber(stars) : FromText(raw.Trim());
            case ColumnType.Progress when TryParseNumber(raw, out var percent):
                // Like a % complete in Smartsheet or Excel: 40% is 0.4, so [Progress] * [Budget] is the earned part.
                return FromNumber(percent / 100);
            case ColumnType.CreatedDate or ColumnType.ModifiedDate
                when DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp):
                return FromDate(DateOnly.FromDateTime(stamp.ToLocalTime()));
            default:
                return FromText(raw);
        }
    }

    /// <summary>Formats the value as a stored cell string, or null for blank.</summary>
    public string? ToCellString() => Kind switch
    {
        FormulaValueKind.Blank => null,
        FormulaValueKind.Number => FormatNumber(_number),
        FormulaValueKind.Text => _text,
        FormulaValueKind.Boolean => _boolean ? "true" : "false",
        FormulaValueKind.Date => _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FormulaValueKind.Error => _text,
        _ => FormulaErrors.Value,
    };

    public override string ToString() => Kind switch
    {
        FormulaValueKind.Blank => "(blank)",
        FormulaValueKind.Text => $"\"{_text}\"",
        FormulaValueKind.List => $"[{string.Join(", ", _items!)}]",
        _ => ToCellString()!,
    };

    public bool Equals(FormulaValue? other) =>
        other is not null && Kind == other.Kind && Kind switch
        {
            FormulaValueKind.Number => _number.Equals(other._number),
            FormulaValueKind.Text or FormulaValueKind.Error => _text == other._text,
            FormulaValueKind.Boolean => _boolean == other._boolean,
            FormulaValueKind.Date => _date == other._date,
            FormulaValueKind.List => _width == other._width && _items!.SequenceEqual(other._items!),
            _ => true,
        };

    public override bool Equals(object? obj) => obj is FormulaValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _number, _text, _boolean, _date);

    internal static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    internal static bool TryParseDate(string text, out DateOnly value) =>
        DateOnly.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    internal static string FormatNumber(double value) =>
        // 15 significant digits hides binary noise such as 0.1 + 0.2 = 0.30000000000000004.
        value.ToString("G15", CultureInfo.InvariantCulture);

    private InvalidOperationException WrongKind(FormulaValueKind expected) => new($"Value is {Kind}, not {expected}.");
}
