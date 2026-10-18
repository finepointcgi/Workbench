using Workbench.Contracts.Reports;

namespace Workbench.Application.Reports;

/// <summary>
/// Applies a report-writer definition to rows already loaded (and permission-limited) by a <see cref="ReportSource"/>:
/// filters, sort, up to three grouping levels with subtotal rows, and a total row. Pure: no database, no user.
/// </summary>
public static class ReportEngine
{
    public const int MaxGroupLevels = 3;

    /// <param name="visible">The source's fields the user may see; definitions naming others have those parts dropped.</param>
    public static ReportResultDto Run(
        ReportSource source, IReadOnlyList<SourceField> visible, SourceRows rows, ReportDefinition definition, string? subtitle, DateTime generatedAtUtc)
    {
        var builder = new ReportBuilder(string.IsNullOrWhiteSpace(definition.Title) ? source.Name : definition.Title.Trim(), subtitle) { Truncated = rows.Truncated };
        var allIndex = source.Fields.Select((f, i) => (f.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        var visibleKeys = visible.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var dropped = 0;

        bool Usable(string field)
        {
            if (!allIndex.ContainsKey(field))
            {
                throw new ArgumentException($"{source.Name} has no field '{field}'.");
            }

            if (visibleKeys.Contains(field))
            {
                return true;
            }

            dropped++;
            return false;
        }

        var groups = (definition.GroupBy ?? []).Where(Usable).Distinct(StringComparer.Ordinal).ToList();
        if (groups.Count > MaxGroupLevels)
        {
            throw new ArgumentException($"Group by at most {MaxGroupLevels} fields.");
        }

        var columns = definition.Columns.Where(c => Usable(c.Field)).ToList();
        // Grouped fields lead the columns so every subtotal row says which group it totals.
        foreach (var g in Enumerable.Reverse(groups))
        {
            columns.RemoveAll(c => c.Field == g && c.Aggregate == ReportAggregate.None);
            columns.Insert(0, new ReportColumnSpec(g));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException(dropped > 0 ? "None of the report's columns are visible to you." : "Choose at least one column.");
        }

        var filters = (definition.Filters ?? []).Where(f => Usable(f.Field)).ToList();
        var sorts = (definition.Sort ?? []).Where(s => Usable(s.Field)).ToList();
        if (dropped > 0)
        {
            builder.Note("Parts of this report that use fields you don't have access to were left out.");
        }

        var fields = source.Fields;
        var hasAggregates = columns.Any(c => c.Aggregate != ReportAggregate.None);
        if (groups.Count > 0 && !hasAggregates)
        {
            // Sensible default for grouped reports: total the money and hours columns.
            columns = columns.Select(c => fields[allIndex[c.Field]].Type is ReportColumnType.Money or ReportColumnType.Hours && !groups.Contains(c.Field)
                ? c with { Aggregate = ReportAggregate.Sum }
                : c).ToList();
            hasAggregates = columns.Any(c => c.Aggregate != ReportAggregate.None);
        }

        foreach (var c in columns)
        {
            var field = fields[allIndex[c.Field]];
            var type = c.Aggregate == ReportAggregate.Count ? ReportColumnType.Number : field.Type;
            var label = string.IsNullOrWhiteSpace(c.Label) ? field.Label : c.Label.Trim();
            builder.Column(field.Key, c.Aggregate is ReportAggregate.None or ReportAggregate.Sum ? label : $"{label} ({ReportValues.Words(c.Aggregate.ToString()).ToLowerInvariant()})", type);
        }

        IEnumerable<object?[]> data = rows.Rows;
        foreach (var f in filters)
        {
            var index = allIndex[f.Field];
            var predicate = Predicate(fields[index], f);
            data = data.Where(r => predicate(r[index]));
        }

        var list = data.ToList();
        IOrderedEnumerable<object?[]>? ordered = null;
        foreach (var (field, descending) in groups.Select(g => (g, sorts.FirstOrDefault(s => s.Field == g)?.Descending ?? false))
                     .Concat(sorts.Where(s => !groups.Contains(s.Field)).Select(s => (s.Field, s.Descending))))
        {
            var index = allIndex[field];
            ordered = ordered is null
                ? descending ? list.OrderByDescending(r => r[index], ValueComparer.Instance) : list.OrderBy(r => r[index], ValueComparer.Instance)
                : descending ? ordered.ThenByDescending(r => r[index], ValueComparer.Instance) : ordered.ThenBy(r => r[index], ValueComparer.Instance);
        }

        var sorted = ordered?.ToList() ?? list;
        var columnIndexes = columns.Select(c => allIndex[c.Field]).ToArray();
        var groupIndexes = groups.Select(g => allIndex[g]).ToArray();

        void Emit(List<object?[]> slice, int level)
        {
            if (level == groupIndexes.Length)
            {
                if (definition.ShowDetails)
                {
                    foreach (var r in slice)
                    {
                        builder.Add(ReportRowKind.Detail, level, null, null, columnIndexes.Select((fi, ci) => columns[ci].Aggregate == ReportAggregate.Count ? (object?)1 : r[fi]).ToArray());
                    }
                }

                return;
            }

            var gi = groupIndexes[level];
            foreach (var group in Partition(slice, gi))
            {
                Emit(group, level + 1);
                builder.Add(ReportRowKind.Subtotal, level, null, null, columnIndexes.Select((fi, ci) =>
                {
                    var g = Array.IndexOf(groupIndexes, fi);
                    if (g >= 0 && columns[ci].Aggregate == ReportAggregate.None)
                    {
                        return g <= level ? group[0][fi] ?? "(blank)" : null;
                    }

                    return Aggregate(columns[ci].Aggregate, fields[fi].Type, group.Select(r => r[fi]));
                }).ToArray());
            }
        }

        Emit(sorted, 0);
        if (hasAggregates && sorted.Count > 0)
        {
            var labelled = false;
            builder.Add(ReportRowKind.Total, 0, null, null, columnIndexes.Select((fi, ci) =>
            {
                if (columns[ci].Aggregate != ReportAggregate.None)
                {
                    return Aggregate(columns[ci].Aggregate, fields[fi].Type, sorted.Select(r => r[fi]));
                }

                if (!labelled && fields[fi].Type == ReportColumnType.Text)
                {
                    labelled = true;
                    return (object?)"Total";
                }

                return null;
            }).ToArray());
        }

        builder.Note($"{sorted.Count:N0} row{(sorted.Count == 1 ? "" : "s")}.");
        return builder.Build(generatedAtUtc);
    }

    private static IEnumerable<List<object?[]>> Partition(List<object?[]> sorted, int index)
    {
        var current = new List<object?[]>();
        foreach (var r in sorted)
        {
            if (current.Count > 0 && ValueComparer.Instance.Compare(current[0][index], r[index]) != 0)
            {
                yield return current;
                current = [];
            }

            current.Add(r);
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static object? Aggregate(ReportAggregate aggregate, ReportColumnType type, IEnumerable<object?> values)
    {
        if (aggregate == ReportAggregate.Count)
        {
            return values.Count();
        }

        if (aggregate == ReportAggregate.None)
        {
            return null;
        }

        if (!ReportValues.IsNumeric(type))
        {
            var present = values.Where(v => v is not null).ToList();
            return aggregate switch
            {
                ReportAggregate.Min => present.Min(ValueComparer.Instance),
                ReportAggregate.Max => present.Max(ValueComparer.Instance),
                _ => null,
            };
        }

        var numbers = values.Select(ToDecimal).OfType<decimal>().ToList();
        return aggregate switch
        {
            ReportAggregate.Sum => numbers.Sum(),
            ReportAggregate.Average => numbers.Count == 0 ? null : decimal.Round(numbers.Average(), 4, MidpointRounding.AwayFromZero),
            ReportAggregate.Min => numbers.Count == 0 ? null : numbers.Min(),
            ReportAggregate.Max => numbers.Count == 0 ? null : numbers.Max(),
            _ => null,
        };
    }

    private static decimal? ToDecimal(object? value) => value switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        double f => (decimal)f,
        _ => null,
    };

    /// <summary>A filter on a field, comparing as the field's type: numbers numerically, dates by date, text ignoring case.</summary>
    internal static Func<object?, bool> Predicate(SourceField field, ReportFilterSpec filter)
    {
        var op = filter.Operator;
        if (op == ReportFilterOperator.IsBlank)
        {
            return v => v is null || (v is string s && s.Length == 0);
        }

        if (op == ReportFilterOperator.IsNotBlank)
        {
            return v => !(v is null || (v is string s && s.Length == 0));
        }

        var raw = filter.Value?.Trim() ?? throw new ArgumentException($"The filter on {field.Label} needs a value.");
        if (op == ReportFilterOperator.OneOf)
        {
            var options = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(o => Parse(field, o)).ToList();
            return v => options.Any(o => ValueComparer.Instance.Compare(Normalize(v), o) == 0);
        }

        if (op is ReportFilterOperator.Contains or ReportFilterOperator.StartsWith)
        {
            return v =>
            {
                var text = ReportValues.Format(v);
                return text is not null && (op == ReportFilterOperator.Contains
                    ? text.Contains(raw, StringComparison.OrdinalIgnoreCase)
                    : text.StartsWith(raw, StringComparison.OrdinalIgnoreCase));
            };
        }

        var target = Parse(field, raw);
        return op switch
        {
            ReportFilterOperator.Equals => v => ValueComparer.Instance.Compare(Normalize(v), target) == 0,
            ReportFilterOperator.NotEquals => v => ValueComparer.Instance.Compare(Normalize(v), target) != 0,
            ReportFilterOperator.GreaterThan => v => v is not null && ValueComparer.Instance.Compare(Normalize(v), target) > 0,
            ReportFilterOperator.GreaterOrEqual => v => v is not null && ValueComparer.Instance.Compare(Normalize(v), target) >= 0,
            ReportFilterOperator.LessThan => v => v is not null && ValueComparer.Instance.Compare(Normalize(v), target) < 0,
            ReportFilterOperator.LessOrEqual => v => v is not null && ValueComparer.Instance.Compare(Normalize(v), target) <= 0,
            _ => throw new ArgumentException($"Unknown filter operator {op}."),
        };
    }

    /// <summary>Typed filter value; text, enums and anything unparseable compare as text.</summary>
    private static object Parse(SourceField field, string raw)
    {
        if (ReportValues.IsNumeric(field.Type))
        {
            return ReportValues.Number(raw) ?? throw new ArgumentException($"'{raw}' isn't a number ({field.Label}).");
        }

        return field.Type switch
        {
            ReportColumnType.Date => ReportValues.Date(raw) ?? throw new ArgumentException($"'{raw}' isn't a date (yyyy-MM-dd) ({field.Label})."),
            ReportColumnType.Boolean => raw.ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" => false,
                _ => throw new ArgumentException($"'{raw}' isn't yes or no ({field.Label})."),
            },
            _ => raw,
        };
    }

    /// <summary>Row values compared with filter values: enums by their display text, times by date, numbers as decimals.</summary>
    private static object? Normalize(object? value) => value switch
    {
        Enum e => ReportValues.Format(e),
        DateTime t => DateOnly.FromDateTime(t),
        int i => (decimal)i,
        long l => (decimal)l,
        double d => (decimal)d,
        _ => value,
    };

    /// <summary>Orders mixed report values: blanks first, then numbers, dates, booleans and text (case-insensitive).</summary>
    internal sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            x = Normalize(x);
            y = Normalize(y);
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            return (x, y) switch
            {
                (decimal a, decimal b) => a.CompareTo(b),
                (DateOnly a, DateOnly b) => a.CompareTo(b),
                (bool a, bool b) => a.CompareTo(b),
                (string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(ReportValues.Format(x), ReportValues.Format(y), StringComparison.OrdinalIgnoreCase),
            };
        }
    }
}
