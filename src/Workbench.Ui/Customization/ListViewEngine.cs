using System.Globalization;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Reports;

namespace Workbench.Ui.Customization;

/// <summary>
/// What a list view does to rows, without the UI: the quick search, filters, grouping and sorts, and the export of exactly
/// those rows and columns. The screen and the file both come from <see cref="Apply{T}"/> and <see cref="Visible{T}"/>, so
/// an export always matches the view.
/// </summary>
public static class ListViewEngine
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The view's columns that exist (and the viewer may see), in the view's order. An empty result falls back to the standard columns.</summary>
    public static IReadOnlyList<ListColumn<T>> Visible<T>(IReadOnlyList<ListColumn<T>> available, ListViewDefinition view)
    {
        var byKey = available.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var shown = view.Columns.Where(byKey.ContainsKey).Distinct(StringComparer.Ordinal).Select(k => byKey[k]).ToList();
        return shown.Count > 0 ? shown : available.Where(c => c.Standard).ToList();
    }

    /// <summary>The view without what the viewer can't see (columns, sorts, filters and grouping on unknown columns are dropped).</summary>
    public static ListViewDefinition Restrict<T>(IReadOnlyList<ListColumn<T>> available, ListViewDefinition view)
    {
        var keys = available.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        return new ListViewDefinition(
            Visible(available, view).Select(c => c.Key).ToList(),
            (view.Sort ?? []).Where(s => keys.Contains(s.Field)).ToList(),
            (view.Filters ?? []).Where(f => keys.Contains(f.Field)).ToList(),
            view.GroupBy is { } g && keys.Contains(g) ? g : null,
            view.Search);
    }

    /// <summary>Rows that pass the search and every filter, grouped (by the group column) then sorted.</summary>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> rows, IReadOnlyList<ListColumn<T>> available, ListViewDefinition view)
    {
        var view2 = Restrict(available, view);
        var byKey = available.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var visible = Visible(available, view2);
        IEnumerable<T> result = rows;

        if (!string.IsNullOrWhiteSpace(view2.Search))
        {
            var term = view2.Search.Trim();
            result = result.Where(r => visible.Any(c => Text(c, r)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
        }

        foreach (var filter in view2.Filters ?? [])
        {
            var column = byKey[filter.Field];
            result = result.Where(r => Matches(column, r, filter));
        }

        var sorts = (view2.Sort ?? []).Select(s => (Column: byKey[s.Field], s.Descending)).ToList();
        if (view2.GroupBy is { } group)
        {
            sorts.Insert(0, (byKey[group], false));
        }

        if (sorts.Count == 0)
        {
            return result.ToList();
        }

        var list = result.ToList();
        var comparer = Comparer<T>.Create((a, b) =>
        {
            foreach (var (column, descending) in sorts)
            {
                var c = Compare(column, a, b);
                if (c != 0)
                {
                    return descending ? -c : c;
                }
            }

            return 0;
        });

        // A stable sort, so rows keep the page's order within equal keys.
        return list.Select((row, index) => (row, index))
            .OrderBy(x => x.row, comparer).ThenBy(x => x.index)
            .Select(x => x.row)
            .ToList();
    }

    /// <summary>The group a row falls in (blank values as "(none)").</summary>
    public static string GroupKey<T>(ListColumn<T> column, T row) => Text(column, row) is { Length: > 0 } text ? text : "(none)";

    public static bool Matches<T>(ListColumn<T> column, T row, ListFilterCondition filter)
    {
        var text = Text(column, row);
        var empty = string.IsNullOrWhiteSpace(text);
        switch (filter.Operator)
        {
            case ListFilterOperator.IsEmpty:
                return empty;
            case ListFilterOperator.IsNotEmpty:
                return !empty;
            case ListFilterOperator.Contains:
                return text?.Contains(filter.Value ?? "", StringComparison.OrdinalIgnoreCase) == true;
            case ListFilterOperator.NotContains:
                return text?.Contains(filter.Value ?? "", StringComparison.OrdinalIgnoreCase) != true;
            case ListFilterOperator.StartsWith:
                return text?.StartsWith(filter.Value ?? "", StringComparison.OrdinalIgnoreCase) == true;
        }

        var c = CompareToValue(column, row, filter.Value);
        return filter.Operator switch
        {
            ListFilterOperator.Equals => c == 0,
            ListFilterOperator.NotEquals => c != 0,
            ListFilterOperator.GreaterThan => !empty && c > 0,
            ListFilterOperator.GreaterOrEqual => !empty && c >= 0,
            ListFilterOperator.LessThan => !empty && c < 0,
            ListFilterOperator.LessOrEqual => !empty && c <= 0,
            _ => true,
        };
    }

    /// <summary>The operators that make sense for a column.</summary>
    public static IReadOnlyList<ListFilterOperator> OperatorsFor(ListColumnKind kind) => kind switch
    {
        ListColumnKind.Number or ListColumnKind.Money or ListColumnKind.Percent or ListColumnKind.Date =>
            [ListFilterOperator.Equals, ListFilterOperator.NotEquals, ListFilterOperator.GreaterThan, ListFilterOperator.GreaterOrEqual,
             ListFilterOperator.LessThan, ListFilterOperator.LessOrEqual, ListFilterOperator.IsEmpty, ListFilterOperator.IsNotEmpty],
        ListColumnKind.Bool => [ListFilterOperator.Equals, ListFilterOperator.NotEquals],
        _ => [ListFilterOperator.Contains, ListFilterOperator.NotContains, ListFilterOperator.Equals, ListFilterOperator.NotEquals,
              ListFilterOperator.StartsWith, ListFilterOperator.IsEmpty, ListFilterOperator.IsNotEmpty],
    };

    public static string Label(ListFilterOperator op) => op switch
    {
        ListFilterOperator.Equals => "is",
        ListFilterOperator.NotEquals => "is not",
        ListFilterOperator.Contains => "contains",
        ListFilterOperator.NotContains => "doesn't contain",
        ListFilterOperator.StartsWith => "starts with",
        ListFilterOperator.GreaterThan => "is more than",
        ListFilterOperator.GreaterOrEqual => "is at least",
        ListFilterOperator.LessThan => "is less than",
        ListFilterOperator.LessOrEqual => "is at most",
        ListFilterOperator.IsEmpty => "is empty",
        ListFilterOperator.IsNotEmpty => "is not empty",
        _ => op.ToString(),
    };

    public static bool NeedsValue(ListFilterOperator op) => op is not (ListFilterOperator.IsEmpty or ListFilterOperator.IsNotEmpty);

    /// <summary>The value as people read it (display text if the column has one).</summary>
    public static string? Text<T>(ListColumn<T> column, T row)
    {
        if (column.Display is { } display)
        {
            return display(row);
        }

        var value = column.Value(row);
        return value switch
        {
            null => null,
            string s => s,
            bool b => b ? "Yes" : "No",
            DateOnly d => d.ToString("yyyy-MM-dd", Invariant),
            DateTime t => t.ToString("yyyy-MM-dd", Invariant),
            _ when column.Kind == ListColumnKind.Money && ToDecimal(value) is { } m => m.ToString("C0", Us),
            _ when column.Kind == ListColumnKind.Percent && ToDecimal(value) is { } p => p.ToString("P0", Us),
            _ when ToDecimal(value) is { } n => n == Math.Round(n) ? n.ToString("N0", Us) : n.ToString("N2", Us),
            Enum e => Humanize(e.ToString()),
            _ => Convert.ToString(value, Invariant),
        };
    }

    /// <summary>The value for a file: invariant strings (numbers <c>1234.5</c>, dates <c>yyyy-MM-dd</c>), like report results.</summary>
    public static string? ExportValue<T>(ListColumn<T> column, T row)
    {
        var value = column.Value(row);
        return value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            DateOnly d => d.ToString("yyyy-MM-dd", Invariant),
            DateTime t => t.ToString("yyyy-MM-dd", Invariant),
            _ when column.Kind is ListColumnKind.Number or ListColumnKind.Money or ListColumnKind.Percent && ToDecimal(value) is { } n => n.ToString(Invariant),
            _ => Text(column, row),
        };
    }

    /// <summary>The rows and columns of an export: what the view shows, in its order.</summary>
    public static ListExportRequest Export<T>(string listKey, string title, IReadOnlyList<T> rows, IReadOnlyList<ListColumn<T>> columns, ReportFileFormat format) =>
        new(listKey, title,
            columns.Select(c => new ReportColumnDto(c.Key, c.Title, ColumnType(c.Kind))).ToList(),
            rows.Select(r => (IReadOnlyList<string?>)columns.Select(c => ExportValue(c, r)).ToList()).ToList(),
            format);

    public static ReportColumnType ColumnType(ListColumnKind kind) => kind switch
    {
        ListColumnKind.Number => ReportColumnType.Number,
        ListColumnKind.Money => ReportColumnType.Money,
        ListColumnKind.Percent => ReportColumnType.Percent,
        ListColumnKind.Date => ReportColumnType.Date,
        ListColumnKind.Bool => ReportColumnType.Boolean,
        _ => ReportColumnType.Text,
    };

    private static int Compare<T>(ListColumn<T> column, T a, T b)
    {
        var x = column.Value(a);
        var y = column.Value(b);
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : 1) : -1; // Blanks last, on every database and screen alike.
        }

        if (ToDecimal(x) is { } dx && ToDecimal(y) is { } dy)
        {
            return dx.CompareTo(dy);
        }

        if (x is IComparable cx && x.GetType() == y.GetType() && x is not string)
        {
            return cx.CompareTo(y);
        }

        return string.Compare(Text(column, a), Text(column, b), StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareToValue<T>(ListColumn<T> column, T row, string? value)
    {
        var raw = column.Value(row);
        if (column.Kind is ListColumnKind.Number or ListColumnKind.Money or ListColumnKind.Percent
            && ToDecimal(raw) is { } n && decimal.TryParse(value, NumberStyles.Number, Invariant, out var v))
        {
            return n.CompareTo(v);
        }

        if (column.Kind == ListColumnKind.Date && DateOnly.TryParse(value, Invariant, out var date))
        {
            var rowDate = raw switch { DateOnly d => d, DateTime t => DateOnly.FromDateTime(t), _ => (DateOnly?)null };
            return rowDate is { } rd ? rd.CompareTo(date) : -1;
        }

        if (column.Kind == ListColumnKind.Bool && bool.TryParse(value, out var flag))
        {
            return raw is bool b && b == flag ? 0 : 1;
        }

        return string.Compare(Text(column, row) ?? "", value ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ToDecimal(object? value) => value switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        double f => (decimal)f,
        float f => (decimal)f,
        _ => null,
    };

    private static string Humanize(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}
