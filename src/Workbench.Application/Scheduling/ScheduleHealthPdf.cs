using System.Globalization;
using System.Text;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>
/// The DCMA 14-point assessment as a portrait letter PDF: a summary table (check, result, threshold, flagged, the outcome in
/// words), then each failing check's flagged tasks. A small self-contained writer like <see cref="GanttPdf"/>: Helvetica, no library.
/// </summary>
public static class ScheduleHealthPdf
{
    public const string ContentType = "application/pdf";

    private const float Width = 612, Height = 792, Margin = 36, Bottom = Height - 40, Line = 13, Size = 8;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Column x positions in the summary table.
    private const float ColCheck = Margin, ColResult = Margin + 168, ColThreshold = Margin + 214, ColFlagged = Margin + 330, ColDetail = Margin + 382;

    public static byte[] Write(ScheduleHealthDto health)
    {
        var pages = new List<StringBuilder>();
        StringBuilder page = null!;
        float y = 0;

        void NewPage()
        {
            page = new StringBuilder("0.5 w\n");
            pages.Add(page);
            Text(page, Margin, 40, 9, false, $"Schedule health (DCMA 14-point) - {health.PlanName}", (0.42f, 0.45f, 0.5f));
            y = 60;
        }

        void Ensure(float height)
        {
            if (y + height > Bottom)
            {
                NewPage();
            }
        }

        NewPage();
        Text(page, Margin, y + 6, 16, true, Fit($"Schedule health: {health.PlanName}", Width - 2 * Margin, 16));
        y += 24;
        Text(page, Margin, y, 9, false, $"Status date {health.StatusDate.ToString("MMM d, yyyy", Inv)}   Baseline: {health.BaselineName ?? "none"}   "
            + $"{health.DetailTasks.ToString(Inv)} tasks ({health.IncompleteTasks.ToString(Inv)} unfinished), {health.Links.ToString(Inv)} links");
        y += Line;
        Text(page, Margin, y, 11, true, $"{health.Passed.ToString(Inv)} passed, {health.Failed.ToString(Inv)} failed, "
            + $"{(health.Checks.Count - health.Passed - health.Failed).ToString(Inv)} not measured", health.Failed == 0 ? Green : Red);
        y += Line + 2;
        foreach (var note in health.Notes)
        {
            Text(page, Margin, y, Size, false, Fit(note, Width - 2 * Margin, Size), Muted);
            y += Line - 2;
        }

        y += 8;
        Fill(page, Margin, y - 10, Width - 2 * Margin, Line + 1, (0.91f, 0.93f, 0.97f));
        Text(page, ColCheck + 3, y, Size, true, "Check");
        Text(page, ColResult, y, Size, true, "Result");
        Text(page, ColThreshold, y, Size, true, "Threshold");
        Text(page, ColFlagged, y, Size, true, "Flagged");
        Text(page, ColDetail, y, Size, true, "Outcome");
        y += Line;
        foreach (var check in health.Checks)
        {
            var detail = Wrap(check.Result, Width - Margin - ColDetail, Size);
            var explanation = Wrap(check.Explanation, ColResult - ColCheck - 6, Size - 1);
            var rows = Math.Max(1 + explanation.Count, detail.Count);
            Ensure(rows * (Line - 2) + 6);
            Text(page, ColCheck + 3, y, Size, true, Fit($"{check.Number.ToString(Inv)}. {check.Name}", ColResult - ColCheck - 6, Size));
            for (var i = 0; i < explanation.Count; i++)
            {
                Text(page, ColCheck + 3, y + (i + 1) * (Line - 2), Size - 1, false, explanation[i], Muted);
            }

            Text(page, ColResult, y, Size, true, ScheduleHealthService.StatusText(check.Status), check.Status switch
            {
                HealthCheckStatus.Pass => Green,
                HealthCheckStatus.Fail => Red,
                _ => Muted,
            });
            Text(page, ColThreshold, y, Size, false, Fit(check.Threshold, ColFlagged - ColThreshold - 6, Size));
            var flagged = check.Value is { } v ? v.ToString("0.00", Inv)
                : check.Status == HealthCheckStatus.NotApplicable ? "-"
                : check.Percent is { } p ? $"{check.Count.ToString(Inv)} ({p.ToString("0.#", Inv)}%)" : check.Count.ToString(Inv);
            Text(page, ColFlagged, y, Size, false, flagged);
            for (var i = 0; i < detail.Count; i++)
            {
                Text(page, ColDetail, y + i * (Line - 2), Size, false, detail[i]);
            }

            y += rows * (Line - 2) + 4;
            page.Append(Inv, $"0.85 0.87 0.9 RG {Margin:0.##} {Height - y + 8:0.##} m {Width - Margin:0.##} {Height - y + 8:0.##} l S 0 0 0 RG\n");
            y += 4;
        }

        // The tasks behind each failing check.
        foreach (var check in health.Checks.Where(c => c.Status == HealthCheckStatus.Fail && c.Tasks.Count > 0))
        {
            Ensure(Line * 3);
            y += 8;
            Text(page, Margin, y, 11, true, $"{check.Number.ToString(Inv)}. {check.Name}: {check.Tasks.Count.ToString(Inv)} task{(check.Tasks.Count == 1 ? "" : "s")}");
            y += Line + 2;
            foreach (var task in check.Tasks)
            {
                Ensure(Line);
                Text(page, Margin + 4, y, Size, false, task.TaskNumber.ToString(Inv));
                Text(page, Margin + 30, y, Size, false, Fit($"{task.OutlineNumber}  {task.Title}", 260, Size));
                Text(page, Margin + 300, y, Size, false, Fit(task.Detail, Width - Margin - (Margin + 300), Size), Muted);
                y += Line - 1;
            }
        }

        for (var i = 0; i < pages.Count; i++)
        {
            Text(pages[i], Width - Margin - 60, Height - 22, 8, false, $"Page {(i + 1).ToString(Inv)} of {pages.Count.ToString(Inv)}", Muted);
        }

        return Document(pages, $"Schedule health {health.PlanName}");
    }

    private static readonly (float, float, float) Ink = (0.13f, 0.15f, 0.18f), Muted = (0.42f, 0.45f, 0.5f), Green = (0.1f, 0.5f, 0.23f), Red = (0.78f, 0.16f, 0.16f);

    private static void Text(StringBuilder ops, float x, float y, float size, bool bold, string text, (float R, float G, float B)? color = null)
    {
        if (text.Length == 0)
        {
            return;
        }

        var (r, g, b) = color ?? Ink;
        ops.Append(Inv, $"{r:0.###} {g:0.###} {b:0.###} rg BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {Height - y:0.##} Td ({Escape(text)}) Tj ET 0 0 0 rg\n");
    }

    private static void Fill(StringBuilder ops, float x, float y, float w, float h, (float R, float G, float B) color) =>
        ops.Append(Inv, $"{color.R:0.###} {color.G:0.###} {color.B:0.###} rg {x:0.##} {Height - y - h:0.##} {w:0.##} {h:0.##} re f 0 0 0 rg\n");

    /// <summary>Splits text into lines that fit <paramref name="width"/> points.</summary>
    private static List<string> Wrap(string text, float width, float size)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Length == 0 ? word : current + " " + word;
            if (TextWidth(next, size) <= width || current.Length == 0)
            {
                current = next;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(Fit(current, width, size));
        }

        return lines;
    }

    private static string Fit(string text, float width, float size)
    {
        if (TextWidth(text, size) <= width)
        {
            return text;
        }

        var end = text.Length;
        while (end > 0 && TextWidth(text[..end] + "...", size) > width)
        {
            end--;
        }

        return end == 0 ? string.Empty : text[..end].TrimEnd() + "...";
    }

    /// <summary>Approximate Helvetica advance widths (per 1,000 em).</summary>
    private static float TextWidth(string s, float size) => s.Sum(c => c switch
    {
        ' ' or 'i' or 'j' or 'l' or '.' or ',' or '\'' or '|' or '!' or ':' or ';' => 0.278f,
        'f' or 't' or 'r' or 'I' or '(' or ')' or '-' or '/' => 0.333f,
        'm' or 'M' or 'W' or '%' => 0.833f,
        'w' => 0.722f,
        >= 'A' and <= 'Z' => 0.667f,
        _ => 0.556f,
    }) * size;

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
                case '‘' or '’':
                    b.Append('\'');
                    break;
                case '“' or '”':
                    b.Append('"');
                    break;
                case '…':
                    b.Append("...");
                    break;
                case '÷':
                    b.Append('/');
                    break;
                default:
                    if (c is >= ' ' and <= '~')
                    {
                        b.Append(c);
                    }
                    else if (c is >= ' ' and <= 'ÿ')
                    {
                        b.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        b.Append('?');
                    }

                    break;
            }
        }

        return b.ToString();
    }

    private static byte[] Document(List<StringBuilder> pages, string title)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            $"<< /Producer (Workbench) /Title ({Escape(title)}) >>",
        };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = page.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
            var contentId = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Width:0} {Height:0}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
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
