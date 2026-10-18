using System.Globalization;
using ClosedXML.Excel;
using Workbench.Contracts.IncurredCost;

namespace Workbench.Application.IncurredCost;

/// <summary>
/// The package as an .xlsx workbook laid out like the DCAA ICE model: one sheet per schedule ("Sch A" … "Sch O"), each
/// with the contractor, the schedule title and the fiscal year on top, then the table; plus the checks.
/// </summary>
internal static class IceWorkbook
{
    private const string AmountFormat = "#,##0.00;(#,##0.00);\"-\"";
    private const string RateFormat = "0.0000%";
    private const string HoursFormat = "#,##0.00";

    public static string SheetName(string code) => code switch
    {
        "Cover" => "Cover",
        "U" => "Unallowable",
        _ => $"Sch {code}",
    };

    public static byte[] Create(IncurredCostPackageDto package)
    {
        using var workbook = new XLWorkbook();
        var firm = package.Schedule("Cover").Row("contractor")?.TextOf("value");
        var yearLine = $"For the fiscal year ended {package.YearEnd.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)} (FY{package.FiscalYear})";
        var status = package.Status == IncurredCostState.Submitted
            ? $"Submitted {package.SubmittedAtUtc:yyyy-MM-dd}"
            : package.Status == IncurredCostState.Certified ? $"Certified {package.CertifiedAtUtc:yyyy-MM-dd}" : $"{package.Status.ToString().ToUpperInvariant()} rev. {package.Revision}";

        foreach (var schedule in package.Schedules)
        {
            var ws = workbook.Worksheets.Add(SheetName(schedule.Code));
            ws.Cell(1, 1).Value = string.IsNullOrWhiteSpace(firm) ? "(contractor name)" : firm;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, Math.Max(2, schedule.Columns.Count)).Value = status;
            ws.Cell(1, Math.Max(2, schedule.Columns.Count)).Style.Font.Italic = true;
            ws.Cell(2, 1).Value = schedule.Code == "Cover" ? "Incurred cost submission" : $"Schedule {schedule.Code} — {schedule.Title}";
            ws.Cell(2, 1).Style.Font.Bold = true;
            ws.Cell(2, 1).Style.Font.FontSize = 13;
            ws.Cell(3, 1).Value = yearLine;
            ws.Cell(4, 1).Value = schedule.Description;
            ws.Cell(4, 1).Style.Font.Italic = true;

            const int headerRow = 6;
            for (var c = 0; c < schedule.Columns.Count; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = schedule.Columns[c].Header;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
                cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                cell.Style.Alignment.WrapText = true;
                if (schedule.Columns[c].Kind is IceColumnKind.Amount or IceColumnKind.Rate or IceColumnKind.Hours)
                {
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                }
            }

            var r = headerRow + 1;
            foreach (var row in schedule.Rows)
            {
                for (var c = 0; c < schedule.Columns.Count; c++)
                {
                    var column = schedule.Columns[c];
                    var cell = ws.Cell(r, c + 1);
                    switch (column.Kind)
                    {
                        case IceColumnKind.Amount or IceColumnKind.Rate or IceColumnKind.Hours:
                            if (row.NumberOrNull(column.Key) is { } n)
                            {
                                cell.Value = n;
                                cell.Style.NumberFormat.Format = column.Kind switch
                                {
                                    IceColumnKind.Rate => RateFormat,
                                    IceColumnKind.Hours => HoursFormat,
                                    _ => AmountFormat,
                                };
                            }

                            break;
                        case IceColumnKind.Date when DateOnly.TryParseExact(row.TextOf(column.Key), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d):
                            cell.Value = d.ToDateTime(TimeOnly.MinValue);
                            cell.Style.DateFormat.Format = "mm/dd/yyyy";
                            break;
                        default:
                            if (row.TextOf(column.Key) is { } text)
                            {
                                cell.Value = text;
                            }

                            break;
                    }
                }

                var range = ws.Range(r, 1, r, schedule.Columns.Count);
                switch (row.Kind)
                {
                    case IceRowKind.Section:
                        range.Style.Font.Bold = true;
                        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
                        break;
                    case IceRowKind.Subtotal:
                        range.Style.Font.Bold = true;
                        range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                        break;
                    case IceRowKind.Total:
                        range.Style.Font.Bold = true;
                        range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                        range.Style.Border.BottomBorder = XLBorderStyleValues.Double;
                        break;
                }

                r++;
            }

            r++;
            foreach (var note in schedule.Notes)
            {
                ws.Cell(r++, 1).Value = note;
            }

            ws.SheetView.FreezeRows(headerRow);
            ws.Columns(1, schedule.Columns.Count).AdjustToContents(headerRow, r);
            foreach (var column in ws.Columns(1, schedule.Columns.Count))
            {
                column.Width = Math.Min(Math.Max(column.Width, 12), 60);
            }

            if (schedule.Code == "N")
            {
                ws.Column(2).Width = 100;
                ws.Column(2).Style.Alignment.WrapText = true;
            }

            ws.PageSetup.PageOrientation = schedule.Columns.Count > 6 ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
            ws.PageSetup.FitToPages(1, 0);
            ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        }

        var checks = workbook.Worksheets.Add("Checks");
        checks.Cell(1, 1).Value = "Checks and warnings";
        checks.Cell(1, 1).Style.Font.Bold = true;
        string[] headers = ["Severity", "Schedule", "Check", "Detail", "Expected", "Actual", "Difference"];
        for (var c = 0; c < headers.Length; c++)
        {
            checks.Cell(3, c + 1).Value = headers[c];
            checks.Cell(3, c + 1).Style.Font.Bold = true;
            checks.Cell(3, c + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        var row_ = 4;
        foreach (var check in package.Checks)
        {
            checks.Cell(row_, 1).Value = check.Severity.ToString();
            checks.Cell(row_, 2).Value = check.Schedule;
            checks.Cell(row_, 3).Value = check.Title;
            checks.Cell(row_, 4).Value = check.Detail;
            if (check.Expected is { } e)
            {
                checks.Cell(row_, 5).Value = e;
            }

            if (check.Actual is { } a)
            {
                checks.Cell(row_, 6).Value = a;
            }

            if (check.Difference is { } d)
            {
                checks.Cell(row_, 7).Value = d;
            }

            checks.Range(row_, 5, row_, 7).Style.NumberFormat.Format = AmountFormat;
            row_++;
        }

        checks.Columns(1, 7).AdjustToContents();
        checks.Column(4).Width = 80;
        checks.Column(4).Style.Alignment.WrapText = true;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
