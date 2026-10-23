using System.Globalization;
using System.Text;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Sheets;

/// <summary>
/// Draws a sheet (or a view of it) as a landscape letter PDF table: the title and what the view holds, a header row
/// repeated on every page, rows indented by hierarchy, page numbers. A small self-contained writer (Helvetica, no library),
/// like the DD 250's; long values are cut with an ellipsis to fit their column.
/// </summary>
internal static class SheetPdf
{
    private const float Width = 792, Height = 612, Margin = 32, RowHeight = 15, HeaderTop = 82, Bottom = Height - 36;
    private const float FontSize = 8, IndentStep = 9, Pad = 3;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static byte[] Write(ReportResultDto result, IReadOnlyList<ColumnKind> kinds)
    {
        var widths = Widths(result.Columns, kinds);
        var pages = new List<Page>();
        Page? page = null;
        var y = 0f;
        for (var r = 0; r < Math.Max(1, result.Rows.Count); r++)
        {
            var row = r < result.Rows.Count ? result.Rows[r] : null;
            var parent = row is not null && r + 1 < result.Rows.Count && result.Rows[r + 1].Level > row.Level;
            if (page is null || y + RowHeight > Bottom)
            {
                page = new Page();
                pages.Add(page);
                y = Header(page, result, kinds, widths, pages.Count == 1);
            }

            if (row is null)
            {
                page.Text(Margin + Pad, y + 11, FontSize, false, "No rows.");
                break;
            }

            var x = Margin;
            for (var c = 0; c < result.Columns.Count; c++)
            {
                var indent = c == 0 ? Math.Min(row.Level, 8) * IndentStep : 0;
                var text = Fit(Display(row.Values[c], kinds[c]), widths[c] - 2 * Pad - indent);
                if (IsNumeric(kinds[c]))
                {
                    page.TextRight(x + widths[c] - Pad, y + 11, FontSize, false, text);
                }
                else
                {
                    page.Text(x + Pad + indent, y + 11, FontSize, c == 0 && parent, text);
                }

                x += widths[c];
            }

            page.Gray();
            page.Line(Margin, y + RowHeight, Width - Margin, y + RowHeight);
            page.Black();
            y += RowHeight;
        }

        for (var i = 0; i < pages.Count; i++)
        {
            pages[i].TextRight(Width - Margin, Height - 18, 8, false, $"Page {i + 1} of {pages.Count}");
            pages[i].Text(Margin, Height - 18, 8, false, $"Generated {result.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC");
        }

        return Document(pages, result.Title);
    }

    /// <summary>Draws the title (first page) and the column header; returns where the rows start.</summary>
    private static float Header(Page page, ReportResultDto result, IReadOnlyList<ColumnKind> kinds, IReadOnlyList<float> widths, bool first)
    {
        page.Text(Margin, 44, first ? 16 : 11, true, Fit(result.Title, Width - 2 * Margin, first ? 16 : 11));
        if (result.Subtitle is { } subtitle)
        {
            page.Text(Margin, 60, 9, false, Fit(subtitle, Width - 2 * Margin, 9));
        }

        var y = HeaderTop - RowHeight;
        page.Fill(Margin, y, Width - 2 * Margin, RowHeight);
        var x = Margin;
        for (var c = 0; c < result.Columns.Count; c++)
        {
            var label = Fit(result.Columns[c].Label, widths[c] - 2 * Pad);
            if (IsNumeric(kinds[c]))
            {
                page.TextRight(x + widths[c] - Pad, y + 11, FontSize, true, label);
            }
            else
            {
                page.Text(x + Pad, y + 11, FontSize, true, label);
            }
            x += widths[c];
        }

        page.Line(Margin, y + RowHeight, Width - Margin, y + RowHeight);
        return y + RowHeight;
    }

    /// <summary>Title gets the most room; other columns by type, all scaled to the page width.</summary>
    private static List<float> Widths(IReadOnlyList<ReportColumnDto> columns, IReadOnlyList<ColumnKind> kinds)
    {
        var wanted = columns.Select((c, i) => i == 0 ? 220f : kinds[i] switch
        {
            ColumnKind.Checkbox => 44f,
            ColumnKind.Date => 62f,
            ColumnKind.Number or ColumnKind.Currency => 72f,
            ColumnKind.Person => 120f,
            _ => 100f,
        }).ToList();
        var scale = (Width - 2 * Margin) / wanted.Sum();
        return wanted.Select(w => w * scale).ToList();
    }

    private static bool IsNumeric(ColumnKind kind) => kind is ColumnKind.Number or ColumnKind.Currency;

    internal static string Display(string? raw, ColumnKind kind)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return kind switch
        {
            ColumnKind.Number when decimal.TryParse(raw, NumberStyles.Number, Inv, out var n) => n.ToString("#,0.##########", Us),
            ColumnKind.Currency when decimal.TryParse(raw, NumberStyles.Number, Inv, out var m) => m.ToString("C2", Us),
            ColumnKind.Date when DateOnly.TryParseExact(raw, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) => d.ToString("MM/dd/yyyy", Inv),
            ColumnKind.Checkbox => raw == "true" ? "Yes" : string.Empty,
            _ => raw.ReplaceLineEndings(" "),
        };
    }

    /// <summary>Cuts text with "..." so it fits <paramref name="width"/> points.</summary>
    private static string Fit(string text, float width, float size = FontSize)
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

    /// <summary>Approximate Helvetica advance widths (per 1,000 em), enough to keep text inside its column.</summary>
    private static float TextWidth(string s, float size) => s.Sum(c => c switch
    {
        ' ' or 'i' or 'j' or 'l' or '.' or ',' or '\'' or '|' or '!' or ':' or ';' => 0.278f,
        'f' or 't' or 'r' or 'I' or '(' or ')' or '-' or '/' => 0.333f,
        'm' or 'M' or 'W' => 0.833f,
        'w' => 0.722f,
        >= 'A' and <= 'Z' => 0.667f,
        >= '0' and <= '9' => 0.556f,
        _ => 0.556f,
    }) * size;

    private sealed class Page
    {
        public StringBuilder Ops { get; } = new();

        public void Line(float x1, float y1, float x2, float y2) => Ops.Append(Inv, $"{x1:0.##} {Height - y1:0.##} m {x2:0.##} {Height - y2:0.##} l S\n");

        public void Fill(float x, float y, float w, float h) => Ops.Append(Inv, $"0.91 0.93 0.97 rg {x:0.##} {Height - y - h:0.##} {w:0.##} {h:0.##} re f 0 0 0 rg\n");

        public void Gray() => Ops.Append("0.8 0.8 0.8 RG\n");

        public void Black() => Ops.Append("0 0 0 RG\n");

        public void Text(float x, float y, float size, bool bold, string text)
        {
            if (text.Length > 0)
            {
                Ops.Append(Inv, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {Height - y:0.##} Td ({Escape(text)}) Tj ET\n");
            }
        }

        public void TextRight(float right, float y, float size, bool bold, string text) => Text(right - TextWidth(text, size), y, size, bold, text);
    }

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
                default:
                    // WinAnsi covers Latin-1: write those as octal escapes, anything else as '?'.
                    if (c is >= ' ' and <= '~')
                    {
                        b.Append(c);
                    }
                    else if (c is >= ' ' and <= 'ÿ')
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

    private static byte[] Document(List<Page> pages, string title)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            $"<< /Producer (Workbench) /Title ({Escape(title)}) >>",
        };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = "0.5 w\n" + page.Ops;
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
