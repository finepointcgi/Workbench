using System.Globalization;

namespace Workbench.Contracts.Sheets;

/// <summary>The symbol sets a <see cref="ColumnKind.Symbol"/> column can use (its <c>Options[0]</c>).</summary>
public static class SymbolSets
{
    public const string Rag = "rag";
    public const string Flag = "flag";
    public const string Stars = "stars";
    public const string Priority = "priority";

    public static IReadOnlyList<string> All { get; } = [Rag, Flag, Stars, Priority];

    /// <summary>What the column dialog calls a set.</summary>
    public static string Name(string? set) => set switch
    {
        Flag => "Flag",
        Stars => "Stars (1 to 5)",
        Priority => "Priority (critical, high, medium, low)",
        _ => "Red / yellow / green",
    };

    /// <summary>The set of a column (red/yellow/green when it names none).</summary>
    public static string Of(ColumnDto column) => column.Options.Count > 0 && All.Contains(column.Options[0]) ? column.Options[0] : Rag;

    /// <summary>The symbols of a set, in sort order. Every symbol has a text label; color is never the only signal.</summary>
    public static IReadOnlyList<SymbolOption> Options(string? set) => set switch
    {
        Flag => FlagOptions,
        Stars => StarOptions,
        Priority => PriorityOptions,
        _ => RagOptions,
    };

    private static readonly IReadOnlyList<SymbolOption> RagOptions =
    [
        new("Red", "●", "Red", StatusColors.Red),
        new("Yellow", "●", "Yellow", StatusColors.Amber),
        new("Green", "●", "Green", StatusColors.Green),
    ];

    private static readonly IReadOnlyList<SymbolOption> FlagOptions = [new("Flagged", "⚑", "Flagged", StatusColors.Red)];

    private static readonly IReadOnlyList<SymbolOption> StarOptions =
        Enumerable.Range(1, 5).Select(n => new SymbolOption(
            n.ToString(CultureInfo.InvariantCulture), new string('★', n) + new string('☆', 5 - n), n == 1 ? "1 star" : $"{n} stars", StatusColors.Amber)).ToList();

    private static readonly IReadOnlyList<SymbolOption> PriorityOptions =
    [
        new("Critical", "▲▲", "Critical", StatusColors.Red),
        new("High", "▲", "High", StatusColors.Orange),
        new("Medium", "■", "Medium", StatusColors.Amber),
        new("Low", "▼", "Low", StatusColors.Blue),
    ];

    /// <summary>The symbol stored as <paramref name="value"/> in any set (values are unique across sets).</summary>
    public static SymbolOption? Find(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : All.SelectMany(Options).FirstOrDefault(o => string.Equals(o.Value, value.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One symbol: the stored <paramref name="Value"/>, the <paramref name="Glyph"/> drawn (decorative), the text
/// <paramref name="Label"/> always shown next to it and read by screen readers, and its color.
/// </summary>
public sealed record SymbolOption(string Value, string Glyph, string Label, StatusColor Color);

/// <summary>
/// Stored values of the column types added after the first ones (multi-select, hyperlink, symbol, progress, auto-number and
/// the system columns): parsing what people type or paste, the plain text they show as, and how they sort. The grid
/// (<c>CellFormat</c>), the bulk save, forms, exports and the view engine all use these, so a value means the same everywhere.
/// </summary>
public static class ColumnValues
{
    private const string StampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Columns Workbench fills in by itself: never typed, pasted or filled.</summary>
    public static bool IsAutomatic(ColumnKind kind) => kind is ColumnKind.AutoNumber or ColumnKind.CreatedBy or ColumnKind.CreatedDate
        or ColumnKind.ModifiedBy or ColumnKind.ModifiedDate;

    /// <summary>Created by / Created date / Modified by / Modified date.</summary>
    public static bool IsSystem(ColumnKind kind) => kind is ColumnKind.CreatedBy or ColumnKind.CreatedDate or ColumnKind.ModifiedBy or ColumnKind.ModifiedDate;

    /// <summary>Values that are a moment in time (stored in UTC with the time).</summary>
    public static bool IsTimestamp(ColumnKind kind) => kind is ColumnKind.CreatedDate or ColumnKind.ModifiedDate;

    /// <summary>Types whose values compare as dates (filters, sorting, rules).</summary>
    public static bool IsDateLike(ColumnKind kind) => kind == ColumnKind.Date || IsTimestamp(kind);

    /// <summary>Types whose values compare as numbers.</summary>
    public static bool IsNumberLike(ColumnKind kind) => kind is ColumnKind.Number or ColumnKind.Currency or ColumnKind.Progress;

    /// <summary>The labels a person picks from: a status column's or multi-select's options, a symbol set's values.</summary>
    public static IReadOnlyList<string> Choices(ColumnDto column) => column.Type switch
    {
        ColumnKind.Dropdown or ColumnKind.MultiSelect => column.Options,
        ColumnKind.Symbol => SymbolSets.Options(SymbolSets.Of(column)).Select(o => o.Value).ToList(),
        _ => [],
    };

    // ---- Multi-select ----

    /// <summary>The options chosen in a multi-select cell.</summary>
    public static IReadOnlyList<string> Selected(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? [] : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? JoinSelected(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join('\n', list);
    }

    // ---- Hyperlink ----

    /// <summary>A link cell's URL and the text it shows (the URL when it has no label).</summary>
    public static (string Url, string Text)? Link(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith('[') && text.EndsWith(')') && text.LastIndexOf("](", StringComparison.Ordinal) is var split and > 0)
        {
            var url = text[(split + 2)..^1].Trim();
            var label = text[1..split].Trim();
            return SafeUrl(url) is { } safe ? (safe, label.Length == 0 ? safe : label) : null;
        }

        return SafeUrl(text) is { } plain ? (plain, plain) : null;
    }

    /// <summary>http, https and mailto links only (never javascript: or data:); a bare host gets https://.</summary>
    public static string? SafeUrl(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Length > 7 && trimmed.Contains('@') ? trimmed : null;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (trimmed.Contains('@') && !trimmed.Contains('/'))
            {
                return "mailto:" + trimmed;
            }

            if (!trimmed.Contains('.') || trimmed.Contains(':') && !trimmed.Contains('/'))
            {
                return null;
            }

            trimmed = "https://" + trimmed;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? trimmed : null;
    }

    // ---- Progress ----

    public static decimal? Percent(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 0, 100) : null;

    // ---- System columns ----

    /// <summary>The stored form of a system date (UTC, to the second).</summary>
    public static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(StampFormat, CultureInfo.InvariantCulture);

    public static DateTime? ParseStamp(string? raw) =>
        DateTime.TryParseExact(raw, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : null;

    /// <summary>The calendar day of a date or system date value (local time for system dates).</summary>
    public static DateOnly? Day(string? raw) =>
        DateOnly.TryParseExact(raw, ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : ParseStamp(raw) is { } utc ? DateOnly.FromDateTime(utc.ToLocalTime())
        : null;

    // ---- Auto-number ----

    /// <summary>An auto-number column's prefix, digits and first number (<c>Options</c>), with sensible defaults.</summary>
    public static (string Prefix, int Digits, int Start) AutoNumberSettings(IReadOnlyList<string> options) => (
        options.Count > 0 ? options[0].Trim() : string.Empty,
        options.Count > 1 && int.TryParse(options[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var digits) ? Math.Clamp(digits, 0, 10) : 0,
        options.Count > 2 && int.TryParse(options[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? Math.Max(0, start) : 1);

    public static string AutoNumber(IReadOnlyList<string> options, int number)
    {
        var (prefix, digits, _) = AutoNumberSettings(options);
        return prefix + number.ToString(new string('0', Math.Max(1, digits)), CultureInfo.InvariantCulture);
    }

    // ---- Shared ----

    /// <summary>
    /// Parses typed or pasted text for one of these types into its stored form (null clears). <paramref name="options"/>
    /// are the column's options when known (multi-select choices, the symbol set); without them any known value is taken.
    /// </summary>
    public static bool TryNormalize(string trimmed, ColumnKind kind, IReadOnlyList<string>? options, out string? value, out string? error)
    {
        value = null;
        error = null;
        switch (kind)
        {
            case ColumnKind.MultiSelect:
                // One per line, or comma-separated; a choice that itself has a comma still matches whole.
                var parts = options?.FirstOrDefault(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase)) is { } whole
                    ? [whole]
                    : trimmed.Split(trimmed.Contains('\n') ? ['\n'] : [',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                value = JoinSelected(parts.Select(p => options?.FirstOrDefault(o => string.Equals(o, p, StringComparison.OrdinalIgnoreCase)) ?? p));
                return true;

            case ColumnKind.Hyperlink:
                if (Link(trimmed) is { } link)
                {
                    value = link.Text == link.Url ? link.Url : $"[{link.Text.Replace("]", ")", StringComparison.Ordinal)}]({link.Url})";
                    return true;
                }

                error = $"\"{trimmed}\" isn't a web address. Use one like https://example.com.";
                return false;

            case ColumnKind.Symbol:
                var set = options is { Count: > 0 } && SymbolSets.All.Contains(options[0]) ? options[0] : null;
                var candidates = set is null ? SymbolSets.All.SelectMany(SymbolSets.Options).ToList() : SymbolSets.Options(set);
                var word = trimmed.ToLowerInvariant();
                var match = candidates.FirstOrDefault(o => string.Equals(o.Value, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o.Label, trimmed, StringComparison.OrdinalIgnoreCase) || o.Glyph == trimmed);
                if (match is null && set is SymbolSets.Flag or null && word is "true" or "yes" or "y" or "x" or "1" or "flag" or "⚑")
                {
                    match = SymbolSets.Options(SymbolSets.Flag)[0];
                }
                else if (set is SymbolSets.Flag && word is "false" or "no" or "n" or "0" or "-")
                {
                    return true; // unflagged
                }
                else if (match is null && trimmed.All(c => c is '★' or '☆') && trimmed.Count(c => c == '★') is var stars and >= 1 and <= 5)
                {
                    match = SymbolSets.Options(SymbolSets.Stars)[stars - 1];
                }
                else if (match is null && set is null or SymbolSets.Rag && word is "amber" or "r" or "y" or "g")
                {
                    match = SymbolSets.Options(SymbolSets.Rag).First(o => o.Value[0] == (word == "amber" ? 'Y' : char.ToUpperInvariant(word[0])));
                }

                if (match is null)
                {
                    var allowed = string.Join(", ", (set is null ? SymbolSets.Options(SymbolSets.Rag) : SymbolSets.Options(set)).Select(o => o.Label));
                    error = $"\"{trimmed}\" isn't one of the symbols ({allowed}).";
                    return false;
                }

                value = match.Value;
                return true;

            case ColumnKind.Progress:
                var percentSign = trimmed.EndsWith('%');
                var number = trimmed.TrimEnd('%').Trim();
                if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
                {
                    error = $"\"{trimmed}\" isn't a percentage. Use a number from 0 to 100, like 40 or 40%.";
                    return false;
                }

                // 0.4 (what Excel copies for 40% formatted as a number) is 40%; 1 stays 1%.
                if (!percentSign && percent is > 0 and < 1)
                {
                    percent *= 100;
                }

                if (percent is < 0 or > 100)
                {
                    error = $"{trimmed} is outside 0 to 100%.";
                    return false;
                }

                value = Math.Round(percent, 2).ToString("0.##", CultureInfo.InvariantCulture);
                return true;

            case ColumnKind.AutoNumber or ColumnKind.CreatedBy or ColumnKind.CreatedDate or ColumnKind.ModifiedBy or ColumnKind.ModifiedDate:
                error = "This column is filled in automatically.";
                return false;

            default:
                value = trimmed;
                return true;
        }
    }

    /// <summary>The plain text a value shows as (exports, clipboard, screen readers), or null for the older types.</summary>
    public static string? Text(string raw, ColumnKind kind) => kind switch
    {
        ColumnKind.MultiSelect => string.Join(", ", Selected(raw)),
        ColumnKind.Hyperlink => Link(raw)?.Text ?? raw,
        ColumnKind.Symbol => SymbolSets.Find(raw)?.Label ?? raw,
        ColumnKind.Progress => Percent(raw) is { } p ? p.ToString("0.##", CultureInfo.InvariantCulture) + "%" : raw,
        ColumnKind.CreatedDate or ColumnKind.ModifiedDate => ParseStamp(raw) is { } utc
            ? utc.ToLocalTime().ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture)
            : raw,
        _ => null,
    };

    /// <summary>Compares two non-blank values of these types; null for the older types (the caller compares those).</summary>
    public static int? Compare(string a, string b, ColumnKind kind, ColumnDto? column) => kind switch
    {
        ColumnKind.Progress when Percent(a) is { } x && Percent(b) is { } y => x.CompareTo(y),
        ColumnKind.CreatedDate or ColumnKind.ModifiedDate when ParseStamp(a) is { } x && ParseStamp(b) is { } y => x.CompareTo(y),
        ColumnKind.Symbol => SymbolRank(a).CompareTo(SymbolRank(b)),
        ColumnKind.MultiSelect when column is not null => CompareSelected(a, b, column),
        ColumnKind.AutoNumber => CompareNatural(a, b),
        _ => null,
    };

    /// <summary>A symbol's place in the order of all sets (each set in its own order; stars by count).</summary>
    private static int SymbolRank(string value)
    {
        var index = AllSymbols.FindIndex(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private static readonly List<SymbolOption> AllSymbols = SymbolSets.All.SelectMany(SymbolSets.Options).ToList();

    private static int CompareSelected(string a, string b, ColumnDto column)
    {
        int Rank(string raw) => Selected(raw).Select(v => column.Options.ToList().FindIndex(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min();
        var result = Rank(a).CompareTo(Rank(b));
        return result != 0 ? result : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>REQ-9 before REQ-10: compares the trailing numbers when the prefixes match.</summary>
    private static int CompareNatural(string a, string b)
    {
        static (string Prefix, long? Number) Split(string s)
        {
            var i = s.Length;
            while (i > 0 && char.IsAsciiDigit(s[i - 1]))
            {
                i--;
            }

            return (s[..i], i < s.Length && long.TryParse(s[i..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null);
        }

        var (pa, na) = Split(a);
        var (pb, nb) = Split(b);
        var prefix = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
        return prefix != 0 ? prefix : na is { } x && nb is { } y ? x.CompareTo(y) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
