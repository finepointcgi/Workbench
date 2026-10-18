using System.Globalization;
using Workbench.Contracts.Reports;

namespace Workbench.Application.Reports;

/// <summary>Turns typed values into the invariant strings report rows carry, and back.</summary>
public static class ReportValues
{
    public static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double f => ((decimal)f).ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => DateOnly.FromDateTime(t).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Enum e => Words(e.ToString()),
        Guid g => g.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    public static decimal? Number(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static bool IsNumeric(ReportColumnType type) =>
        type is ReportColumnType.Number or ReportColumnType.Money or ReportColumnType.Hours or ReportColumnType.Rate or ReportColumnType.Percent;

    /// <summary><c>CostPlusFixedFee</c> → <c>Cost plus fixed fee</c>.</summary>
    public static string Words(string pascal)
    {
        if (pascal.Length < 2)
        {
            return pascal;
        }

        var text = new System.Text.StringBuilder(pascal.Length + 8);
        text.Append(pascal[0]);
        for (var i = 1; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && !char.IsUpper(pascal[i - 1]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>Money rounded to cents.</summary>
    public static decimal Cents(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>A / B, or null when B is zero.</summary>
    public static decimal? Ratio(decimal numerator, decimal denominator) =>
        denominator == 0 ? null : decimal.Round(numerator / denominator, 6, MidpointRounding.AwayFromZero);
}

/// <summary>Builds a <see cref="ReportResultDto"/> one row at a time.</summary>
public sealed class ReportBuilder(string title, string? subtitle = null)
{
    private readonly List<ReportColumnDto> _columns = [];
    private readonly List<ReportRowDto> _rows = [];
    private readonly List<string> _notes = [];

    public string Title { get; set; } = title;

    public string? Subtitle { get; set; } = subtitle;

    public bool Truncated { get; set; }

    public int RowCount => _rows.Count;

    public ReportBuilder Column(string key, string label, ReportColumnType type = ReportColumnType.Text)
    {
        _columns.Add(new ReportColumnDto(key, label, type));
        return this;
    }

    public ReportBuilder Row(params object?[] values) => Add(ReportRowKind.Detail, 0, null, null, values);

    public ReportBuilder Add(ReportRowKind kind, int level, string? drillKey, string? link, params object?[] values)
    {
        if (values.Length != _columns.Count)
        {
            throw new InvalidOperationException($"{Title}: {values.Length} values for {_columns.Count} columns.");
        }

        _rows.Add(new ReportRowDto(kind, level, values.Select(ReportValues.Format).ToList(), drillKey, link));
        return this;
    }

    public ReportBuilder Note(string note)
    {
        if (!_notes.Contains(note))
        {
            _notes.Add(note);
        }

        return this;
    }

    public ReportBuilder Notes(IEnumerable<string> notes)
    {
        foreach (var n in notes)
        {
            Note(n);
        }

        return this;
    }

    public ReportResultDto Build(DateTime generatedAtUtc) =>
        new(Title, Subtitle, _columns.ToList(), _rows.ToList(), _notes.ToList(), generatedAtUtc, Truncated);
}
