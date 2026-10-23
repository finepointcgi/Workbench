using System.Globalization;
using Workbench.Contracts.Customization;

namespace Workbench.Contracts.Sheets;

/// <summary>
/// Applies a <see cref="SheetViewDefinition"/> to a sheet's items: the same rules for the grid on screen and for exports.
/// Values are compared by the field's type (numbers as numbers, dates as dates, status labels in the column's option
/// order); blanks sort last in either direction.
/// </summary>
public static class SheetViewEngine
{
    /// <summary>The stored (invariant) value of a field: a built-in item field (<see cref="SheetFields"/>) or a column id.</summary>
    public static string? Value(ItemDto item, string field) => field switch
    {
        SheetFields.Title => item.Title,
        SheetFields.Start => item.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        SheetFields.Finish => item.Finish?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        SheetFields.Wbs => item.CostpointWbsId,
        _ => item.Values.TryGetValue(field, out var value) ? value : null,
    };

    public static ColumnKind KindOf(string field, IReadOnlyList<ColumnDto> columns) => field switch
    {
        SheetFields.Start or SheetFields.Finish => ColumnKind.Date,
        SheetFields.Title or SheetFields.Wbs => ColumnKind.Text,
        _ => Find(field, columns)?.Type ?? ColumnKind.Text,
    };

    /// <summary>
    /// The ids of rows a view shows: rows matching every filter, plus their ancestors (so a match keeps its context).
    /// Null when the view has no filters (every row).
    /// </summary>
    public static IReadOnlySet<Guid>? Visible(IReadOnlyList<ItemDto> items, IReadOnlyList<ColumnDto> columns, SheetViewDefinition? view)
    {
        if (view?.Filters is not { Count: > 0 } filters)
        {
            return null;
        }

        var byId = items.ToDictionary(i => i.Id);
        var visible = new HashSet<Guid>();
        foreach (var item in items.Where(i => filters.All(f => Matches(i, f, columns))))
        {
            for (var current = item; current is not null && visible.Add(current.Id);)
            {
                current = current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) ? parent : null;
            }
        }

        return visible;
    }

    /// <summary>
    /// The rows in display order with their depth: the hierarchy, each level sorted by the view (else by the sheet's
    /// order), and only rows <see cref="Visible"/> keeps.
    /// </summary>
    public static IReadOnlyList<(ItemDto Item, int Depth)> Apply(IReadOnlyList<ItemDto> items, IReadOnlyList<ColumnDto> columns, SheetViewDefinition? view)
    {
        var visible = Visible(items, columns, view);
        var comparer = Comparer(view?.Sort, columns);
        var children = items.ToLookup(i => i.ParentId);
        var known = items.Select(i => i.Id).ToHashSet();
        var rows = new List<(ItemDto, int)>(items.Count);
        // Orphans (a parent the caller can't see) show at the top level rather than disappearing.
        Add(items.Where(i => i.ParentId is null || !known.Contains(i.ParentId.Value)), 0);
        return rows;

        void Add(IEnumerable<ItemDto> siblings, int depth)
        {
            foreach (var item in siblings.Where(i => visible is null || visible.Contains(i.Id)).Order(comparer))
            {
                rows.Add((item, depth));
                Add(children[item.Id], depth + 1);
            }
        }
    }

    /// <summary>Orders siblings by the sorts, then by the sheet's own order.</summary>
    public static IComparer<ItemDto> Comparer(IReadOnlyList<ListSort>? sorts, IReadOnlyList<ColumnDto> columns)
    {
        var keys = (sorts ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Field)).Select(s => (s.Field, s.Descending, Kind: KindOf(s.Field, columns), Column: Find(s.Field, columns))).ToList();
        return Comparer<ItemDto>.Create((a, b) =>
        {
            foreach (var (field, descending, kind, column) in keys)
            {
                var result = CompareValues(Value(a, field), Value(b, field), kind, column, descending);
                if (result != 0)
                {
                    return result;
                }
            }

            return a.Order.CompareTo(b.Order);
        });
    }

    public static bool Matches(ItemDto item, ListFilterCondition filter, IReadOnlyList<ColumnDto> columns)
    {
        var raw = Value(item, filter.Field);
        var kind = KindOf(filter.Field, columns);
        var empty = string.IsNullOrWhiteSpace(raw) || (kind == ColumnKind.Checkbox && raw != "true");
        switch (filter.Operator)
        {
            case ListFilterOperator.IsEmpty:
                return empty;
            case ListFilterOperator.IsNotEmpty:
                return !empty;
            case ListFilterOperator.Equals or ListFilterOperator.NotEquals:
                var any = Values(filter.Value).Any(v => kind == ColumnKind.Checkbox ? (v == "true") == !empty : !empty && CompareValues(raw, v, kind, null, false) == 0);
                return filter.Operator == ListFilterOperator.Equals ? any : !any;
        }

        if (empty || filter.Value is not { } value)
        {
            return false;
        }

        switch (filter.Operator)
        {
            case ListFilterOperator.Contains:
                return raw!.Contains(value, StringComparison.OrdinalIgnoreCase);
            case ListFilterOperator.NotContains:
                return !raw!.Contains(value, StringComparison.OrdinalIgnoreCase);
            case ListFilterOperator.StartsWith:
                return raw!.StartsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        var compared = CompareValues(raw, value, kind, null, false);
        return filter.Operator switch
        {
            ListFilterOperator.GreaterThan => compared > 0,
            ListFilterOperator.GreaterOrEqual => compared >= 0,
            ListFilterOperator.LessThan => compared < 0,
            ListFilterOperator.LessOrEqual => compared <= 0,
            _ => false,
        };
    }

    /// <summary>Equals and NotEquals values: one per line.</summary>
    public static IReadOnlyList<string> Values(string? value) =>
        (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ColumnDto? Find(string field, IReadOnlyList<ColumnDto> columns) =>
        columns.FirstOrDefault(c => string.Equals(c.Id.ToString(), field, StringComparison.OrdinalIgnoreCase));

    /// <summary>Compares two stored values of a field; blanks come last whatever the direction.</summary>
    private static int CompareValues(string? a, string? b, ColumnKind kind, ColumnDto? column, bool descending)
    {
        var aEmpty = string.IsNullOrWhiteSpace(a);
        var bEmpty = string.IsNullOrWhiteSpace(b);
        if (aEmpty || bEmpty)
        {
            return aEmpty == bEmpty ? 0 : aEmpty ? 1 : -1;
        }

        var result = kind switch
        {
            ColumnKind.Number or ColumnKind.Currency when Number(a) is { } x && Number(b) is { } y => x.CompareTo(y),
            ColumnKind.Date when Date(a) is { } x && Date(b) is { } y => x.CompareTo(y),
            ColumnKind.Checkbox => (a == "true").CompareTo(b == "true"),
            ColumnKind.Dropdown when column is not null && OptionIndex(column, a!) is var x and >= 0 && OptionIndex(column, b!) is var y and >= 0 => x.CompareTo(y),
            _ => string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace),
        };
        return descending ? -result : result;
    }

    private static int OptionIndex(ColumnDto column, string value)
    {
        for (var i = 0; i < column.Options.Count; i++)
        {
            if (string.Equals(column.Options[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static decimal? Number(string? s) =>
        decimal.TryParse(s?.Replace(",", string.Empty).Replace("$", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s, ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>How a filter reads in plain words ("Status is Done"), on screen and in exports.</summary>
public static class SheetFilterText
{
    public static string Operator(ListFilterOperator op) => op switch
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
        ListFilterOperator.IsEmpty => "is blank",
        ListFilterOperator.IsNotEmpty => "isn't blank",
        _ => op.ToString(),
    };

    /// <summary>The operators that make sense for a field type, the most useful first.</summary>
    public static IReadOnlyList<ListFilterOperator> OperatorsFor(ColumnKind kind) => kind switch
    {
        ColumnKind.Number or ColumnKind.Currency or ColumnKind.Date =>
            [ListFilterOperator.Equals, ListFilterOperator.NotEquals, ListFilterOperator.GreaterThan, ListFilterOperator.GreaterOrEqual,
             ListFilterOperator.LessThan, ListFilterOperator.LessOrEqual, ListFilterOperator.IsEmpty, ListFilterOperator.IsNotEmpty],
        ColumnKind.Checkbox => [ListFilterOperator.Equals],
        ColumnKind.Dropdown => [ListFilterOperator.Equals, ListFilterOperator.NotEquals, ListFilterOperator.IsEmpty, ListFilterOperator.IsNotEmpty],
        _ => [ListFilterOperator.Contains, ListFilterOperator.Equals, ListFilterOperator.NotEquals, ListFilterOperator.NotContains,
              ListFilterOperator.StartsWith, ListFilterOperator.IsEmpty, ListFilterOperator.IsNotEmpty],
    };

    public static bool TakesValue(ListFilterOperator op) => op is not (ListFilterOperator.IsEmpty or ListFilterOperator.IsNotEmpty);
}
