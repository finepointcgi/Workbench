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

    private FormulaValue(FormulaValueKind kind, double number = 0, string? text = null, bool boolean = false,
        DateOnly date = default, IReadOnlyList<FormulaValue>? items = null)
    {
        Kind = kind;
        _number = number;
        _text = text;
        _boolean = boolean;
        _date = date;
        _items = items;
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

    public bool IsError => Kind == FormulaValueKind.Error;

    public static FormulaValue FromNumber(double value) =>
        double.IsFinite(value) ? new(FormulaValueKind.Number, number: value == 0 ? 0 : value) : FromError(FormulaErrors.Value);

    public static FormulaValue FromText(string value) => new(FormulaValueKind.Text, text: value);

    public static FormulaValue FromBoolean(bool value) => value ? True : False;

    public static FormulaValue FromDate(DateOnly value) => new(FormulaValueKind.Date, date: value);

    public static FormulaValue FromError(string code) => new(FormulaValueKind.Error, text: code);

    public static FormulaValue FromList(IReadOnlyList<FormulaValue> items) => new(FormulaValueKind.List, items: items);

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
            FormulaValueKind.List => _items!.SequenceEqual(other._items!),
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
