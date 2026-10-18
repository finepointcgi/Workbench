using Workbench.Application.MyWork;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.MyWork;

public sealed class MyWorkCalculatorTests
{
    private static readonly DateOnly Wednesday = new(2026, 10, 14);

    private static readonly ColumnDto Status = new(Guid.NewGuid(), "Status", ColumnKind.Dropdown, 0, ["Not started", "In progress", "Done"], null);
    private static readonly ColumnDto Owner = new(Guid.NewGuid(), "Owner", ColumnKind.Person, 1, [], null);
    private static readonly ColumnDto Reviewer = new(Guid.NewGuid(), "Reviewer", ColumnKind.Person, 2, [], null);
    private static readonly ColumnDto Due = new(Guid.NewGuid(), "Due date", ColumnKind.Date, 3, [], null);
    private static readonly ColumnDto Logged = new(Guid.NewGuid(), "Logged", ColumnKind.Date, 4, [], null);

    internal static SheetDetail Sheet(bool scheduling, params ColumnDto[] columns) =>
        new(new SheetSummary(Guid.NewGuid(), scheduling ? "Schedule" : "Tracker", null, "Workspace", 1, scheduling, null, DateTime.UtcNow), columns);

    internal static ItemDto Item(string title, DateOnly? finish = null, params (ColumnDto Column, string? Value)[] values) =>
        new(Guid.NewGuid(), null, 0, title, null, finish, null, values.ToDictionary(v => v.Column.Id.ToString(), v => v.Value));

    [Theory]
    [InlineData("2026-10-12", "2026-10-18")] // Monday
    [InlineData("2026-10-14", "2026-10-18")] // Wednesday
    [InlineData("2026-10-18", "2026-10-18")] // Sunday ends its own week
    public void Week_runs_monday_to_sunday(string today, string end)
    {
        Assert.Equal(DateOnly.Parse(end), MyWorkCalculator.WeekEnd(DateOnly.Parse(today)));
        Assert.Equal(DateOnly.Parse(end).AddDays(-6), MyWorkCalculator.WeekStart(DateOnly.Parse(today)));
    }

    [Theory]
    [InlineData("2026-10-13", false, MyWorkBucket.Overdue)]
    [InlineData("2026-10-14", false, MyWorkBucket.DueThisWeek)] // due today
    [InlineData("2026-10-18", false, MyWorkBucket.DueThisWeek)]
    [InlineData("2026-10-19", false, MyWorkBucket.Later)]
    [InlineData(null, false, MyWorkBucket.NoDate)]
    [InlineData("2026-10-01", true, MyWorkBucket.Done)] // done is never overdue
    public void Buckets_by_due_date(string? due, bool done, MyWorkBucket expected) =>
        Assert.Equal(expected, MyWorkCalculator.Bucket(due is null ? null : DateOnly.Parse(due), done, Wednesday));

    [Fact]
    public void Scheduling_sheets_use_finish_and_others_use_the_due_date_column()
    {
        var finish = new DateOnly(2026, 11, 1);
        var item = Item("Task", finish, (Due, "2026-10-20"), (Logged, "2026-01-01"));

        Assert.Equal(finish, MyWorkCalculator.DueDate(Sheet(true, Due, Logged), item));
        Assert.Equal(new DateOnly(2026, 10, 20), MyWorkCalculator.DueDate(Sheet(false, Logged, Due), item)); // prefers "Due date"
        Assert.Equal(new DateOnly(2026, 1, 1), MyWorkCalculator.DueDate(Sheet(false, Logged), item));      // any Date column
        Assert.Equal(finish, MyWorkCalculator.DueDate(Sheet(false, Logged), Item("No value", finish)));    // falls back to Finish
    }

    [Fact]
    public void Matches_any_person_column_ignoring_case_and_lists()
    {
        var sheet = Sheet(false, Owner, Reviewer, Status);
        ItemDto[] items =
        [
            Item("Mine", null, (Owner, "PM@Example.com")),
            Item("Reviewing", null, (Owner, "someone@example.com"), (Reviewer, "a@example.com; pm@example.com")),
            Item("Theirs", null, (Owner, "someone@example.com")),
            Item("Similar", null, (Owner, "pm@example.com.au")),
        ];

        var mine = MyWorkCalculator.ItemsFor(sheet, items, "pm@example.com", Wednesday).Select(i => i.Item.Title);

        Assert.Equal(["Mine", "Reviewing"], mine);
    }

    [Fact]
    public void Groups_orders_and_counts()
    {
        var sheet = Sheet(true, Owner, Status);
        var me = (Owner, (string?)"pm@example.com");
        ItemDto[] items =
        [
            Item("Later", new DateOnly(2026, 10, 30), me),
            Item("Overdue", new DateOnly(2026, 10, 10), me, (Status, "In progress")),
            Item("Friday", new DateOnly(2026, 10, 16), me),
            Item("Today", Wednesday, me),
            Item("Undated", null, me),
            Item("Done this week", new DateOnly(2026, 10, 12), me, (Status, "Done")),
            Item("Done last week", new DateOnly(2026, 10, 5), me, (Status, "done")),
        ];

        var work = MyWorkCalculator.Summarize("pm@example.com", Wednesday, MyWorkCalculator.ItemsFor(sheet, items, "pm@example.com", Wednesday));

        Assert.Equal(["Overdue", "Today", "Friday", "Later", "Undated", "Done last week", "Done this week"], work.Items.Select(i => i.Item.Title));
        Assert.Equal(new MyWorkCounts(Assigned: 5, DueThisWeek: 2, Overdue: 1, DoneThisWeek: 1), work.Counts);
        Assert.Equal("In progress", work.Items[0].Status);
        Assert.Equal(Status, work.Items[0].StatusColumn);
        Assert.Equal(new DateOnly(2026, 10, 18), work.WeekEnd);
    }
}
