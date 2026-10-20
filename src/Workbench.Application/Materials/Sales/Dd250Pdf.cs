using System.Globalization;
using System.Text;
using Workbench.Contracts.Materials;

namespace Workbench.Application.Materials;

/// <summary>
/// Draws the DD Form 250 Material Inspection and Receiving Report as a PDF (letter, the form's numbered blocks; items
/// continue on more pages). A small self-contained PDF writer: Helvetica text, boxes and lines, no library.
/// </summary>
internal static class Dd250Pdf
{
    private const float Width = 612, Height = 792, Left = 24, Right = 588, ItemsBottom = 600;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static byte[] Write(Dd250Dto d)
    {
        var rows = d.Items.Select(i => (Item: i, Lines: ItemLines(i))).ToList();
        var pages = new List<PdfPage>();
        var index = 0;
        do
        {
            var page = new PdfPage();
            pages.Add(page);
            var y = Header(page, d, pages.Count == 1);
            y = ItemHeader(page, y);
            while (index < rows.Count)
            {
                var h = 12 + (rows[index].Lines.Count - 1) * 9 + 4;
                if (y + h > ItemsBottom && page.Rows > 0)
                {
                    break;
                }

                Item(page, y, rows[index].Item, rows[index].Lines, h);
                y += h;
                page.Rows++;
                index++;
            }

            if (index >= rows.Count)
            {
                Footer(page, d, y);
            }
            else
            {
                page.Text(Left + 4, ItemsBottom + 14, 8, true, "Continued on the next page.");
            }
        }
        while (index < rows.Count);

        for (var i = 0; i < pages.Count; i++)
        {
            pages[i].Text(Left + 408, 70, 9, true, $"{i + 1} of {pages.Count}"); // Block 7.
        }

        return Document(pages);
    }

    private static List<string> ItemLines(Dd250ItemDto i)
    {
        var lines = new List<string>();
        lines.Add((i.StockNumber is { } nsn ? $"NSN {nsn}  " : "") + $"P/N {i.PartNumber}");
        lines.AddRange(Wrap(i.Description, 52));
        if (i.Lots.Count > 0)
        {
            lines.AddRange(Wrap("Lot " + string.Join(", ", i.Lots), 52));
        }

        if (i.Serials.Count > 0)
        {
            lines.AddRange(Wrap("Serial " + string.Join(", ", i.Serials), 52));
        }

        if (i.UidMarked)
        {
            lines.Add("UID-marked item (DFARS 252.211-7003)");
        }

        if (i.GovernmentFurnished)
        {
            lines.Add("Includes government-furnished material");
        }

        return lines;
    }

    private static float Header(PdfPage p, Dd250Dto d, bool first)
    {
        p.Text(Left, 34, 11, true, "MATERIAL INSPECTION AND RECEIVING REPORT");
        p.Text(Right - 150, 30, 6, false, "Form Approved OMB No. 0704-0248");
        p.Text(Right - 150, 40, 6, false, d.IsConfirmed ? "" : "DRAFT: not yet shipped");
        float y = 48;
        Box(p, Left, y, 180, 30, "1. PROC. INSTRUMENT IDEN. (CONTRACT)", d.ContractNumber);
        Box(p, Left + 180, y, 100, 30, "(ORDER) NO.", d.DeliveryOrder);
        Box(p, Left + 280, y, 124, 30, "6. INVOICE NO./DATE", d.InvoiceNumber is { } n ? $"{n}  {d.InvoiceDate:yyyy-MM-dd}" : null);
        Box(p, Left + 404, y, 50, 30, "7. PAGE  OF", null);
        Box(p, Left + 454, y, 110, 30, "8. ACCEPTANCE POINT", Point(d.AcceptancePoint));
        y += 30;
        Box(p, Left, y, 100, 30, "2. SHIPMENT NO.", d.ShipmentNumber);
        Box(p, Left + 100, y, 80, 30, "3. DATE SHIPPED", d.DateShipped?.ToString("yyyy-MM-dd", Inv));
        Box(p, Left + 180, y, 100, 30, "4. B/L  TCN", d.BillOfLading);
        Box(p, Left + 280, y, 124, 30, "5. DISCOUNT TERMS", d.DiscountTerms);
        Box(p, Left + 404, y, 160, 30, "CARRIER / PACKAGES / WEIGHT", $"{d.Carrier ?? "—"}  {d.PackageCount} pkg  {d.TotalWeight.ToString("0.#", Inv)} lb");
        y += 30;
        if (!first)
        {
            return y + 6;
        }

        Party(p, Left, y, "9. PRIME CONTRACTOR", d.PrimeContractor);
        Party(p, Left + 282, y, "10. ADMINISTERED BY", d.AdministeredBy);
        y += 62;
        Party(p, Left, y, "11. SHIPPED FROM (If other than 9)", d.ShippedFrom);
        Party(p, Left + 282, y, "12. PAYMENT WILL BE MADE BY", d.PaymentBy);
        y += 62;
        Party(p, Left, y, "13. SHIPPED TO", d.ShippedTo);
        Party(p, Left + 282, y, "14. MARKED FOR", d.MarkedFor ?? new Dd250PartyDto(null, "", null));
        return y + 68;
    }

    private static readonly (string Label, float Width)[] Columns =
        [("15. ITEM NO.", 50), ("16. STOCK/PART NO.  DESCRIPTION", 254), ("17. QUANTITY SHIP/REC'D", 66), ("18. UNIT", 34), ("19. UNIT PRICE", 70), ("20. AMOUNT", 90)];

    private static float ItemHeader(PdfPage p, float y)
    {
        var x = Left;
        foreach (var (label, w) in Columns)
        {
            p.Rect(x, y, w, 20);
            p.Text(x + 2, y + 8, 5.5f, false, label);
            x += w;
        }

        return y + 20;
    }

    private static void Item(PdfPage p, float y, Dd250ItemDto i, List<string> lines, float h)
    {
        var x = Left;
        foreach (var (_, w) in Columns)
        {
            p.Rect(x, y, w, h);
            x += w;
        }

        p.Text(Left + 3, y + 10, 8, true, i.ItemNumber);
        for (var n = 0; n < lines.Count; n++)
        {
            p.Text(Left + 53, y + 10 + n * 9, n == 0 ? 8 : 7, n == 0, lines[n]);
        }

        p.TextRight(Left + 50 + 254 + 64, y + 10, 8, false, i.Quantity.ToString("#,##0.####", Inv));
        p.Text(Left + 50 + 254 + 66 + 3, y + 10, 8, false, i.Unit);
        p.TextRight(Left + 50 + 254 + 66 + 34 + 68, y + 10, 8, false, i.UnitPrice.ToString("#,##0.00##", Inv));
        p.TextRight(Right - 2, y + 10, 8, false, i.Amount.ToString("#,##0.00", Inv));
    }

    private static void Footer(PdfPage p, Dd250Dto d, float y)
    {
        p.Rect(Left, y, 564, 16);
        p.Text(Left + 53, y + 11, 8, true, d.IsFinal ? "FINAL SHIPMENT" : "");
        p.TextRight(Right - 92, y + 11, 8, true, "TOTAL");
        p.TextRight(Right - 2, y + 11, 8, true, d.Total.ToString("#,##0.00", Inv));
        y = Math.Max(y + 24, ItemsBottom + 24);
        var origin = d.InspectionPoint == GovernmentInspectionPoint.Origin || d.AcceptancePoint == GovernmentInspectionPoint.Origin;
        p.Rect(Left, y, 376, 116);
        p.Text(Left + 3, y + 8, 6, true, "21. CONTRACT QUALITY ASSURANCE");
        p.Line(Left + 188, y + 12, Left + 188, y + 116);
        p.Text(Left + 3, y + 18, 6, false, "a. ORIGIN" + (origin ? "" : " (not used)"));
        p.Text(Left + 3, y + 28, 6, false, "[ ] CQA  [ ] ACCEPTANCE of listed items has been made by me");
        p.Text(Left + 3, y + 36, 6, false, "or under my supervision and they conform to contract,");
        p.Text(Left + 3, y + 44, 6, false, "except as noted herein or on supporting documents.");
        p.Text(Left + 191, y + 18, 6, false, "b. DESTINATION" + (origin ? " (not used)" : ""));
        p.Text(Left + 191, y + 28, 6, false, "[ ] CQA  [ ] ACCEPTANCE of listed items has been made by me");
        p.Text(Left + 191, y + 36, 6, false, "or under my supervision and they conform to contract,");
        p.Text(Left + 191, y + 44, 6, false, "except as noted herein or on supporting documents.");
        foreach (var x in new[] { Left + 3, Left + 191 })
        {
            p.Line(x, y + 84, x + 180, y + 84);
            p.Text(x, y + 92, 5.5f, false, "DATE        SIGNATURE OF AUTHORIZED GOVT. REP.");
            p.Line(x, y + 106, x + 180, y + 106);
            p.Text(x, y + 113, 5.5f, false, "TYPED NAME, TITLE, MAILING ADDRESS");
        }

        p.Rect(Left + 376, y, 188, 116);
        p.Text(Left + 379, y + 8, 6, true, "22. RECEIVER'S USE");
        p.Text(Left + 379, y + 20, 6, false, "Quantities shown in column 17 were received in");
        p.Text(Left + 379, y + 28, 6, false, "apparent good condition except as noted.");
        p.Line(Left + 379, y + 84, Left + 560, y + 84);
        p.Text(Left + 379, y + 92, 5.5f, false, "DATE RECEIVED   SIGNATURE OF AUTH. GOVT. REP.");
        y += 122;
        p.Rect(Left, y, 564, 22);
        p.Text(Left + 3, y + 8, 6, true, "23. CONTRACTOR USE ONLY");
        p.Text(Left + 3, y + 17, 7, false, (d.Items.Any(i => i.GovernmentFurnished) ? "Government-furnished material included. " : "")
            + (d.Items.Any(i => i.UidMarked) ? "UID data reported in WAWF. " : "") + $"Shipped from {d.ShippedFrom.Name}.");
    }

    private static string Point(GovernmentInspectionPoint p) => p == GovernmentInspectionPoint.Origin ? "Origin (S)" : "Destination (D)";

    private static void Box(PdfPage p, float x, float y, float w, float h, string label, string? value)
    {
        p.Rect(x, y, w, h);
        p.Text(x + 2, y + 7, 5.5f, false, label);
        if (!string.IsNullOrEmpty(value))
        {
            p.Text(x + 3, y + 22, 9, true, Fit(value, w - 6, 9));
        }
    }

    private static void Party(PdfPage p, float x, float y, string label, Dd250PartyDto party)
    {
        p.Rect(x, y, 282, 62);
        p.Text(x + 2, y + 7, 5.5f, false, label);
        p.Text(x + 196, y + 7, 5.5f, false, "CODE");
        p.Rect(x + 194, y, 88, 14);
        if (party.Code is { } code)
        {
            p.Text(x + 220, y + 10, 8, true, code);
        }

        var lines = Wrap(party.Name, 52).Concat((party.Address ?? "").Split('\n').SelectMany(l => Wrap(l, 56))).Where(l => l.Length > 0).Take(5).ToList();
        for (var n = 0; n < lines.Count; n++)
        {
            p.Text(x + 3, y + 23 + n * 9, n == 0 ? 8 : 7, n == 0, lines[n]);
        }
    }

    private static string Fit(string s, float width, float size)
    {
        var max = (int)(width / (size * 0.55f));
        return s.Length <= max ? s : s[..Math.Max(0, max - 3)] + "...";
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word.Length > width ? word[..width] : word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private sealed class PdfPage
    {
        public StringBuilder Ops { get; } = new();
        public int Rows { get; set; }

        public void Rect(float x, float y, float w, float h) => Ops.Append(Inv, $"{x:0.##} {Height - y - h:0.##} {w:0.##} {h:0.##} re S\n");

        public void Line(float x1, float y1, float x2, float y2) => Ops.Append(Inv, $"{x1:0.##} {Height - y1:0.##} m {x2:0.##} {Height - y2:0.##} l S\n");

        /// <summary>Text with its baseline <paramref name="y"/> points from the top.</summary>
        public void Text(float x, float y, float size, bool bold, string text)
        {
            if (text.Length > 0)
            {
                Ops.Append(Inv, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {Height - y:0.##} Td ({Escape(text)}) Tj ET\n");
            }
        }

        public void TextRight(float right, float y, float size, bool bold, string text) => Text(right - TextWidth(text, size), y, size, bold, text);

        private static float TextWidth(string s, float size) => s.Sum(c => char.IsDigit(c) || c is '.' or ',' ? 0.556f : c is ' ' ? 0.278f : 0.6f) * size;
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
                case '×':
                    b.Append('x');
                    break;
                default:
                    b.Append(c is >= ' ' and <= '~' ? c : '?');
                    break;
            }
        }

        return b.ToString();
    }

    private static byte[] Document(List<PdfPage> pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            "<< /Producer (Workbench) /Title (DD Form 250 Material Inspection and Receiving Report) >>",
        };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = "0.6 w\n" + page.Ops;
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
