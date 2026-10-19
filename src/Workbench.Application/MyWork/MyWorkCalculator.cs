using Workbench.Contracts.MyWork;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.MyWork;

/// <summary>Pure rules for My Work: who an item is assigned to, when it is due and which bucket it falls in.</summary>
public static class MyWorkCalculator
{
    public const string StatusColumnName = "Status";

    private static readonly string[] DoneStatuses = ["Done", "Complete", "Completed"];

    private static readonly string[] PreferredDateColumns = ["Due", "Due date", "Finish", "End", "End date"];

    /// <summary>The Sunday that ends the Monday-to-Sunday week containing <paramref name="today"/>.</summary>
    public static DateOnly WeekEnd(DateOnly today) => today.AddDays((7 - (int)today.DayOfWeek) % 7);

    /// <summary>The Monday that starts the week containing <paramref name="today"/>.</summary>
    public static DateOnly WeekStart(DateOnly today) => WeekEnd(today).AddDays(-6);

    /// <summary>The "Status" Dropdown column, or null.</summary>
    public static ColumnDto? StatusColumn(SheetDetail sheet) =>
        sheet.Columns.FirstOrDefault(c => c.Type == ColumnKind.Dropdown
            && string.Equals(c.Name, StatusColumnName, StringComparison.OrdinalIgnoreCase));

    public static bool IsDoneStatus(string? status) =>
        status is not null && DoneStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string? ValueOf(ItemDto item, ColumnDto column) =>
        item.Values.TryGetValue(column.Id.ToString(), out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>True when any Person column holds <paramref name="email"/> (case-insensitive; lists split on , or ;).</summary>
    public static bool IsAssignedTo(ItemDto item, IEnumerable<ColumnDto> personColumns, string email) =>
        personColumns.Any(c => ValueOf(item, c)?
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(email, StringComparer.OrdinalIgnoreCase) == true);

    /// <summary>
    /// The date an item is due: Finish on scheduling sheets; otherwise the Date column (preferring one named like
    /// "Due"), falling back to Finish.
    /// </summary>
    public static DateOnly? DueDate(SheetDetail sheet, ItemDto item)
    {
        if (sheet.Summary.SchedulingEnabled)
        {
            return item.Finish;
        }

        var dateColumns = sheet.Columns.Where(c => c.Type == ColumnKind.Date).OrderBy(c => c.Order).ToList();
        var column = PreferredDateColumns
            .Select(n => dateColumns.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(c => c is not null) ?? dateColumns.FirstOrDefault();
        if (column is not null && DateOnly.TryParseExact(ValueOf(item, column), "yyyy-MM-dd", out var date))
        {
            return date;
        }

        return item.Finish;
    }

    public static MyWorkBucket Bucket(DateOnly? due, bool done, DateOnly today)
    {
        if (done)
        {
            return MyWorkBucket.Done;
        }

        if (due is not { } d)
        {
            return MyWorkBucket.NoDate;
        }

        return d < today ? MyWorkBucket.Overdue
            : d <= WeekEnd(today) ? MyWorkBucket.DueThisWeek
            : MyWorkBucket.Later;
    }

    /// <summary>
    /// The current user's items on one sheet: a Person column holds their email, or <paramref name="assignedItemIds"/>
    /// (the item's Assignees field) lists the item.
    /// </summary>
    public static IEnumerable<MyWorkItem> ItemsFor(SheetDetail sheet, IEnumerable<ItemDto> items, string email, DateOnly today,
        IReadOnlySet<Guid>? assignedItemIds = null)
    {
        var people = sheet.Columns.Where(c => c.Type == ColumnKind.Person).ToList();
        if ((people.Count == 0 && assignedItemIds is not { Count: > 0 }) || string.IsNullOrWhiteSpace(email))
        {
            yield break;
        }

        var statusColumn = StatusColumn(sheet);
        foreach (var item in items.Where(i => assignedItemIds?.Contains(i.Id) == true || IsAssignedTo(i, people, email)))
        {
            var status = statusColumn is null ? null : ValueOf(item, statusColumn);
            var due = DueDate(sheet, item);
            yield return new MyWorkItem(sheet.Summary.Id, sheet.Summary.Name, item, due, status, statusColumn,
                Bucket(due, IsDoneStatus(status), today));
        }
    }

    public static MyWorkDto Summarize(string email, DateOnly today, IEnumerable<MyWorkItem> items)
    {
        var weekStart = WeekStart(today);
        var weekEnd = WeekEnd(today);
        var ordered = items
            .OrderBy(i => i.Bucket)
            .ThenBy(i => i.Due ?? DateOnly.MaxValue)
            .ThenBy(i => i.SheetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var counts = new MyWorkCounts(
            Assigned: ordered.Count(i => i.Bucket != MyWorkBucket.Done),
            DueThisWeek: ordered.Count(i => i.Bucket == MyWorkBucket.DueThisWeek),
            Overdue: ordered.Count(i => i.Bucket == MyWorkBucket.Overdue),
            DoneThisWeek: ordered.Count(i => i.Bucket == MyWorkBucket.Done && i.Due >= weekStart && i.Due <= weekEnd));
        return new MyWorkDto(email, today, weekEnd, counts, ordered);
    }
}
