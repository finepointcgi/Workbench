using System.Globalization;
using System.Text;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>What a Gantt PDF draws: the plan's rows in outline order and what to mark on them.</summary>
public sealed record GanttPdfChart(
    string PlanName,
    IReadOnlyList<PlanSheetRow> Rows,
    IReadOnlySet<Guid> Critical,
    IReadOnlySet<Guid> Conflicts,
    IReadOnlyDictionary<Guid, (DateOnly? Start, DateOnly? Finish)>? Baseline,
    string? BaselineName,
    WorkCalendar Calendar,
    DateOnly Today);

/// <summary>
/// Draws the Gantt chart as a landscape PDF: an optional task table (ID, name, duration, start, finish) beside the timeline,
/// bars (critical ones red, % complete shaded), summary brackets, milestones, links, baseline bars under the bars, days off
/// shaded and today marked. <see cref="GanttPdfRequest.FitToPage"/> scales the date range to one page wide (and the rows to
/// one page tall when they fit at a readable size); otherwise the timeline keeps a readable scale and continues on more
/// pages across. A small self-contained PDF writer: Helvetica, lines and filled shapes, no library.
/// </summary>
public static class GanttPdf
{
    public const string ContentType = "application/pdf";

    private const float Margin = 28, HeaderHeight = 46, FooterHeight = 18, ScaleHeight = 26;
    private const float IdWidth = 24, DurationWidth = 40, DateWidth = 56;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Colors (RGB 0–1).
    private static readonly (float, float, float) Ink = (0.13f, 0.15f, 0.18f), Muted = (0.42f, 0.45f, 0.5f), Grid = (0.85f, 0.87f, 0.9f),
        DayOff = (0.94f, 0.95f, 0.96f), Bar = (0.24f, 0.47f, 0.85f), BarDone = (0.13f, 0.3f, 0.62f), CriticalBar = (0.82f, 0.22f, 0.2f),
        CriticalDone = (0.6f, 0.12f, 0.1f), BaselineBar = (0.6f, 0.62f, 0.66f), TodayLine = (0.1f, 0.6f, 0.35f), Header = (0.93f, 0.95f, 0.98f);

    public static byte[] Write(GanttPdfChart chart, GanttPdfRequest request)
    {
        var (pageWidth, pageHeight) = request.Paper switch
        {
            GanttPaperSize.Tabloid => (1224f, 792f),
            GanttPaperSize.A4 => (842f, 595f),
            _ => (792f, 612f),
        };

        // Rows in the range: a task (or summary) shows when its dates overlap it; undated rows only without a range.
        var dated = chart.Rows.Where(r => (r.Start ?? r.Finish) is not null).ToList();
        var first = request.From ?? (dated.Count > 0 ? dated.Min(r => (r.Start ?? r.Finish)!.Value) : chart.Today);
        var last = request.To ?? (dated.Count > 0 ? dated.Max(r => (r.Finish ?? r.Start)!.Value) : chart.Today);
        if (request.From is null)
        {
            first = first.AddDays(-2);
        }

        if (request.To is null)
        {
            last = last.AddDays(3);
        }

        if (last < first)
        {
            last = first;
        }

        var rows = chart.Rows.Where(r => (r.Start ?? r.Finish) is not { } s
                ? request.From is null && request.To is null
                : s <= last && (r.Finish ?? s) >= first)
            .ToList();

        var tableWidth = request.IncludeTable ? IdWidth + NameWidth(pageWidth) + DurationWidth + DateWidth * 2 : 0;
        var timelineLeft = Margin + tableWidth;
        var timelineWidth = pageWidth - Margin - timelineLeft;
        var days = last.DayNumber - first.DayNumber + 1;
        var bodyTop = Margin + HeaderHeight + ScaleHeight;
        var bodyHeight = pageHeight - bodyTop - Margin - FooterHeight;

        float rowHeight = 18;
        float dayWidth;
        if (request.FitToPage)
        {
            dayWidth = timelineWidth / days;
            if (rows.Count > 0 && rows.Count * rowHeight > bodyHeight && bodyHeight / rows.Count >= 11)
            {
                rowHeight = bodyHeight / rows.Count;
            }
        }
        else
        {
            dayWidth = days <= 90 ? 14 : days <= 400 ? 4 : 1.5f;
        }

        var daysPerPage = Math.Max((int)Math.Floor(timelineWidth / dayWidth), 1);
        var columns = request.FitToPage ? 1 : (int)Math.Ceiling(days / (double)daysPerPage);
        var rowsPerPage = Math.Max((int)Math.Floor(bodyHeight / rowHeight), 1);
        var bands = Math.Max((int)Math.Ceiling(rows.Count / (double)rowsPerPage), 1);

        var pages = new List<Page>();
        for (var band = 0; band < bands; band++)
        {
            var bandRows = rows.Skip(band * rowsPerPage).Take(rowsPerPage).ToList();
            for (var column = 0; column < columns; column++)
            {
                var from = first.AddDays(column * daysPerPage);
                var to = request.FitToPage ? last : Min(from.AddDays(daysPerPage - 1), last);
                var page = new Page(pageWidth, pageHeight);
                pages.Add(page);
                var scale = new Scale(from, to, timelineLeft, dayWidth);
                DrawHeader(page, chart, request, first, last);
                DrawTimelineScale(page, scale, bodyTop, rowHeight * bandRows.Count, chart.Calendar);
                if (request.IncludeTable)
                {
                    DrawTable(page, bandRows, pageWidth, bodyTop, rowHeight);
                }

                DrawBars(page, chart, bandRows, scale, bodyTop, rowHeight);
            }
        }

        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            p.Color(Muted, fill: true);
            p.Text(Margin, pageHeight - Margin + 4, 7, false, $"Printed {chart.Today.ToString("MMM d, yyyy", Inv)} from Workbench");
            p.TextRight(pageWidth - Margin, pageHeight - Margin + 4, 7, false, $"Page {i + 1} of {pages.Count}");
        }

        return Document(pages, chart.PlanName);
    }

    private static float NameWidth(float pageWidth) => pageWidth >= 1000 ? 260 : 170;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static void DrawHeader(Page p, GanttPdfChart chart, GanttPdfRequest request, DateOnly first, DateOnly last)
    {
        p.Color(Ink, fill: true);
        p.Text(Margin, Margin + 14, 14, true, Fit(chart.PlanName, p.Width - 2 * Margin - 260, 14));
        p.Color(Muted, fill: true);
        var range = $"{first.ToString("MMM d, yyyy", Inv)} to {last.ToString("MMM d, yyyy", Inv)}";
        p.Text(Margin, Margin + 28, 8, false, $"Gantt chart · {range}" + (chart.BaselineName is { } b ? $" · compared with baseline \"{b}\"" : ""));

        // Legend, right-aligned.
        var x = p.Width - Margin - 250;
        var y = Margin + 6;
        Legend(p, ref x, y, Bar, "Task");
        Legend(p, ref x, y, CriticalBar, "Critical path");
        if (chart.Baseline is not null)
        {
            Legend(p, ref x, y, BaselineBar, "Baseline");
        }

        if (chart.Conflicts.Count > 0)
        {
            p.Color(CriticalBar, fill: true);
            p.Text(p.Width - Margin - 250, y + 20, 7, true, $"! {chart.Conflicts.Count} constraint conflict{(chart.Conflicts.Count == 1 ? "" : "s")}: see the marked tasks");
        }
    }

    private static void Legend(Page p, ref float x, float y, (float, float, float) color, string label)
    {
        p.Color(color, fill: true);
        p.FillRect(x, y, 14, 6);
        p.Color(Ink, fill: true);
        p.Text(x + 18, y + 6, 7, false, label);
        x += 22 + TextWidth(label, 7) + 10;
    }

    private sealed record Scale(DateOnly From, DateOnly To, float Left, float DayWidth)
    {
        public float X(DateOnly date) => Left + (date.DayNumber - From.DayNumber) * DayWidth;

        public float Right => X(To.AddDays(1));

        public float Clamp(float x) => Math.Clamp(x, Left, Right);
    }

    private static void DrawTimelineScale(Page p, Scale scale, float bodyTop, float bodyHeight, WorkCalendar calendar)
    {
        var top = bodyTop - ScaleHeight;
        p.Color(Header, fill: true);
        p.FillRect(scale.Left, top, scale.Right - scale.Left, ScaleHeight);

        // Days off, shaded under the bars.
        if (scale.DayWidth >= 1.2f)
        {
            p.Color(DayOff, fill: true);
            for (var d = scale.From; d <= scale.To; d = d.AddDays(1))
            {
                if (!calendar.IsWorkingDay(d))
                {
                    p.FillRect(scale.X(d), bodyTop, scale.DayWidth, bodyHeight);
                }
            }
        }

        // Upper band: months (years when months are too narrow); lower band: days, weeks or months.
        var monthWide = scale.DayWidth * 30 >= 40;
        p.Color(Grid, fill: false);
        p.Color(Ink, fill: true);
        for (var d = new DateOnly(scale.From.Year, monthWide ? scale.From.Month : 1, 1); d <= scale.To; d = monthWide ? d.AddMonths(1) : d.AddYears(1))
        {
            var x = scale.Clamp(scale.X(d));
            var end = scale.Clamp(scale.X(monthWide ? d.AddMonths(1) : d.AddYears(1)));
            p.Line(x, top, x, bodyTop + bodyHeight);
            var label = monthWide ? d.ToString(end - x >= 70 ? "MMMM yyyy" : "MMM yy", Inv) : d.Year.ToString(Inv);
            if (end - x >= TextWidth(label, 7) + 4)
            {
                p.Text(x + 3, top + 10, 7, true, label);
            }
        }

        p.Line(scale.Left, top + 13, scale.Right, top + 13);
        if (scale.DayWidth >= 9)
        {
            for (var d = scale.From; d <= scale.To; d = d.AddDays(1))
            {
                var x = scale.X(d);
                p.Line(x, top + 13, x, top + ScaleHeight);
                p.TextCenter(x + scale.DayWidth / 2, top + 22, 6, false, d.Day.ToString(Inv));
            }
        }
        else if (scale.DayWidth * 7 >= 26)
        {
            for (var d = scale.From.AddDays(-(((int)scale.From.DayOfWeek + 6) % 7)); d <= scale.To; d = d.AddDays(7))
            {
                var x = scale.X(d);
                if (x >= scale.Left)
                {
                    p.Line(x, top + 13, x, top + ScaleHeight);
                    p.Text(x + 2, top + 22, 6, false, d.ToString("MMM d", Inv));
                }
            }
        }
        else if (monthWide)
        {
            for (var d = new DateOnly(scale.From.Year, scale.From.Month, 1); d <= scale.To; d = d.AddMonths(1))
            {
                var x = scale.Clamp(scale.X(d));
                if (scale.Clamp(scale.X(d.AddMonths(1))) - x >= 16)
                {
                    p.Text(x + 2, top + 22, 6, false, d.ToString("MMM", Inv));
                }
            }
        }

        p.Rect(scale.Left, top, scale.Right - scale.Left, ScaleHeight + bodyHeight);
    }

    private static void DrawTable(Page p, IReadOnlyList<PlanSheetRow> rows, float pageWidth, float bodyTop, float rowHeight)
    {
        var nameWidth = NameWidth(pageWidth);
        var top = bodyTop - ScaleHeight;
        var x0 = Margin;
        float[] widths = [IdWidth, nameWidth, DurationWidth, DateWidth, DateWidth];
        string[] headers = ["ID", "Task name", "Duration", "Start", "Finish"];
        var total = widths.Sum();
        p.Color(Header, fill: true);
        p.FillRect(x0, top, total, ScaleHeight);
        p.Color(Ink, fill: true);
        var x = x0;
        for (var c = 0; c < widths.Length; c++)
        {
            p.Text(x + 3, top + 17, 7, true, headers[c]);
            x += widths[c];
        }

        var size = Math.Min(7.5f, rowHeight * 0.5f);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var y = bodyTop + i * rowHeight;
            var baseline = y + rowHeight / 2 + size * 0.35f;
            p.Color(Grid, fill: false);
            p.Line(x0, y + rowHeight, x0 + total, y + rowHeight);
            p.Color(Ink, fill: true);
            x = x0;
            p.TextRight(x + IdWidth - 3, baseline, size, false, (row.Id ?? i + 1).ToString(Inv));
            x += IdWidth;
            var indent = Math.Min(row.Level - 1, 8) * 8;
            p.Text(x + 3 + indent, baseline, size, row.IsSummary, Fit(row.Name, nameWidth - 6 - indent, size));
            x += nameWidth;
            p.TextRight(x + DurationWidth - 3, baseline, size, false, row.DurationDays is { } d ? (d == 1 ? "1 day" : $"{d} days") : "");
            x += DurationWidth;
            p.Text(x + 3, baseline, size, false, row.Start?.ToString("MM/dd/yy", Inv) ?? "");
            x += DateWidth;
            p.Text(x + 3, baseline, size, false, row.Finish?.ToString("MM/dd/yy", Inv) ?? "");
        }

        p.Color(Muted, fill: false);
        p.Rect(x0, top, total, ScaleHeight + rows.Count * rowHeight);
        x = x0;
        foreach (var w in widths.SkipLast(1))
        {
            x += w;
            p.Line(x, top, x, bodyTop + rows.Count * rowHeight);
        }
    }

    private static void DrawBars(Page p, GanttPdfChart chart, IReadOnlyList<PlanSheetRow> rows, Scale scale, float bodyTop, float rowHeight)
    {
        var index = rows.Select((r, i) => (r, i)).Where(x => x.r.UniqueId is not null).ToDictionary(x => x.r.UniqueId!.Value, x => x.i);
        var byNumber = rows.Where(r => r.Id is not null).ToDictionary(r => r.Id!.Value);
        var barHeight = Math.Min(10f, rowHeight * 0.5f);

        // Row lines across the timeline.
        p.Color(Grid, fill: false);
        for (var i = 0; i < rows.Count; i++)
        {
            p.Line(scale.Left, bodyTop + (i + 1) * rowHeight, scale.Right, bodyTop + (i + 1) * rowHeight);
        }

        // Links between rows on this page, as orthogonal connectors.
        p.Color(Muted, fill: false);
        foreach (var row in rows)
        {
            foreach (var link in ScheduleEngine.ParsePredecessors(row.Predecessors, out _))
            {
                if (!byNumber.TryGetValue(link.TaskNumber, out var from) || from.Start is null || row.Start is null)
                {
                    continue;
                }

                var fromStart = link.Type is DependencyKind.StartToStart or DependencyKind.StartToFinish;
                var toStart = link.Type is DependencyKind.FinishToStart or DependencyKind.StartToStart;
                var x1 = scale.Clamp(fromStart ? scale.X(from.Start.Value) : scale.X((from.Finish ?? from.Start).Value.AddDays(1)));
                var x2 = scale.Clamp(toStart ? scale.X(row.Start.Value) : scale.X((row.Finish ?? row.Start).Value.AddDays(1)));
                var y1 = bodyTop + index[from.UniqueId!.Value] * rowHeight + rowHeight / 2;
                var y2 = bodyTop + index[row.UniqueId!.Value] * rowHeight + rowHeight / 2;
                var elbow = x1 + (fromStart ? -4 : 4);
                p.Line(x1, y1, elbow, y1);
                p.Line(elbow, y1, elbow, y2);
                p.Line(elbow, y2, x2, y2);
                var dir = toStart ? 1 : -1;
                p.Color(Muted, fill: true);
                p.FillPolygon([(x2, y2), (x2 - 3 * dir, y2 - 2), (x2 - 3 * dir, y2 + 2)]);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var top = bodyTop + i * rowHeight;
            var mid = top + rowHeight / 2;
            var id = row.UniqueId ?? Guid.Empty;

            // Baseline under the bar.
            if (chart.Baseline is not null && chart.Baseline.TryGetValue(id, out var b) && (b.Start ?? b.Finish) is { } bs)
            {
                var bf = b.Finish ?? bs;
                if (bf >= scale.From && bs <= scale.To)
                {
                    p.Color(BaselineBar, fill: true);
                    var bx1 = scale.Clamp(scale.X(bs));
                    var bx2 = scale.Clamp(scale.X(bf.AddDays(1)));
                    p.FillRect(bx1, mid + barHeight / 2 + 1, Math.Max(bx2 - bx1, 1.5f), Math.Max(barHeight * 0.3f, 2));
                }
            }

            if (row.Start is not { } start)
            {
                continue;
            }

            var finish = row.Finish ?? start;
            if (finish < scale.From || start > scale.To)
            {
                continue;
            }

            var x1 = scale.Clamp(scale.X(start));
            var x2 = scale.Clamp(scale.X(finish.AddDays(1)));
            var critical = chart.Critical.Contains(id);
            if (row.IsSummary)
            {
                p.Color(Ink, fill: true);
                var h = barHeight * 0.45f;
                p.FillRect(x1, mid - h, x2 - x1, h);
                p.FillPolygon([(x1, mid), (x1 + 4, mid), (x1, mid + 4)]);
                p.FillPolygon([(x2, mid), (x2 - 4, mid), (x2, mid + 4)]);
            }
            else if (row.DurationDays == 0)
            {
                var cx = scale.X(start) + Math.Min(scale.DayWidth, 8) / 2;
                var r = barHeight / 2 + 1;
                p.Color(critical ? CriticalBar : Ink, fill: true);
                p.FillPolygon([(cx, mid - r), (cx + r, mid), (cx, mid + r), (cx - r, mid)]);
                x2 = cx + r;
            }
            else
            {
                p.Color(critical ? CriticalBar : Bar, fill: true);
                p.FillRect(x1, mid - barHeight / 2, Math.Max(x2 - x1, 1.5f), barHeight);
                if (row.PercentComplete is > 0 and var pct)
                {
                    p.Color(critical ? CriticalDone : BarDone, fill: true);
                    p.FillRect(x1, mid - barHeight / 6, Math.Max((x2 - x1) * (float)Math.Min(pct, 100) / 100, 0.5f), barHeight / 3);
                }
            }

            var label = row.Name;
            if (chart.Conflicts.Contains(id))
            {
                p.Color(CriticalBar, fill: true);
                p.Text(x2 + 3, mid + 3, 8, true, "!");
                x2 += 8;
                label += " (constraint conflict)";
            }

            if (row.Constraint is { } constraint && constraint.Kind != PlanConstraintKind.AsSoonAsPossible)
            {
                label += $" [{Abbreviation(constraint.Kind)} {constraint.Date.ToString("M/d", Inv)}]";
            }

            if (scale.Right - x2 > 30)
            {
                p.Color(Ink, fill: true);
                p.Text(x2 + 3, mid + 2.5f, Math.Min(6.5f, rowHeight * 0.45f), false, Fit(label, scale.Right - x2 - 6, 6.5f));
            }
        }

        // Today.
        if (chart.Today >= scale.From && chart.Today <= scale.To)
        {
            var x = scale.X(chart.Today) + scale.DayWidth / 2;
            p.Color(TodayLine, fill: false);
            p.Line(x, bodyTop, x, bodyTop + rows.Count * rowHeight);
        }
    }

    private static string Abbreviation(PlanConstraintKind kind) => kind switch
    {
        PlanConstraintKind.MustStartOn => "MSO",
        PlanConstraintKind.MustFinishOn => "MFO",
        PlanConstraintKind.StartNoEarlierThan => "SNET",
        PlanConstraintKind.StartNoLaterThan => "SNLT",
        PlanConstraintKind.FinishNoEarlierThan => "FNET",
        PlanConstraintKind.FinishNoLaterThan => "FNLT",
        _ => "",
    };

    private static string Fit(string s, float width, float size)
    {
        if (TextWidth(s, size) <= width)
        {
            return s;
        }

        var cut = s;
        while (cut.Length > 1 && TextWidth(cut + "...", size) > width)
        {
            cut = cut[..^1];
        }

        return cut.Length <= 1 ? "" : cut.TrimEnd() + "...";
    }

    /// <summary>Helvetica's widths, roughly: digits and most lowercase ~0.55 em, capitals wider, spaces and narrow letters less.</summary>
    private static float TextWidth(string s, float size) =>
        s.Sum(c => c is ' ' or 'i' or 'l' or 'j' or 't' or 'f' or 'I' or '.' or ',' or '\'' or '!' or '|' ? 0.28f
            : char.IsUpper(c) || c is 'm' or 'w' or 'M' or 'W' ? 0.75f : 0.55f) * size;

    private sealed class Page(float width, float height)
    {
        public float Width { get; } = width;
        public float Height { get; } = height;
        public StringBuilder Ops { get; } = new();

        private float Y(float top) => height - top;

        public void Color((float R, float G, float B) c, bool fill) =>
            Ops.Append(Inv, $"{c.R:0.###} {c.G:0.###} {c.B:0.###} {(fill ? "rg" : "RG")}\n");

        public void FillRect(float x, float y, float w, float h) => Ops.Append(Inv, $"{x:0.##} {Y(y + h):0.##} {w:0.##} {h:0.##} re f\n");

        public void Rect(float x, float y, float w, float h) => Ops.Append(Inv, $"{x:0.##} {Y(y + h):0.##} {w:0.##} {h:0.##} re S\n");

        public void Line(float x1, float y1, float x2, float y2) => Ops.Append(Inv, $"{x1:0.##} {Y(y1):0.##} m {x2:0.##} {Y(y2):0.##} l S\n");

        public void FillPolygon(IReadOnlyList<(float X, float Y)> points)
        {
            Ops.Append(Inv, $"{points[0].X:0.##} {Y(points[0].Y):0.##} m ");
            foreach (var (x, y) in points.Skip(1))
            {
                Ops.Append(Inv, $"{x:0.##} {Y(y):0.##} l ");
            }

            Ops.Append("h f\n");
        }

        /// <summary>Text with its baseline <paramref name="y"/> points from the top.</summary>
        public void Text(float x, float y, float size, bool bold, string text)
        {
            if (text.Length > 0)
            {
                Ops.Append(Inv, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {Y(y):0.##} Td ({Escape(text)}) Tj ET\n");
            }
        }

        public void TextRight(float right, float y, float size, bool bold, string text) => Text(right - TextWidth(text, size), y, size, bold, text);

        public void TextCenter(float center, float y, float size, bool bold, string text) => Text(center - TextWidth(text, size) / 2, y, size, bold, text);
    }

    /// <summary>PDF string escapes; Latin-1 letters as octal (WinAnsiEncoding), dashes and dots as ASCII, anything else as "?".</summary>
    private static string Escape(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\' or '(' or ')':
                    b.Append('\\').Append(c);
                    break;
                case '—' or '–':
                    b.Append('-');
                    break;
                case '·':
                    b.Append("\\267");
                    break;
                case '…':
                    b.Append("...");
                    break;
                case '‘' or '’':
                    b.Append('\'');
                    break;
                case '“' or '”':
                    b.Append('"');
                    break;
                case >= ' ' and <= '~':
                    b.Append(c);
                    break;
                case >= ' ' and <= 'ÿ':
                    b.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    break;
                default:
                    b.Append('?');
                    break;
            }
        }

        return b.ToString();
    }

    private static byte[] Document(List<Page> pages, string title)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            $"<< /Producer (Workbench) /Title ({Escape(title + " Gantt chart")}) >>",
        };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = "0.5 w\n" + page.Ops;
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
            var contentId = objects.Count;
            objects.Add(string.Create(Inv, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width:0} {page.Height:0}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));
            kids.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";
        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append(Inv, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append(Inv, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            output.Append(Inv, $"{o:D10} 00000 n \n");
        }

        output.Append(Inv, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
}
