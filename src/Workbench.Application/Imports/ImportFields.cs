using System.Globalization;
using Workbench.Contracts.Imports;

namespace Workbench.Application.Imports;

/// <summary>A target field of an import kind. <see cref="Aliases"/> are the header spellings auto-mapped to it.</summary>
public sealed record ImportField(string Name, string Label, bool Required, string Description, params string[] Aliases)
{
    public ImportFieldDto ToDto() => new(Name, Label, Required, Description, [Name, .. Aliases]);
}

/// <summary>Matches source headers to fields: case-, space- and punctuation-insensitive, by name or alias.</summary>
public static class FieldMatcher
{
    public static string Normalize(string? header) =>
        new((header ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Maps each field to the first unused header matching its name or an alias (in alias order).</summary>
    public static Dictionary<string, string?> AutoMap(IEnumerable<ImportField> fields, IReadOnlyList<string> headers)
    {
        var byNormal = new Dictionary<string, string>();
        foreach (var header in headers)
        {
            byNormal.TryAdd(Normalize(header), header);
        }

        var used = new HashSet<string>();
        var mapping = new Dictionary<string, string?>();
        foreach (var field in fields)
        {
            string? match = null;
            foreach (var candidate in new[] { field.Name, field.Label }.Concat(field.Aliases))
            {
                if (byNormal.TryGetValue(Normalize(candidate), out var header) && !used.Contains(header))
                {
                    match = header;
                    break;
                }
            }

            if (match is not null)
            {
                used.Add(match);
            }

            mapping[field.Name] = match;
        }

        return mapping;
    }
}

/// <summary>A source row seen through the mapping, with typed accessors that report bad values as row errors.</summary>
public sealed class MappedRow(TabularFile file, TabularRow row, IReadOnlyDictionary<string, string?> mapping, ImportRun run)
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "M/d/yyyy H:mm", "M/d/yyyy h:mm:ss tt",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "yyyyMMdd", "MMM d, yyyy", "MMM dd, yyyy",
    ];

    public int Number => row.Number;
    public string FileName => file.FileName;
    public int Level => row.Level;
    public bool HasErrors { get; private set; }

    /// <summary>The trimmed value of a mapped field, or null when the field isn't mapped or the cell is blank.</summary>
    public string? Get(string field)
    {
        if (!mapping.TryGetValue(field, out var header) || header is null)
        {
            return null;
        }

        var index = IndexOf(file.Headers, header);
        if (index < 0 || index >= row.Cells.Count)
        {
            return null;
        }

        var value = row.Cells[index].Trim();
        return value.Length == 0 ? null : value;
    }

    public bool IsMapped(string field) => mapping.TryGetValue(field, out var h) && h is not null && IndexOf(file.Headers, h) >= 0;

    public string? Required(string field)
    {
        var value = Get(field);
        if (value is null)
        {
            Error(field, $"{field} is required.");
        }

        return value;
    }

    public decimal? Decimal(string field)
    {
        var text = Get(field);
        if (text is null)
        {
            return null;
        }

        var cleaned = text.Replace("$", "").Replace(",", "").Trim();
        var negative = cleaned.StartsWith('(') && cleaned.EndsWith(')');
        if (negative)
        {
            cleaned = cleaned[1..^1];
        }

        if (cleaned.EndsWith('%'))
        {
            cleaned = cleaned[..^1];
        }

        if (decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value))
        {
            return negative ? -value : value;
        }

        Error(field, $"{field} '{text}' isn't a number.");
        return null;
    }

    public DateOnly? Date(string field)
    {
        var text = Get(field);
        if (text is null)
        {
            return null;
        }

        if (ParseDate(text) is { } date)
        {
            return date;
        }

        Error(field, $"{field} '{text}' isn't a date (use yyyy-MM-dd or MM/dd/yyyy).");
        return null;
    }

    public bool? Bool(string field)
    {
        var text = Get(field);
        if (text is null)
        {
            return null;
        }

        if (ParseBool(text) is { } value)
        {
            return value;
        }

        Error(field, $"{field} '{text}' isn't Y/N.");
        return null;
    }

    public void Error(string? field, string message)
    {
        HasErrors = true;
        run.Error(file.FileName, row.Number, field, message);
    }

    public void Warning(string? field, string message) => run.Warning(file.FileName, row.Number, field, message);

    public static DateOnly? ParseDate(string text)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            return date;
        }

        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime))
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out dateTime) ? DateOnly.FromDateTime(dateTime) : null;
    }

    public static bool? ParseBool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "Y" or "YES" or "TRUE" or "T" or "1" or "A" or "ACTIVE" or "OPEN" or "X" or "CHECKED" => true,
        "N" or "NO" or "FALSE" or "F" or "0" or "I" or "INACTIVE" or "CLOSED" or "" => false,
        _ => null,
    };

    private static int IndexOf(IReadOnlyList<string> headers, string header)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i], header, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
