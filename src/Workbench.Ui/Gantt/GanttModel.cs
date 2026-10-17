using System.Globalization;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Gantt;

public enum GanttZoom
{
    Day,
    Week,
    Month,
}

/// <summary>One visible line of the Gantt, shared by the task list and the timeline so they stay aligned.</summary>
public sealed record GanttRow(ItemDto Item, int Index, int Depth, bool IsSummary, DateOnly? Start, DateOnly? Finish)
{
    public bool IsMilestone => !IsSummary && Start is not null && Start == Finish;

    public bool HasDates => Start is not null && Finish is not null;
}

public enum GanttDragMode
{
    Move,
    Resize,
}

/// <summary>A bar dragged by <see cref="Days"/> calendar days.</summary>
public sealed record GanttBarDrop(GanttRow Row, GanttDragMode Mode, int Days);

/// <summary>A labelled span of the timeline header.</summary>
public sealed record GanttTick(double X, double Width, string Label, bool IsWeekend = false);

public static class GanttLayout
{
    public const double RowHeight = 34;
    public const double HeaderHeight = 52;

    /// <summary>
    /// Flattens the item tree depth-first in sibling order. Summary rows span all of their dated
    /// descendants; leaves with only one date are drawn as milestones on that date.
    /// </summary>
    public static IReadOnlyList<GanttRow> BuildRows(IReadOnlyList<ItemDto> items, IReadOnlySet<Guid> collapsed)
    {
        var children = ChildLookup(items);
        var rows = new List<GanttRow>(items.Count);

        void Visit(ItemDto item, int depth)
        {
            var kids = children[item.Id].OrderBy(c => c.Order).ToList();
            var (start, finish) = Span(item, i => (i.Start, i.Finish), children);
            rows.Add(new GanttRow(item, rows.Count, depth, kids.Count > 0, start, finish));
            if (!collapsed.Contains(item.Id))
            {
                foreach (var child in kids)
                {
                    Visit(child, depth + 1);
                }
            }
        }

        foreach (var root in children[null].OrderBy(i => i.Order))
        {
            Visit(root, 0);
        }

        return rows;
    }

    /// <summary>Start and finish of an item, or the span of its dated descendants when it has children.</summary>
    public static (DateOnly? Start, DateOnly? Finish) Span(
        ItemDto item,
        Func<ItemDto, (DateOnly? Start, DateOnly? Finish)> dates,
        ILookup<Guid?, ItemDto> children)
    {
        var kids = children[item.Id].ToList();
        if (kids.Count > 0)
        {
            var spans = kids.Select(k => Span(k, dates, children)).Where(s => s.Start is not null).ToList();
            if (spans.Count > 0)
            {
                return (spans.Min(s => s.Start), spans.Max(s => s.Finish));
            }
        }

        var (start, finish) = dates(item);
        return (start ?? finish, finish ?? start);
    }

    public static ILookup<Guid?, ItemDto> ChildLookup(IReadOnlyList<ItemDto> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        return items.ToLookup(i => i.ParentId is { } p && ids.Contains(p) ? p : (Guid?)null);
    }
}

/// <summary>Maps calendar dates to x positions. Each day is <see cref="DayWidth"/> pixels wide.</summary>
public sealed class GanttScale
{
    private GanttScale(GanttZoom zoom, DateOnly start, DateOnly end, double dayWidth)
    {
        Zoom = zoom;
        Start = start;
        End = end;
        DayWidth = dayWidth;
    }

    public GanttZoom Zoom { get; }

    /// <summary>First day shown.</summary>
    public DateOnly Start { get; }

    /// <summary>Day after the last day shown.</summary>
    public DateOnly End { get; }

    public double DayWidth { get; }

    public double Width => (End.DayNumber - Start.DayNumber) * DayWidth;

    public double X(DateOnly date) => (date.DayNumber - Start.DayNumber) * DayWidth;

    public static double DayWidthFor(GanttZoom zoom) => zoom switch
    {
        GanttZoom.Day => 32,
        GanttZoom.Week => 10,
        _ => 3.5,
    };

    /// <summary>A range covering every date plus padding, snapped to whole weeks or months.</summary>
    public static GanttScale Create(GanttZoom zoom, IEnumerable<DateOnly> dates, DateOnly today)
    {
        var all = dates.Append(today).ToList();
        var (pad, minDays) = zoom switch
        {
            GanttZoom.Day => (7, 56),
            GanttZoom.Week => (14, 182),
            _ => (31, 400),
        };
        var start = all.Min().AddDays(-pad);
        var end = all.Max().AddDays(pad + 1);
        if (zoom == GanttZoom.Month)
        {
            start = new DateOnly(start.Year, start.Month, 1);
            end = new DateOnly(end.Year, end.Month, 1).AddMonths(1);
        }
        else
        {
            start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
            end = end.AddDays((7 - ((int)end.DayOfWeek + 6) % 7) % 7);
        }

        while (end.DayNumber - start.DayNumber < minDays)
        {
            end = zoom == GanttZoom.Month ? end.AddMonths(1) : end.AddDays(7);
        }

        return new GanttScale(zoom, start, end, DayWidthFor(zoom));
    }

    /// <summary>The upper header band: months (day and week zoom) or years (month zoom).</summary>
    public IReadOnlyList<GanttTick> MajorTicks() => Zoom == GanttZoom.Month
        ? Bands(d => new DateOnly(d.Year, 1, 1), d => d.AddYears(1), d => d.Year.ToString(CultureInfo.InvariantCulture))
        : Bands(d => new DateOnly(d.Year, d.Month, 1), d => d.AddMonths(1), d => d.ToString("MMMM yyyy", CultureInfo.InvariantCulture));

    /// <summary>The lower header band: days, weeks starting Monday, or months.</summary>
    public IReadOnlyList<GanttTick> MinorTicks() => Zoom switch
    {
        GanttZoom.Day => Bands(d => d, d => d.AddDays(1), d => d.Day.ToString(CultureInfo.InvariantCulture)),
        GanttZoom.Week => Bands(d => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)), d => d.AddDays(7), d => d.ToString("MMM d", CultureInfo.InvariantCulture)),
        _ => Bands(d => new DateOnly(d.Year, d.Month, 1), d => d.AddMonths(1), d => d.ToString("MMM", CultureInfo.InvariantCulture)),
    };

    /// <summary>Saturdays and Sundays, shaded in day and week zoom.</summary>
    public IEnumerable<GanttTick> Weekends()
    {
        if (Zoom == GanttZoom.Month)
        {
            yield break;
        }

        for (var d = Start; d < End; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Saturday)
            {
                yield return new GanttTick(X(d), DayWidth * 2, string.Empty, true);
            }
        }
    }

    private List<GanttTick> Bands(Func<DateOnly, DateOnly> floor, Func<DateOnly, DateOnly> next, Func<DateOnly, string> label)
    {
        var ticks = new List<GanttTick>();
        for (var d = floor(Start); d < End; d = next(d))
        {
            var from = d < Start ? Start : d;
            var to = next(d) > End ? End : next(d);
            ticks.Add(new GanttTick(X(from), X(to) - X(from), label(d),
                Zoom == GanttZoom.Day && d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
        }

        return ticks;
    }
}
