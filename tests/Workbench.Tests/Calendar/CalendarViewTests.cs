using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Board;
using Workbench.Ui.Board;
using Workbench.Ui.Calendar;
using Workbench.Ui.Views;

namespace Workbench.Tests.Calendar;

public sealed class CalendarViewTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public void Month_grid_starts_on_sunday_and_covers_the_whole_month()
    {
        // October 2026: Thursday the 1st to Saturday the 31st.
        var (first, last) = CalendarLayout.GridRange(new DateOnly(2026, 10, 17));

        Assert.Equal(new DateOnly(2026, 9, 27), first);
        Assert.Equal(new DateOnly(2026, 10, 31), last);
        Assert.Equal(DayOfWeek.Sunday, first.DayOfWeek);
        Assert.Equal(5, CalendarLayout.Build(new DateOnly(2026, 10, 1), []).Count);
    }

    [Fact]
    public void Multi_day_entry_covers_every_day_it_spans_across_weeks()
    {
        var item = ViewFakes.Item("Requirements review");
        var entry = new CalendarEntry(item, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 20));

        var weeks = CalendarLayout.Build(new DateOnly(2026, 10, 1), [entry]);
        var segments = weeks.SelectMany(w => w.Segments).ToList();

        Assert.Equal(3, segments.Count);
        for (var day = entry.Start; day <= entry.End; day = day.AddDays(1))
        {
            Assert.Single(segments, s => s.Covers(day));
        }

        Assert.DoesNotContain(segments, s => s.Covers(entry.Start.AddDays(-1)) || s.Covers(entry.End.AddDays(1)));
        Assert.True(segments[1].ContinuesBefore && segments[1].ContinuesAfter);
        Assert.Equal((0, 7), (segments[1].Column, segments[1].Span));
    }

    [Fact]
    public void Overlapping_entries_stack_in_separate_lanes()
    {
        var a = new CalendarEntry(ViewFakes.Item("A"), new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7));
        var b = new CalendarEntry(ViewFakes.Item("B"), new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 6));
        var c = new CalendarEntry(ViewFakes.Item("C"), new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 8));

        var week = CalendarLayout.Build(new DateOnly(2026, 10, 1), [a, b, c]).Single(w => w.Covers(a.Start));

        Assert.Equal(2, week.LaneCount);
        Assert.Equal(0, week.Segments.Single(s => s.Entry == a).Lane);
        Assert.Equal(1, week.Segments.Single(s => s.Entry == b).Lane);
        Assert.Equal(0, week.Segments.Single(s => s.Entry == c).Lane);
    }

    [Fact]
    public async Task Renders_a_multi_day_item_on_every_day_it_spans()
    {
        // Spans the boundary between two weeks of the current month view.
        var first = CalendarLayout.FirstOfMonth(Today);
        var start = first.AddDays(6 - (int)first.DayOfWeek + 5); // Thursday of the second week
        var finish = start.AddDays(4);                            // Monday of the third week
        var item = ViewFakes.Item("Requirements review", start: start, finish: finish);
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true))
            .Add(v => v.Items, [item]));

        var bars = Bars(calendar, item);
        Assert.Equal(2, bars.Count);
        var covered = bars
            .SelectMany(b => Days(DateOnly.Parse(b.GetAttribute("data-start")!), DateOnly.Parse(b.GetAttribute("data-end")!)))
            .ToList();
        Assert.Equal(Days(start, finish), covered);
        Assert.All(bars, b => Assert.Contains("Requirements review", b.TextContent));
    }

    [Fact]
    public async Task Shows_only_milestones_and_major_items_by_default()
    {
        var day = CalendarLayout.FirstOfMonth(Today).AddDays(9);
        var phase = ViewFakes.Item("Phase 1", start: day, finish: day.AddDays(5));
        var task = ViewFakes.Item("Write the plan", start: day, finish: day.AddDays(2), parentId: phase.Id);
        var milestone = ViewFakes.Item("Plan approved", start: day.AddDays(5), finish: day.AddDays(5), parentId: phase.Id) with { IsMilestone = true };
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true))
            .Add(v => v.Items, [phase, task, milestone]));

        Assert.NotEmpty(Bars(calendar, phase));
        Assert.NotEmpty(Bars(calendar, milestone));
        Assert.Contains("◆", Bars(calendar, milestone)[0].TextContent);
        Assert.Empty(Bars(calendar, task)); // an ordinary task stays off the key-dates calendar
        Assert.Contains("1 more item is hidden", calendar.Find("[data-testid=calendar-hidden]").TextContent);
    }

    [Fact]
    public async Task A_sheet_without_milestones_or_phases_shows_every_item()
    {
        var day = CalendarLayout.FirstOfMonth(Today).AddDays(9);
        var a = ViewFakes.Item("Ticket A", start: day, finish: day);
        var b = ViewFakes.Item("Ticket B", start: day.AddDays(1), finish: day.AddDays(1));
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true))
            .Add(v => v.Items, [a, b]));

        Assert.NotEmpty(Bars(calendar, a));
        Assert.NotEmpty(Bars(calendar, b));
        Assert.Single(calendar.FindAll("[data-testid=calendar-fallback]"));
    }

    [Fact]
    public async Task Uses_a_date_column_when_the_sheet_has_no_schedule()
    {
        var due = new ColumnDto(Guid.NewGuid(), "Due", ColumnKind.Date, 0, [], null);
        var day = CalendarLayout.FirstOfMonth(Today).AddDays(9);
        var item = ViewFakes.Item("Report", due, day.ToString("yyyy-MM-dd"));
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, due))
            .Add(v => v.Items, [item, ViewFakes.Item("Undated")]));

        var bar = Assert.Single(Bars(calendar, item));
        Assert.Equal(day.ToString("yyyy-MM-dd"), bar.GetAttribute("data-start"));
        Assert.Contains("1 item has no date", calendar.Markup);
    }

    [Fact]
    public async Task Shows_an_empty_state_without_any_dates()
    {
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false))
            .Add(v => v.Items, []));

        Assert.Contains("Add a Date column", calendar.Markup);
        Assert.Empty(calendar.FindAll(".calendar-week"));
    }

    [Fact]
    public async Task Colours_items_by_the_board_lane_column()
    {
        var status = ViewFakes.StatusColumn() with { Options = BoardLanes.DefaultStatusOptions };
        var item = ViewFakes.Item("Kickoff", status, "Done") with { Start = Today, Finish = Today };
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true, status))
            .Add(v => v.Items, [item]));

        // Not started, In progress, Blocked, Done map to info, warning, error, success.
        Assert.Contains("--mud-palette-success", Assert.Single(Bars(calendar, item)).GetAttribute("style"));
    }

    [Fact]
    public async Task Clicking_an_empty_day_quick_adds_an_item_on_that_date()
    {
        var items = new RecordingItemService();
        var changed = 0;
        await using var ctx = ViewFakes.CreateContext(items);
        var day = CalendarLayout.FirstOfMonth(Today).AddDays(14);

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true))
            .Add(v => v.Items, [])
            .Add(v => v.Changed, EventCallback.Factory.Create(this, () => changed++)));

        calendar.Find($".calendar-day[data-date='{day:yyyy-MM-dd}']").Click();
        var input = calendar.Find(".calendar-quick-add input");
        input.Input("Site visit");
        input.KeyDown("Enter");

        calendar.WaitForAssertion(() => Assert.Single(items.Creates));
        Assert.Equal("Site visit", items.Creates[0].Title);
        Assert.Equal(day, items.Creates[0].Start);
        Assert.Equal(day, items.Creates[0].Finish);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Clicking_an_item_opens_its_details()
    {
        var item = ViewFakes.Item("Kickoff", start: Today, finish: Today);
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true))
            .Add(v => v.Items, [item]));

        Bars(calendar, item).Single().Click();

        Assert.EndsWith($"sheets/{ViewFakes.SheetId}?item={item.Id}", ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Navigates_between_months()
    {
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var calendar = ctx.Render<SheetCalendarView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(true))
            .Add(v => v.Items, []));

        calendar.Find("button[aria-label='Next month']").Click();
        Assert.Contains(Today.AddMonths(1).ToString("MMMM yyyy"), calendar.Find(".calendar-title").TextContent);

        calendar.FindAll("button").Single(b => b.TextContent.Trim() == "Today").Click();
        Assert.Contains(Today.ToString("MMMM yyyy"), calendar.Find(".calendar-title").TextContent);
    }

    private static List<IElement> Bars(IRenderedComponent<SheetCalendarView> calendar, ItemDto item) =>
        calendar.FindAll($".calendar-bar[data-item-id='{item.Id}']").ToList();

    private static List<DateOnly> Days(DateOnly start, DateOnly end) =>
        Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(start.AddDays).ToList();
}
