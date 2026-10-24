using ClosedXML.Excel;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Sheets;

/// <summary>
/// Finishes a sheet's Excel export (written by <c>ReportFiles</c>): link cells become real hyperlinks, and the sheet's
/// conditional formatting colors, bolds and strikes through the same cells it does in the grid.
/// </summary>
internal static class SheetXlsx
{
    /// <param name="fields">The export's columns in order (key and type).</param>
    /// <param name="rowIds">The exported rows' item ids, in order.</param>
    /// <param name="raw">The stored values per row and column (before the export turned them into text).</param>
    public static byte[] Decorate(
        byte[] workbook, IReadOnlyList<(string Key, ColumnKind Kind)> fields, IReadOnlyList<Guid> rowIds, IReadOnlyList<IReadOnlyList<string?>> raw, SheetFormats formats)
    {
        var links = fields.Select((f, i) => (f.Kind, i)).Where(f => f.Kind == ColumnKind.Hyperlink).Select(f => f.i).ToList();
        if (rowIds.Count == 0 || (links.Count == 0 && !formats.Any))
        {
            return workbook;
        }

        using var input = new MemoryStream(workbook);
        using var book = new XLWorkbook(input);
        var sheet = book.Worksheet(1);
        var header = Enumerable.Range(1, 10).FirstOrDefault(r => sheet.Cell(r, 1).GetString() == "Title" && sheet.Cell(r, 1).Style.Font.Bold);
        if (header == 0)
        {
            return workbook;
        }

        for (var r = 0; r < rowIds.Count; r++)
        {
            var row = header + 1 + r;
            foreach (var c in links)
            {
                if (ColumnValues.Link(raw[r][c]) is { } link && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                {
                    var cell = sheet.Cell(row, c + 1);
                    cell.SetHyperlink(new XLHyperlink(uri));
                    cell.Style.Font.FontColor = XLColor.FromHtml("#1F5FBF");
                    cell.Style.Font.Underline = XLFontUnderlineValues.Single;
                }
            }

            if (!formats.Any)
            {
                continue;
            }

            for (var c = 0; c < fields.Count; c++)
            {
                var style = formats.Cell(rowIds[r], fields[c].Key);
                if (style.IsEmpty)
                {
                    continue;
                }

                var cell = sheet.Cell(row, c + 1);
                if (style.Background is { } color)
                {
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml(color.Background);
                    cell.Style.Font.FontColor = XLColor.FromHtml(color.Text);
                }

                cell.Style.Font.Bold |= style.Bold;
                cell.Style.Font.Strikethrough |= style.Strikethrough;
            }
        }

        using var output = new MemoryStream();
        book.SaveAs(output);
        return output.ToArray();
    }
}
