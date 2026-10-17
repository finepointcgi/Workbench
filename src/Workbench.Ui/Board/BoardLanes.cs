using MudBlazor;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Board;

/// <summary>A board lane: one option of the lane column, or the Unassigned lane (empty <see cref="Value"/>).</summary>
public sealed record BoardLane(string Value, string Label, IReadOnlyList<ItemDto> Items)
{
    public bool IsUnassigned => Value.Length == 0;
}

/// <summary>
/// Groups items into lanes by a Dropdown column. Shared by the board and the calendar
/// (which colours items by the same column).
/// </summary>
public static class BoardLanes
{
    public const string DefaultColumnName = "Status";

    public static readonly IReadOnlyList<string> DefaultStatusOptions = ["Not started", "In progress", "Blocked", "Done"];

    private static readonly Color[] Palette =
    [
        Color.Info, Color.Warning, Color.Error, Color.Success, Color.Primary, Color.Tertiary, Color.Secondary, Color.Dark,
    ];

    /// <summary>The column named "Status" when it is a Dropdown, otherwise the first Dropdown column.</summary>
    public static ColumnDto? DefaultColumn(SheetDetail sheet)
    {
        var dropdowns = sheet.Columns.Where(c => c.Type == ColumnKind.Dropdown).OrderBy(c => c.Order).ToList();
        return dropdowns.FirstOrDefault(c => string.Equals(c.Name, DefaultColumnName, StringComparison.OrdinalIgnoreCase))
            ?? dropdowns.FirstOrDefault();
    }

    public static string ValueOf(ItemDto item, ColumnDto column) =>
        item.Values.TryGetValue(column.Id.ToString(), out var value) && !string.IsNullOrWhiteSpace(value) ? value : string.Empty;

    /// <summary>
    /// Unassigned first, then one lane per option in order. Values that aren't options any more
    /// (e.g. after an option was renamed) get their own lanes at the end so no item disappears.
    /// </summary>
    public static IReadOnlyList<BoardLane> Group(ColumnDto column, IEnumerable<ItemDto> items)
    {
        var byValue = items
            .OrderBy(i => i.Order)
            .GroupBy(i => ValueOf(i, column), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ItemDto>)g.ToList(), StringComparer.Ordinal);

        var values = new List<string> { string.Empty };
        values.AddRange(column.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.Ordinal));
        values.AddRange(byValue.Keys.Where(k => !values.Contains(k, StringComparer.Ordinal)).Order(StringComparer.Ordinal));

        return values
            .Select(v => new BoardLane(v, v.Length == 0 ? "Unassigned" : v, byValue.GetValueOrDefault(v) ?? []))
            .ToList();
    }

    /// <summary>Items that have no children.</summary>
    public static IEnumerable<ItemDto> Leaves(IReadOnlyList<ItemDto> items)
    {
        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
        return items.Where(i => !parents.Contains(i.Id));
    }

    /// <summary>A stable colour for a lane value: by option position, or Default for Unassigned / unknown values.</summary>
    public static Color ColorFor(ColumnDto column, string value)
    {
        var index = column.Options.ToList().IndexOf(value);
        return index < 0 ? Color.Default : Palette[index % Palette.Length];
    }

    /// <summary>Two-letter initials from an email such as <c>jane.doe@example.com</c> (JD) or <c>pm@example.com</c> (PM).</summary>
    public static string Initials(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var local = email.Split('@')[0];
        var parts = local.Split(['.', '_', '-', ' ', '+'], StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length switch
        {
            0 => string.Empty,
            1 => parts[0][..Math.Min(2, parts[0].Length)],
            _ => $"{parts[0][0]}{parts[^1][0]}",
        };
        return initials.ToUpperInvariant();
    }
}
