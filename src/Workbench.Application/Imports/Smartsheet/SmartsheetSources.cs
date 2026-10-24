using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Workbench.Application.Imports.Smartsheet;

/// <summary>Turns a Smartsheet API sheet or a Smartsheet .xlsx export into a <see cref="SourceSheet"/>.</summary>
public static partial class SmartsheetSources
{
    public const string PrimaryTag = "PRIMARY";
    public static readonly string[] SystemColumnTypes = ["AUTO_NUMBER", "CREATED_DATE", "MODIFIED_DATE", "CREATED_BY", "MODIFIED_BY"];

    // ---- API ----

    public static SourceSheet FromApi(SmartsheetSheet sheet)
    {
        var columns = sheet.Columns.OrderBy(c => c.Index).Select(c => new SourceColumn(
            c.Id.ToString(CultureInfo.InvariantCulture), c.Title, c.Type, c.Primary, c.Options ?? [], c.Tags ?? [], c.SystemColumnType, c.Symbol)).ToList();
        var byId = sheet.Columns.ToDictionary(c => c.Id);
        var rows = new List<SourceRow>();
        var number = 0;
        foreach (var row in sheet.Rows.OrderBy(r => r.RowNumber))
        {
            number++;
            var cells = new Dictionary<string, SourceCell>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in row.Cells)
            {
                if (!byId.TryGetValue(cell.ColumnId, out var column))
                {
                    continue;
                }

                cells[column.Title] = ApiCell(column, cell);
            }

            rows.Add(new SourceRow(row.Id.ToString(CultureInfo.InvariantCulture), row.ParentId?.ToString(CultureInfo.InvariantCulture),
                row.RowNumber > 0 ? row.RowNumber : number, cells));
        }

        var scheduling = sheet.DependenciesEnabled || sheet.GanttEnabled
            || sheet.Columns.Any(c => c.Tags?.Contains("GANTT_START_DATE") == true);
        return new SourceSheet(sheet.Id.ToString(CultureInfo.InvariantCulture), sheet.Name, scheduling, columns, rows, FromApi: true);
    }

    private static SourceCell ApiCell(SmartsheetColumn column, SmartsheetCell cell)
    {
        var value = cell.Value is { } v ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

        IReadOnlyList<SourcePredecessor>? predecessors = null;
        decimal? duration = null;
        string? email = null;
        if (cell.ObjectValue is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("objectType", out var typeElement))
        {
            switch (typeElement.GetString())
            {
                case "PREDECESSOR_LIST" when obj.TryGetProperty("predecessors", out var list) && list.ValueKind == JsonValueKind.Array:
                    predecessors = list.EnumerateArray().Select(p => new SourcePredecessor(
                        p.TryGetProperty("rowId", out var rid) ? rid.GetRawText().Trim('"') : null,
                        p.TryGetProperty("rowNumber", out var rn) && rn.TryGetInt32(out var n) ? n : null,
                        p.TryGetProperty("type", out var t) ? t.GetString() ?? "FS" : "FS",
                        p.TryGetProperty("lag", out var lag) ? DurationDays(lag) : 0)).ToList();
                    break;
                case "DURATION":
                    duration = DurationDaysDecimal(obj);
                    break;
                case "CONTACT":
                    email = obj.TryGetProperty("email", out var e) ? e.GetString() : null;
                    break;
                case "MULTI_CONTACT" when obj.TryGetProperty("values", out var contacts) && contacts.ValueKind == JsonValueKind.Array:
                    value = string.Join(", ", contacts.EnumerateArray().Select(c => c.TryGetProperty("email", out var ce) ? ce.GetString() : null).OfType<string>());
                    break;
                case "MULTI_PICKLIST" when obj.TryGetProperty("values", out var picks) && picks.ValueKind == JsonValueKind.Array:
                    value = string.Join(", ", picks.EnumerateArray().Select(p => p.GetString()).OfType<string>());
                    break;
            }
        }

        if (column.Type == "DURATION" && duration is null && value is not null)
        {
            duration = ParseDuration(value);
        }

        if (column.Type == "PREDECESSOR" && predecessors is null && value is not null)
        {
            predecessors = ParsePredecessors(value);
        }

        if (column.Type == "CONTACT_LIST" && email is null && value?.Contains('@') == true)
        {
            email = value;
        }

        var url = cell.Hyperlink is { ValueKind: JsonValueKind.Object } link && link.TryGetProperty("url", out var u) ? u.GetString() : null;
        return new SourceCell(value, cell.DisplayValue ?? value, predecessors, duration, email, url);
    }

    private static int DurationDays(JsonElement duration) => (int)Math.Round(DurationDaysDecimal(duration), MidpointRounding.AwayFromZero);

    private static decimal DurationDaysDecimal(JsonElement d)
    {
        decimal Get(string name) => d.TryGetProperty(name, out var x) && x.TryGetDecimal(out var n) ? n : 0m;
        var days = Get("weeks") * 5 + Get("days") + Get("hours") / 8m + Get("minutes") / 480m;
        return d.TryGetProperty("negative", out var neg) && neg.ValueKind == JsonValueKind.True ? -days : days;
    }

    // ---- Text forms (shared by the API fallback and .xlsx) ----

    /// <summary>"5d", "~2w", "4h", "e3d", "5" → working days.</summary>
    public static decimal? ParseDuration(string text)
    {
        var match = DurationPattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var amount = decimal.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "w" => amount * 5,
            "h" => amount / 8,
            "m" => amount / 480,
            _ => amount,
        };
    }

    /// <summary>Smartsheet's predecessor text: "2", "3FS +2d", "4SS-1d, 7FF", referring to row numbers.</summary>
    public static IReadOnlyList<SourcePredecessor> ParsePredecessors(string text)
    {
        var list = new List<SourcePredecessor>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PredecessorPattern().Match(part);
            if (!match.Success)
            {
                continue;
            }

            var lag = 0;
            if (match.Groups["lag"].Success)
            {
                var days = ParseDuration(match.Groups["lag"].Value.Replace(" ", "").TrimStart('+', '-')) ?? 0;
                lag = (int)Math.Round(days, MidpointRounding.AwayFromZero) * (match.Groups["lag"].Value.TrimStart().StartsWith('-') ? -1 : 1);
            }

            list.Add(new SourcePredecessor(null, int.Parse(match.Groups["row"].Value, CultureInfo.InvariantCulture),
                match.Groups["type"].Success ? match.Groups["type"].Value.ToUpperInvariant() : "FS", lag));
        }

        return list;
    }

    // ---- .xlsx ----

    /// <summary>
    /// A Smartsheet .xlsx export: first row headers, the primary column first, hierarchy from the outline level or
    /// indentation. Column types are inferred from the values (dates, numbers, checkboxes, emails).
    /// </summary>
    public static SourceSheet FromExcel(TabularFile file)
    {
        var name = Path.GetFileNameWithoutExtension(file.FileName);
        var headers = file.Headers;
        var columns = new List<SourceColumn>();
        for (var i = 0; i < headers.Count; i++)
        {
            var values = file.Rows.Select(r => i < r.Cells.Count ? r.Cells[i] : "").Where(v => v.Length > 0).ToList();
            var (type, options) = Infer(headers[i], values);
            columns.Add(new SourceColumn(headers[i], headers[i], i == 0 ? "TEXT_NUMBER" : type, i == 0, options, i == 0 ? [PrimaryTag] : [], null));
        }

        var rows = new List<SourceRow>();
        var parents = new List<string>(); // parents[level] = row id of the latest row at that level
        var number = 0;
        foreach (var row in file.Rows)
        {
            number++;
            var id = $"row{row.Number}";
            var level = Math.Min(row.Level, parents.Count);
            var parent = level > 0 ? parents[level - 1] : null;
            if (parents.Count > level)
            {
                parents.RemoveRange(level, parents.Count - level);
            }

            parents.Add(id);
            var cells = new Dictionary<string, SourceCell>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count && i < row.Cells.Count; i++)
            {
                var text = row.Cells[i];
                if (text.Length == 0)
                {
                    continue;
                }

                var column = columns[i];
                cells[headers[i]] = new SourceCell(text, text,
                    column.Type == "PREDECESSOR" ? ParsePredecessors(text) : null,
                    column.Type == "DURATION" ? ParseDuration(text) : null,
                    text.Contains('@') ? text : null);
            }

            rows.Add(new SourceRow(id, parent, number, cells));
        }

        var scheduling = columns.Any(c => c.Type == "PREDECESSOR" || c.Type == "DURATION")
            || (columns.Any(c => c.Title.Contains("start", StringComparison.OrdinalIgnoreCase) && c.Type == "DATE")
                && columns.Any(c => (c.Title.Contains("finish", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("end", StringComparison.OrdinalIgnoreCase)) && c.Type == "DATE"));
        return new SourceSheet(file.FileName, name, scheduling, columns, rows, FromApi: false);
    }

    private static (string Type, IReadOnlyList<string> Options) Infer(string header, IReadOnlyList<string> values)
    {
        var normal = FieldMatcher.Normalize(header);
        if (normal is "predecessors" or "predecessor")
        {
            return ("PREDECESSOR", []);
        }

        if (normal is "duration" || (values.Count > 0 && values.All(v => DurationPattern().IsMatch(v) && v.Any(char.IsLetter))))
        {
            return ("DURATION", []);
        }

        if (values.Count == 0)
        {
            return ("TEXT_NUMBER", []);
        }

        if (values.All(v => v is "true" or "false"))
        {
            return ("CHECKBOX", []);
        }

        if (values.All(v => MappedRow.ParseDate(v) is not null && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
        {
            return ("DATE", []);
        }

        if (values.All(v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
        {
            return ("TEXT_NUMBER", []);
        }

        if (values.All(v => EmailPattern().IsMatch(v)))
        {
            return ("CONTACT_LIST", []);
        }

        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count <= 12 && values.Count >= distinct.Count * 2 && distinct.All(v => v.Length <= 40))
        {
            return ("PICKLIST", distinct);
        }

        return ("TEXT_NUMBER", []);
    }

    [GeneratedRegex(@"^[~e]?\s*(?<n>\d+(\.\d+)?)\s*(?<u>[dwhm])?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^(?<row>\d+)\s*(?<type>FS|SS|FF|SF)?\s*(?<lag>[+-]\s*[~e]?\d+(\.\d+)?\s*[dwhm]?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PredecessorPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
