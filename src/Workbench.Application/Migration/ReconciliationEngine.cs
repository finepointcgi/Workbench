using System.Globalization;
using Workbench.Application.Imports;
using Workbench.Contracts.Migration;

namespace Workbench.Application.Migration;

/// <summary>
/// One Workbench figure to compare: its key parts (in the report's key order), a label and its amounts.
/// <see cref="OnlyIfInFile"/> marks roll-ups (summary accounts, parent WBS elements) compared only when the Costpoint
/// report lists them.
/// </summary>
public sealed record WorkbenchFigure(IReadOnlyList<string> Key, string? Label, IReadOnlyDictionary<string, decimal> Values, bool OnlyIfInFile = false);

public sealed record ReconComparison(IReadOnlyList<ReconRowDto> Rows, IReadOnlyList<ReconProblemDto> Problems, string PrimaryField, decimal CostpointTotal, decimal WorkbenchTotal)
{
    public int Count(ReconRowStatus status) => Rows.Count(r => r.Status == status);

    public bool AllMatched => Rows.All(r => r.Status == ReconRowStatus.Matched) && Problems.Count == 0;
}

/// <summary>
/// Compares a Costpoint report (read through a saved column mapping) with Workbench's figures: rows are matched on
/// their normalized keys, file rows with the same key are added together, and each mapped amount is checked against
/// its tolerance. Pure: no database, so the matching and tolerance rules are tested on sample files directly.
/// </summary>
public static class ReconciliationEngine
{
    public const string KeySeparator = " · ";

    /// <summary>The suggested mapping for a file: headers matched to fields by name and alias.</summary>
    public static ReconMappingDto Suggest(ReconReportDto report, TabularFile file, string name)
    {
        var fields = report.Fields.Select(f => new ImportField(f.Name, f.Label, f.Required, f.Description, [.. f.Aliases])).ToList();
        var matched = FieldMatcher.AutoMap(fields, file.Headers);
        var columns = report.Fields.Select(f => new ReconColumnMapDto(f.Name, matched.GetValueOrDefault(f.Name), Scale: SuggestScale(f, matched.GetValueOrDefault(f.Name), file))).ToList();

        // A trial balance with separate debit and credit balance columns: ending = debit − credit.
        if (report.Kind == ReconReportKind.TrialBalance)
        {
            var credit = file.Headers.FirstOrDefault(h => FieldMatcher.Normalize(h) is "creditbalance" or "endingcredit" or "crbalance" or "endcr" or "credit");
            var ending = columns.First(c => c.Field == "Ending");
            if (credit is not null && ending.Column is { } debit && FieldMatcher.Normalize(debit).Contains("debit", StringComparison.Ordinal))
            {
                columns[columns.IndexOf(ending)] = ending with { MinusColumn = credit };
            }
        }

        return new ReconMappingDto(null, report.Kind, name, columns,
            report.Fields.Where(f => f.Role == ReconFieldRole.Measure).Select(f => new ReconToleranceDto(f.Name, f.DefaultTolerance)).ToList(),
            new ReconOptionsDto(SkipKeyPrefixes: ReconciliationReports.DefaultSkipPrefixes, KeyRewrites: ReconciliationReports.DefaultRewrites(report.Kind), Range: report.DefaultRange));
    }

    /// <summary>Rates shown as percentages (32.15) get a scale of 0.01.</summary>
    private static decimal SuggestScale(ReconFieldDto field, string? column, TabularFile file)
    {
        if (field.Format != ReconValueFormat.Rate || column is null)
        {
            return 1m;
        }

        var index = file.Headers.ToList().FindIndex(h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase));
        var values = file.Rows.Take(20).Select(r => index >= 0 && index < r.Cells.Count ? r.Cells[index] : "")
            .Select(c => (Text: c, Value: ParseAmount(c))).Where(v => v.Value is not null).ToList();
        return values.Any(v => v.Text.Contains('%') || Math.Abs(v.Value!.Value) > 1.5m) ? 0.01m : 1m;
    }

    /// <summary>
    /// Checks a mapping against a report and a file: every key and required amount mapped to a column the file has,
    /// tolerances not negative. Throws <see cref="ArgumentException"/> with every problem.
    /// </summary>
    public static void Validate(ReconReportDto report, ReconMappingDto mapping, IReadOnlyList<string>? headers = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(mapping.Name))
        {
            problems.Add("Name the mapping.");
        }

        foreach (var field in report.Fields)
        {
            var map = mapping.Columns.FirstOrDefault(c => c.Field == field.Name);
            if (map?.Column is null)
            {
                if (field.Required)
                {
                    problems.Add($"Map a column to {field.Label}.");
                }

                continue;
            }

            foreach (var column in new[] { map.Column, map.MinusColumn }.OfType<string>())
            {
                if (headers is not null && !headers.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{field.Label}: the file has no column '{column}'.");
                }
            }

            if (map.Scale == 0)
            {
                problems.Add($"{field.Label}: the scale can't be 0.");
            }
        }

        if (!report.Fields.Any(f => f.Role == ReconFieldRole.Measure && mapping.Columns.Any(c => c.Field == f.Name && c.Column is not null)))
        {
            problems.Add("Map at least one amount to compare.");
        }

        problems.AddRange(mapping.Tolerances.Where(t => t.Absolute < 0 || t.Percent < 0).Select(t => $"The tolerance for {t.Field} can't be negative."));
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }
    }

    public static ReconComparison Compare(ReconReportDto report, ReconMappingDto mapping, TabularFile file, IReadOnlyList<WorkbenchFigure> workbench)
    {
        Validate(report, mapping, file.Headers);
        var options = mapping.Options;
        var keys = report.Fields.Where(f => f.Role == ReconFieldRole.Key).ToList();
        var label = report.Fields.FirstOrDefault(f => f.Role == ReconFieldRole.Label);
        var measures = report.Fields.Where(f => f.Role == ReconFieldRole.Measure && Map(mapping, f.Name)?.Column is not null).ToList();
        var rewrites = (options.KeyRewrites ?? new Dictionary<string, string>())
            .Where(kv => kv.Key.Trim().Length > 0)
            .GroupBy(kv => Plain(kv.Key)).ToDictionary(g => g.Key, g => g.First().Value.Trim());
        var skip = (options.SkipKeyPrefixes ?? []).Where(p => p.Trim().Length > 0).Select(p => p.Trim()).ToList();
        var problems = new List<ReconProblemDto>();

        // Costpoint side: read, normalize, add up by key.
        var costpoint = new Dictionary<string, Side>(StringComparer.Ordinal);
        foreach (var row in file.Rows)
        {
            var display = new List<string>();
            var normal = new List<string>();
            var blank = false;
            foreach (var key in keys)
            {
                var raw = Cell(file, row, Map(mapping, key.Name)?.Column);
                if (raw.Length == 0)
                {
                    blank = true;
                    break;
                }

                var (shown, norm) = NormalizeKey(raw, key.Name, options, rewrites);
                display.Add(shown);
                normal.Add(norm);
            }

            var firstCell = Cell(file, row, Map(mapping, keys[0].Name)?.Column);
            if (skip.Any(p => firstCell.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || row.Cells.All(c => c.Trim().Length == 0))
            {
                continue;
            }

            if (blank)
            {
                // A row with amounts but no key is a problem; a blank spacer or a section title isn't.
                if (measures.Any(m => ParseAmount(Cell(file, row, Map(mapping, m.Name)!.Column)) is { } v && v != 0))
                {
                    problems.Add(new ReconProblemDto(row.Number, $"No {string.Join(" / ", keys.Select(k => k.Label))} on a row with amounts."));
                }

                continue;
            }

            var values = new Dictionary<string, decimal>();
            var bad = false;
            foreach (var measure in measures)
            {
                var map = Map(mapping, measure.Name)!;
                var amount = 0m;
                foreach (var (column, sign) in new[] { (map.Column, 1m), (map.MinusColumn, -1m) })
                {
                    if (column is null)
                    {
                        continue;
                    }

                    var text = Cell(file, row, column);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (ParseAmount(text) is not { } parsed)
                    {
                        problems.Add(new ReconProblemDto(row.Number, $"{measure.Label}: '{text}' isn't a number."));
                        bad = true;
                        break;
                    }

                    amount += sign * parsed;
                }

                values[measure.Name] = (map.Negate ? -amount : amount) * map.Scale;
            }

            if (bad)
            {
                continue;
            }

            var id = string.Join("|", normal);
            if (!costpoint.TryGetValue(id, out var side))
            {
                costpoint[id] = side = new Side(display, label is null ? null : NullIfEmpty(Cell(file, row, Map(mapping, label.Name)?.Column)));
            }

            side.Rows.Add(row.Number);
            foreach (var (field, value) in values)
            {
                side.Values[field] = side.Values.GetValueOrDefault(field) + value;
            }
        }

        // Workbench side, normalized the same way.
        var wb = new Dictionary<string, Side>(StringComparer.Ordinal);
        foreach (var figure in workbench)
        {
            var display = new List<string>();
            var normal = new List<string>();
            for (var i = 0; i < keys.Count; i++)
            {
                var (shown, norm) = NormalizeKey(i < figure.Key.Count ? figure.Key[i] : "", keys[i].Name, options, new Dictionary<string, string>());
                display.Add(shown);
                normal.Add(norm);
            }

            var id = string.Join("|", normal);
            if (figure.OnlyIfInFile && !costpoint.ContainsKey(id))
            {
                continue;
            }

            if (!wb.TryGetValue(id, out var side))
            {
                wb[id] = side = new Side(display, figure.Label) { RollUp = figure.OnlyIfInFile };
            }
            else if (side.RollUp != figure.OnlyIfInFile)
            {
                // A detail figure and a roll-up with one key (a project at project level): keep the detail.
                if (figure.OnlyIfInFile)
                {
                    continue;
                }

                side.Values.Clear();
                side.RollUp = false;
            }

            foreach (var measure in measures)
            {
                side.Values[measure.Name] = side.Values.GetValueOrDefault(measure.Name) + figure.Values.GetValueOrDefault(measure.Name);
            }
        }

        var tolerances = measures.ToDictionary(m => m.Name, m => mapping.Tolerances.FirstOrDefault(t => t.Field == m.Name) ?? new ReconToleranceDto(m.Name, m.DefaultTolerance));
        var rows = new List<ReconRowDto>();
        foreach (var id in costpoint.Keys.Union(wb.Keys).Order(StringComparer.Ordinal))
        {
            costpoint.TryGetValue(id, out var cp);
            wb.TryGetValue(id, out var w);
            var values = measures.Select(m =>
            {
                var c = cp?.Values.GetValueOrDefault(m.Name) ?? 0m;
                var v = w?.Values.GetValueOrDefault(m.Name) ?? 0m;
                return new ReconValueDto(m.Name, c, v, Within(c, v, tolerances[m.Name]));
            }).ToList();
            var zero = values.All(v => Within(0m, v.Costpoint, tolerances[v.Field]) && Within(0m, v.Workbench, tolerances[v.Field]));
            ReconRowStatus status;
            if (cp is null)
            {
                if (zero)
                {
                    continue;
                }

                status = ReconRowStatus.OnlyInWorkbench;
            }
            else if (w is null)
            {
                status = zero ? ReconRowStatus.Matched : ReconRowStatus.OnlyInCostpoint;
            }
            else
            {
                status = values.All(v => v.WithinTolerance) ? ReconRowStatus.Matched : ReconRowStatus.Different;
            }

            var shown = (cp ?? w)!.Display;
            rows.Add(new ReconRowDto(string.Join(KeySeparator, shown), shown, cp?.Label ?? w?.Label, status, values, cp?.Rows ?? []));
        }

        var primary = measures.FirstOrDefault(m => m.Required)?.Name ?? measures.First().Name;
        return new ReconComparison(rows, problems, primary, rows.Sum(r => r.Values.First(v => v.Field == primary).Costpoint), rows.Sum(r => r.Values.First(v => v.Field == primary).Workbench));
    }

    /// <summary>Within the absolute tolerance, or within the percentage of the Costpoint amount.</summary>
    public static bool Within(decimal costpoint, decimal workbench, ReconToleranceDto tolerance)
    {
        var difference = Math.Abs(workbench - costpoint);
        return difference <= tolerance.Absolute || (tolerance.Percent > 0 && difference <= Math.Abs(costpoint) * tolerance.Percent / 100m);
    }

    /// <summary>Amounts as Costpoint exports them: $, thousands separators, (negative), trailing minus, CR/DR, %.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().Replace("$", "").Replace(",", "").Replace(" ", "");
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            negative = true;
            t = t[1..^1];
        }

        if (t.EndsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            negative = !negative;
            t = t[..^2];
        }
        else if (t.EndsWith("DR", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^2];
        }

        if (t.EndsWith('-'))
        {
            negative = !negative;
            t = t[..^1];
        }

        t = t.TrimEnd('%');
        if (t is "-" or "")
        {
            return 0m;
        }

        return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value)
            ? negative ? -value : value
            : null;
    }

    /// <summary>
    /// The shown and compared forms of a key part: trimmed; projects cut to their top level at project level; rewritten;
    /// leading zeros and punctuation dropped when asked; compared case-insensitively.
    /// </summary>
    public static (string Shown, string Normal) NormalizeKey(string raw, string field, ReconOptionsDto options, IReadOnlyDictionary<string, string> rewrites)
    {
        var value = raw.Trim();
        if (field == "Project" && options.ProjectLevel)
        {
            var dot = value.IndexOf('.');
            value = dot > 0 ? value[..dot] : value;
        }

        if (rewrites.TryGetValue(Plain(value), out var rewritten))
        {
            value = rewritten;
        }

        var normal = value;
        if (options.TrimLeadingZeros)
        {
            normal = normal.TrimStart('0');
            normal = normal.Length == 0 ? "0" : normal;
        }

        if (options.IgnorePunctuation || field == "Pool")
        {
            normal = new string(normal.Where(char.IsLetterOrDigit).ToArray());
        }

        return (value, normal.ToUpperInvariant());
    }

    private static string Plain(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static ReconColumnMapDto? Map(ReconMappingDto mapping, string field) => mapping.Columns.FirstOrDefault(c => c.Field == field);

    private static string Cell(TabularFile file, TabularRow row, string? column)
    {
        if (column is null)
        {
            return "";
        }

        for (var i = 0; i < file.Headers.Count; i++)
        {
            if (string.Equals(file.Headers[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i < row.Cells.Count ? row.Cells[i].Trim() : "";
            }
        }

        return "";
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed class Side(List<string> display, string? label)
    {
        public List<string> Display { get; } = display;
        public string? Label { get; } = label;
        public List<int> Rows { get; } = [];
        public Dictionary<string, decimal> Values { get; } = [];
        public bool RollUp { get; set; }
    }
}
