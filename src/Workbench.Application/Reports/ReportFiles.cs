using System.Text;
using ClosedXML.Excel;
using Workbench.Contracts.Reports;

namespace Workbench.Application.Reports;

/// <summary>Renders a report result as an .xlsx workbook or a CSV file.</summary>
public static class ReportFiles
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string CsvContentType = "text/csv";

    public static ReportFileDto Render(ReportResultDto result, ReportFileFormat format) => format switch
    {
        ReportFileFormat.Xlsx => new ReportFileDto(FileName(result, "xlsx"), XlsxContentType, Xlsx(result)),
        ReportFileFormat.Csv => new ReportFileDto(FileName(result, "csv"), CsvContentType, Csv(result)),
        _ => throw new ArgumentException($"Unknown format {format}."),
    };

    public static string FileName(ReportResultDto result, string extension)
    {
        var name = new string(result.Title.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        name = name.Trim('-');
        return $"{(name.Length == 0 ? "report" : name)}-{result.GeneratedAtUtc:yyyyMMdd}.{extension}";
    }

    public static byte[] Csv(ReportResultDto result)
    {
        var csv = new StringBuilder();
        csv.AppendJoin(',', result.Columns.Select(c => Escape(c.Label))).Append("\r\n");
        foreach (var row in result.Rows)
        {
            csv.AppendJoin(',', row.Values.Select(Escape)).Append("\r\n");
        }

        // UTF-8 with a byte-order mark so Excel opens names with accents correctly.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public static byte[] Xlsx(ReportResultDto result)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(result.Title));
        var r = 1;
        sheet.Cell(r, 1).Value = result.Title;
        sheet.Cell(r, 1).Style.Font.Bold = true;
        sheet.Cell(r, 1).Style.Font.FontSize = 14;
        r++;
        if (!string.IsNullOrWhiteSpace(result.Subtitle))
        {
            sheet.Cell(r++, 1).Value = result.Subtitle;
        }

        sheet.Cell(r++, 1).Value = $"Generated {result.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC";
        r++;

        var header = r;
        for (var c = 0; c < result.Columns.Count; c++)
        {
            var cell = sheet.Cell(header, c + 1);
            cell.Value = result.Columns[c].Label;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        foreach (var row in result.Rows)
        {
            r++;
            for (var c = 0; c < result.Columns.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                Write(cell, result.Columns[c].Type, row.Values[c]);
                if (c == 0 && row.Level > 0 && row.Kind == ReportRowKind.Detail)
                {
                    cell.Style.Alignment.Indent = Math.Min(row.Level, 10);
                }
            }

            if (row.Kind != ReportRowKind.Detail)
            {
                var range = sheet.Range(r, 1, r, result.Columns.Count);
                range.Style.Font.Bold = true;
                range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            }
        }

        if (result.Rows.Count > 0)
        {
            sheet.Range(header, 1, r, result.Columns.Count).SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(header);
        r += 2;
        foreach (var note in result.Notes.Append(result.Truncated ? "The data source had more rows than a report may hold; narrow the filters." : null).OfType<string>())
        {
            sheet.Cell(r++, 1).Value = note;
        }

        sheet.Columns(1, Math.Max(1, result.Columns.Count)).AdjustToContents(header, Math.Min(r, header + 500), 8, 60);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Write(IXLCell cell, ReportColumnType type, string? value)
    {
        if (value is null)
        {
            return;
        }

        switch (type)
        {
            case ReportColumnType.Money when ReportValues.Number(value) is { } money:
                cell.Value = money;
                cell.Style.NumberFormat.Format = "#,##0.00;[Red]-#,##0.00";
                break;
            case ReportColumnType.Hours when ReportValues.Number(value) is { } hours:
                cell.Value = hours;
                cell.Style.NumberFormat.Format = "#,##0.00";
                break;
            case ReportColumnType.Rate when ReportValues.Number(value) is { } rate:
                cell.Value = rate;
                cell.Style.NumberFormat.Format = "0.00%";
                break;
            case ReportColumnType.Percent when ReportValues.Number(value) is { } percent:
                cell.Value = percent;
                cell.Style.NumberFormat.Format = "0.0%";
                break;
            case ReportColumnType.Number when ReportValues.Number(value) is { } number:
                cell.Value = number;
                break;
            case ReportColumnType.Date when ReportValues.Date(value) is { } date:
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.NumberFormat.Format = "yyyy-mm-dd";
                break;
            case ReportColumnType.Boolean:
                cell.Value = value == "true" ? "Yes" : value == "false" ? "No" : value;
                break;
            default:
                // Text is written as text so codes like 0001 or =SUM(...) stay as typed (no formula injection).
                cell.SetValue(value);
                cell.Style.NumberFormat.Format = "@";
                break;
        }
    }

    private static string SheetName(string title)
    {
        var name = new string(title.Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray()).Trim();
        return name.Length == 0 ? "Report" : name.Length > 31 ? name[..31] : name;
    }

    /// <summary>Quotes when needed, and neutralizes spreadsheet formulas (CSV injection) with a leading apostrophe.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '@' or '\t' or '\r' || (value[0] == '-' && ReportValues.Number(value) is null))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}
