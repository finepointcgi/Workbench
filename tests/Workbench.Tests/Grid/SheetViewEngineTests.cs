using System.Globalization;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Grid;

namespace Workbench.Tests.Grid;

/// <summary>Sort and filter rules (shared by the grid and exports), status label colors, and clipboard blocks.</summary>
public sealed class SheetViewEngineTests
{
    private static readonly ColumnDto Status = new(Guid.NewGuid(), "Status", ColumnKind.Dropdown, 0, ["Not started", "Working on it", "Done"], null);
    private static readonly ColumnDto Hours = new(Guid.NewGuid(), "Hours", ColumnKind.Number, 1, [], null);
    private static readonly ColumnDto Due = new(Guid.NewGuid(), "Due", ColumnKind.Date, 2, [], null);
    private static readonly ColumnDto Billable = new(Guid.NewGuid(), "Billable", ColumnKind.Checkbox, 3, [], null);
    private static readonly IReadOnlyList<ColumnDto> Columns = [Status, Hours, Due, Billable];

    private static ItemDto Item(string title, int order, Guid? parent = null, string? status = null, string? hours = null, string? due = null, bool billable = false) =>
        new(Guid.NewGuid(), parent, order, title, null, null, null, new Dictionary<string, string?>
        {
            [Status.Id.ToString()] = status,
            [Hours.Id.ToString()] = hours,
            [Due.Id.ToString()] = due,
            [Billable.Id.ToString()] = billable ? "true" : null,
        });

    [Fact]
    public void A_filter_keeps_matching_rows_and_their_parents()
    {
        var phase = Item("Phase 1", 0);
        var design = Item("Design", 0, phase.Id, status: "Done");
        var build = Item("Build", 1, phase.Id, status: "Working on it");
        var phase2 = Item("Phase 2", 1);
        var test = Item("Test", 0, phase2.Id, status: "Not started");
        ItemDto[] items = [phase, design, build, phase2, test];

        var view = new SheetViewDefinition(Filters: [new ListFilterCondition(Status.Id.ToString(), ListFilterOperator.Equals, "done\nWorking on it")]);
        var rows = SheetViewEngine.Apply(items, Columns, view);

        Assert.Equal(["Phase 1", "Design", "Build"], rows.Select(r => r.Item.Title));
        Assert.Equal([0, 1, 1], rows.Select(r => r.Depth));
        Assert.Null(SheetViewEngine.Visible(items, Columns, SheetViewDefinition.Standard));
    }

    [Fact]
    public void Sorting_orders_each_level_by_type_with_blanks_last()
    {
        var a = Item("a", 0, hours: "10");
        var b = Item("B", 1, hours: "9");
        var c = Item("c", 2);
        var d = Item("d", 3, hours: "100");
        var child2 = Item("child 2", 0, d.Id, hours: "2");
        var child1 = Item("child 1", 1, d.Id, hours: "1");
        ItemDto[] items = [a, b, c, d, child2, child1];

        string[] Titles(params ListSort[] sort) => SheetViewEngine.Apply(items, Columns, new SheetViewDefinition(sort)).Select(r => r.Item.Title).ToArray();

        // Numbers as numbers (9 < 10 < 100), children sorted under their parent, the blank last either way.
        Assert.Equal(["B", "a", "d", "child 1", "child 2", "c"], Titles(new ListSort(Hours.Id.ToString())));
        Assert.Equal(["d", "child 2", "child 1", "a", "B", "c"], Titles(new ListSort(Hours.Id.ToString(), Descending: true)));
        Assert.Equal(["a", "B", "c", "d", "child 1", "child 2"], Titles(new ListSort(SheetFields.Title))); // case-insensitive
    }

    [Fact]
    public void Status_labels_sort_in_their_column_order_and_dates_by_date()
    {
        var done = Item("done", 0, status: "Done", due: "2026-12-01");
        var started = Item("started", 1, status: "Working on it", due: "2026-02-15");
        var notYet = Item("not yet", 2, status: "Not started", due: "2027-01-01");
        ItemDto[] items = [done, started, notYet];

        Assert.Equal(["not yet", "started", "done"],
            SheetViewEngine.Apply(items, Columns, new SheetViewDefinition([new ListSort(Status.Id.ToString())])).Select(r => r.Item.Title));
        Assert.Equal(["started", "done", "not yet"],
            SheetViewEngine.Apply(items, Columns, new SheetViewDefinition([new ListSort(Due.Id.ToString())])).Select(r => r.Item.Title));
    }

    [Theory]
    [InlineData(ListFilterOperator.GreaterThan, "9", new[] { "ten", "hundred" })]
    [InlineData(ListFilterOperator.LessOrEqual, "10", new[] { "nine", "ten" })]
    [InlineData(ListFilterOperator.IsEmpty, null, new[] { "blank" })]
    [InlineData(ListFilterOperator.IsNotEmpty, null, new[] { "nine", "ten", "hundred" })]
    [InlineData(ListFilterOperator.NotEquals, "10", new[] { "nine", "hundred", "blank" })]
    public void Number_filters_compare_values(ListFilterOperator op, string? value, string[] expected)
    {
        ItemDto[] items = [Item("nine", 0, hours: "9"), Item("ten", 1, hours: "10"), Item("hundred", 2, hours: "100"), Item("blank", 3)];
        var view = new SheetViewDefinition(Filters: [new ListFilterCondition(Hours.Id.ToString(), op, value)]);
        Assert.Equal(expected, SheetViewEngine.Apply(items, Columns, view).Select(r => r.Item.Title));
    }

    [Fact]
    public void Text_date_and_checkbox_filters()
    {
        ItemDto[] items = [Item("Kickoff meeting", 0, due: "2026-10-01", billable: true), Item("Design review", 1, due: "2026-11-15"), Item("Retro", 2)];
        string[] Matching(ListFilterCondition f) => SheetViewEngine.Apply(items, Columns, new SheetViewDefinition(Filters: [f])).Select(r => r.Item.Title).ToArray();

        Assert.Equal(["Design review"], Matching(new(SheetFields.Title, ListFilterOperator.Contains, "REVIEW")));
        Assert.Equal(["Kickoff meeting", "Retro"], Matching(new(SheetFields.Title, ListFilterOperator.NotContains, "review")));
        Assert.Equal(["Design review"], Matching(new(Due.Id.ToString(), ListFilterOperator.GreaterThan, "2026-10-31")));
        Assert.Equal(["Kickoff meeting"], Matching(new(Billable.Id.ToString(), ListFilterOperator.Equals, "true")));
        Assert.Equal(["Design review", "Retro"], Matching(new(Billable.Id.ToString(), ListFilterOperator.Equals, "false")));
    }

    [Fact]
    public void Every_status_color_has_readable_text()
    {
        foreach (var color in StatusColors.All)
        {
            var ratio = Contrast(color.Background, color.Text);
            Assert.True(ratio >= 4.5, $"{color.Name} text contrast is {ratio:0.00}:1, below 4.5:1.");
        }

        Assert.Equal(StatusColors.All.Count, StatusColors.All.Select(c => c.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("Done", "green")]
    [InlineData("Complete", "green")]
    [InlineData("Incomplete", "light-gray")]
    [InlineData("Not started", "light-gray")]
    [InlineData("In progress", "amber")]
    [InlineData("Working on it", "amber")]
    [InlineData("Blocked", "red")]
    [InlineData("Stuck", "red")]
    [InlineData("Waiting on customer", "blue")]
    [InlineData("Phase 3", "gray")]
    public void Labels_get_a_sensible_color_until_one_is_picked(string label, string expected) =>
        Assert.Equal(expected, StatusColors.Suggest(label).Key);

    [Fact]
    public void Clipboard_blocks_parse_as_excel_and_google_sheets_write_them()
    {
        // Excel: CRLF rows, a trailing new line, a quoted cell holding a new line and a quote.
        var rows = ClipboardText.Parse("Task\tHours\r\nDesign\t8\r\n\"Build \"\"v2\"\"\nand test\"\t12\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Task", "Hours"], rows[0]);
        Assert.Equal(["Build \"v2\"\nand test", "12"], rows[2]);

        // Google Sheets: LF only, no trailing new line, empty cells kept.
        Assert.Equal([["a", "", "c"], ["", "e", ""]], ClipboardText.Parse("a\t\tc\n\te\t"));
        Assert.Empty(ClipboardText.Parse(""));

        // What the grid copies reads back the same.
        var text = ClipboardText.Format([["Plan", "Line 1\nLine 2"], ["Say \"hi\"", null]]);
        Assert.Equal([["Plan", "Line 1\nLine 2"], ["Say \"hi\"", ""]], ClipboardText.Parse(text));
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            double Channel(int i)
            {
                var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
        }

        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
