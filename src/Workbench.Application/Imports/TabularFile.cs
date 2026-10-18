using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Workbench.Costpoint;

namespace Workbench.Application.Imports;

/// <summary>One data row of an uploaded file. <see cref="Number"/> is the 1-based row in the file (header included).</summary>
public sealed record TabularRow(int Number, IReadOnlyList<string> Cells, int Level = 0);

/// <summary>A CSV or worksheet read into a header row and data rows. Cell values are invariant strings.</summary>
public sealed record TabularFile(string FileName, IReadOnlyList<string> Headers, IReadOnlyList<TabularRow> Rows, bool HasHierarchy = false)
{
    public const long MaxBytes = 20 * 1024 * 1024;

    public static bool IsExcel(string fileName) =>
        fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a .csv or .xlsx file. The header row is the first of the first ten non-blank rows that matches the most
    /// <paramref name="knownHeaders"/> (Costpoint reports often put a title above the column headers).
    /// </summary>
    public static TabularFile Read(string fileName, byte[] content, IEnumerable<string>? knownHeaders = null)
    {
        if (content.LongLength > MaxBytes)
        {
            throw new ArgumentException($"{fileName} is larger than {MaxBytes / 1024 / 1024} MB.");
        }

        var known = (knownHeaders ?? []).Select(FieldMatcher.Normalize).ToHashSet();
        if (IsExcel(fileName))
        {
            return ReadExcel(fileName, content, known);
        }

        if (fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{fileName} is an old .xls workbook; save it as .xlsx or .csv first.");
        }

        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var records = CostpointCsv.ReadRecords(reader)
            .Where(r => r.Fields.Any(f => !string.IsNullOrWhiteSpace(f)))
            .Select(r => new TabularRow(r.Line, r.Fields.Select(f => f.Trim()).ToList()))
            .ToList();
        return FromRows(fileName, records, known, hasHierarchy: false);
    }

    private static TabularFile ReadExcel(string fileName, byte[] content, HashSet<string> known)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(new MemoryStream(content));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"{fileName} isn't a readable .xlsx workbook ({ex.Message}).");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible) ?? workbook.Worksheets.First();
            var used = sheet.RangeUsed();
            if (used is null)
            {
                return new TabularFile(fileName, [], []);
            }

            var lastColumn = used.LastColumn().ColumnNumber();
            var rows = new List<TabularRow>();
            var hierarchy = false;
            foreach (var row in sheet.RowsUsed())
            {
                var cells = new List<string>(lastColumn);
                for (var c = 1; c <= lastColumn; c++)
                {
                    cells.Add(CellText(row.Cell(c)));
                }

                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                // Smartsheet exports keep the row hierarchy as Excel outline (grouping) levels; some exports indent
                // the primary column instead. Use whichever is present.
                var level = row.OutlineLevel;
                if (level == 0)
                {
                    level = Enumerable.Range(1, lastColumn).Select(c => row.Cell(c).Style.Alignment.Indent).FirstOrDefault(i => i > 0);
                }

                hierarchy |= level > 0;
                rows.Add(new TabularRow(row.RowNumber(), cells, level));
            }

            return FromRows(fileName, rows, known, hierarchy);
        }
    }

    private static string CellText(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsBlank)
        {
            return "";
        }

        if (value.IsDateTime)
        {
            var date = value.GetDateTime();
            return date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (value.IsNumber)
        {
            return value.GetNumber().ToString(CultureInfo.InvariantCulture);
        }

        if (value.IsBoolean)
        {
            return value.GetBoolean() ? "true" : "false";
        }

        return (value.IsError ? "" : value.ToString(CultureInfo.InvariantCulture)).Trim();
    }

    private static TabularFile FromRows(string fileName, List<TabularRow> rows, HashSet<string> known, bool hasHierarchy)
    {
        if (rows.Count == 0)
        {
            return new TabularFile(fileName, [], []);
        }

        var headerIndex = 0;
        if (known.Count > 0)
        {
            var best = -1;
            for (var i = 0; i < Math.Min(10, rows.Count); i++)
            {
                var score = rows[i].Cells.Count(c => known.Contains(FieldMatcher.Normalize(c)));
                if (score > best)
                {
                    best = score;
                    headerIndex = i;
                }
            }
        }

        var headerRow = rows[headerIndex];
        var headers = headerRow.Cells.Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.TrimStart('﻿').Trim()).ToList();
        var minLevel = rows.Skip(headerIndex + 1).Select(r => r.Level).DefaultIfEmpty(0).Min();
        var data = rows.Skip(headerIndex + 1)
            .Select(r => r with { Cells = Pad(r.Cells, headers.Count), Level = r.Level - minLevel })
            .ToList();
        return new TabularFile(fileName, headers, data, hasHierarchy);
    }

    private static IReadOnlyList<string> Pad(IReadOnlyList<string> cells, int count) =>
        cells.Count >= count ? cells : [.. cells, .. Enumerable.Repeat("", count - cells.Count)];
}
