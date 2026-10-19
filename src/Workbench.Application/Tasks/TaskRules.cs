using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Tasks;

/// <summary>
/// Pure rules shared by task assignment and the assignee-only permission: which columns are a task's status and % complete,
/// who an item is assigned to, and whether an update changes nothing else.
/// </summary>
public static class TaskRules
{
    public const string StatusColumnName = "Status";

    /// <summary>Number columns accepted as "% complete", in order of preference.</summary>
    public static readonly string[] PercentColumnNames = ["% Complete", "Percent complete", "% Done", "Progress"];

    private static readonly string[] DoneStatuses = ["Done", "Complete", "Completed"];

    public static bool IsDone(string? status) => status is not null && DoneStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase);

    public static Column? StatusColumn(IEnumerable<Column> columns) =>
        columns.FirstOrDefault(c => c.Type == ColumnType.Dropdown && c.Formula is null
            && string.Equals(c.Name, StatusColumnName, StringComparison.OrdinalIgnoreCase));

    public static Column? PercentColumn(IEnumerable<Column> columns)
    {
        var numbers = columns.Where(c => c.Type == ColumnType.Number && c.Formula is null).ToList();
        return PercentColumnNames
            .Select(n => numbers.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(c => c is not null);
    }

    /// <summary>Emails in a Person cell (lists split on , or ;), lower-cased.</summary>
    public static IEnumerable<string> People(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.ToLowerInvariant());

    /// <summary>True when the item's Assignees or any of its Person columns name <paramref name="email"/>.</summary>
    public static async Task<bool> IsAssignedAsync(IWorkbenchDbContext db, Item item, IEnumerable<Column> columns, string email, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();
        if (email.Length == 0)
        {
            return false;
        }

        if (columns.Where(c => c.Type == ColumnType.Person).Any(c => People(item.Values.GetValueOrDefault(c.Id.ToString())).Contains(email)))
        {
            return true;
        }

        return await db.Set<ItemAssignee>().AsNoTracking().AnyAsync(a => a.ItemId == item.Id && a.Email == email, ct);
    }

    /// <summary>
    /// True when <paramref name="request"/> changes nothing on <paramref name="stored"/> except the status and % complete
    /// columns. Restricted and formula columns are left out: the caller may not see them, and the permission decorator keeps
    /// their stored values anyway.
    /// </summary>
    public static bool ChangesOnlyStatus(Item stored, IReadOnlyList<Column> columns, UpdateItemRequest request)
    {
        if (!string.Equals(stored.Title, request.Title?.Trim(), StringComparison.Ordinal)
            || stored.Start != request.Start
            || stored.Finish != request.Finish
            || !string.Equals(Blank(stored.CostpointWbsId), Blank(request.CostpointWbsId), StringComparison.Ordinal))
        {
            return false;
        }

        var allowed = new HashSet<Guid>();
        if (StatusColumn(columns) is { } status)
        {
            allowed.Add(status.Id);
        }

        if (PercentColumn(columns) is { } percent)
        {
            allowed.Add(percent.Id);
        }

        foreach (var column in columns.Where(c => !c.IsRestricted && c.Formula is null && !allowed.Contains(c.Id)))
        {
            var key = column.Id.ToString();
            if (!string.Equals(Blank(stored.Values.GetValueOrDefault(key)), Blank(request.Values?.GetValueOrDefault(key)), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static string? FormatPercent(decimal? percent)
    {
        if (percent is not { } p)
        {
            return null;
        }

        if (p is < 0 or > 100)
        {
            throw new ArgumentException("% complete is between 0 and 100.");
        }

        return decimal.Round(p, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
