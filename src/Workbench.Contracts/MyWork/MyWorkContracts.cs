using Workbench.Contracts.Sheets;

namespace Workbench.Contracts.MyWork;

/// <summary>When an open item is due, relative to the current week (Monday to Sunday). Done items are <see cref="Done"/>.</summary>
public enum MyWorkBucket
{
    Overdue,
    DueThisWeek,
    Later,
    NoDate,
    Done,
}

/// <summary>
/// An item assigned to the current user. <paramref name="Due"/> is the item's Finish, or for sheets without
/// scheduling its Date column. <paramref name="StatusColumn"/> is the sheet's "Status" Dropdown, if it has one;
/// <paramref name="Item"/> is the full item so a client can send the full values when it changes the status.
/// </summary>
public sealed record MyWorkItem(
    Guid SheetId,
    string SheetName,
    ItemDto Item,
    DateOnly? Due,
    string? Status,
    ColumnDto? StatusColumn,
    MyWorkBucket Bucket);

/// <param name="Assigned">Open items assigned to me.</param>
/// <param name="DoneThisWeek">Done items due this week (items don't record when they were completed).</param>
public sealed record MyWorkCounts(int Assigned, int DueThisWeek, int Overdue, int DoneThisWeek);

/// <summary>Items are ordered by bucket, then due date, then sheet and title.</summary>
public sealed record MyWorkDto(string Email, DateOnly Today, DateOnly WeekEnd, MyWorkCounts Counts, IReadOnlyList<MyWorkItem> Items);

public interface IMyWorkService
{
    /// <summary>Items on every sheet the current user can see where any Person column is the user's email.</summary>
    Task<MyWorkDto> GetAsync(CancellationToken cancellationToken = default);
}
