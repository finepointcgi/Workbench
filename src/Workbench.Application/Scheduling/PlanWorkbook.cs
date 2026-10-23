using System.Globalization;
using ClosedXML.Excel;
using Workbench.Application.Imports;

namespace Workbench.Application.Scheduling;

/// <summary>One task as written to (or read from) the plan workbook. Dates and numbers are typed; text is trimmed.</summary>
public sealed record PlanSheetRow(
    int? Id,
    string OutlineNumber,
    int Level,
    string? WbsCode,
    string Name,
    int? DurationDays,
    DateOnly? Start,
    DateOnly? Finish,
    string? Predecessors,
    decimal? PercentComplete,
    string? Resources,
    string? Notes,
    Guid? UniqueId,
    bool IsSummary = false,
    Contracts.Scheduling.PlanConstraintDto? Constraint = null);

/// <summary>
/// A row read from an uploaded file: <see cref="Row"/> is its 1-based row in the file (the task ID in an MS Project file),
/// <see cref="Errors"/> what couldn't be read and <see cref="Warnings"/> what was read with a change.
/// </summary>
public sealed record PlanFileRow(int Row, PlanSheetRow Task, string? DurationText, IReadOnlyList<string> Errors, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// The plan's .xlsx layout, shared by export, the blank template and import, so an exported file imports unchanged.
/// Columns follow MS Project's names; the task name is indented by outline level and rows are grouped (collapsible in
/// Excel) under their summary task. Unique ID ties a row back to its task; leave it blank on new rows.
/// </summary>
public static class PlanWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string DateFormat = "ddd mm/dd/yyyy";

    /// <summary>Column headers in order. Import also accepts the aliases in <see cref="Aliases"/>.</summary>
    public static readonly string[] Headers =
    [
        "ID", "Outline number", "Outline level", "WBS code", "Task name", "Duration (days)", "Start", "Finish",
        "Predecessors", "% Complete", "Resource names", "Notes", "Unique ID",
    ];

    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["ID"] = ["id", "taskid", "task", "#", "rowid"],
        ["Outline number"] = ["outlinenumber", "outline", "outlineno"],
        ["Outline level"] = ["outlinelevel", "level", "indent", "indentlevel"],
        ["WBS code"] = ["wbscode", "wbs", "wbselement", "chargecode"],
        ["Task name"] = ["taskname", "name", "title", "taskdescription", "description"],
        ["Duration (days)"] = ["durationdays", "duration", "days", "workingdays"],
        ["Start"] = ["start", "startdate", "begin", "begindate"],
        ["Finish"] = ["finish", "finishdate", "end", "enddate", "due", "duedate"],
        ["Predecessors"] = ["predecessors", "predecessor", "dependson", "dependencies"],
        ["% Complete"] = ["complete", "percentcomplete", "pctcomplete", "done", "progress"],
        ["Resource names"] = ["resourcenames", "resources", "resource", "assignedto", "assignees", "assignee", "owner"],
        ["Notes"] = ["notes", "note", "comments"],
        ["Unique ID"] = ["uniqueid", "uid", "workbenchid", "itemid"],
    };

    public static byte[] Write(string planName, IReadOnlyList<PlanSheetRow> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Plan");
        for (var c = 0; c < Headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Headers[c];
        }

        var header = sheet.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        for (var i = 0; i < rows.Count; i++)
        {
            var r = i + 2;
            var row = rows[i];
            Set(sheet.Cell(r, 1), row.Id);
            sheet.Cell(r, 2).Value = row.OutlineNumber;
            sheet.Cell(r, 3).Value = row.Level;
            sheet.Cell(r, 4).Value = row.WbsCode ?? "";
            var name = sheet.Cell(r, 5);
            name.Value = row.Name;
            name.Style.Alignment.Indent = Math.Min(Math.Max(row.Level - 1, 0), 15);
            Set(sheet.Cell(r, 6), row.DurationDays);
            SetDate(sheet.Cell(r, 7), row.Start);
            SetDate(sheet.Cell(r, 8), row.Finish);
            sheet.Cell(r, 9).Value = row.Predecessors ?? "";
            if (row.PercentComplete is { } percent)
            {
                sheet.Cell(r, 10).Value = percent;
            }

            sheet.Cell(r, 11).Value = row.Resources ?? "";
            sheet.Cell(r, 12).Value = row.Notes ?? "";
            sheet.Cell(r, 13).Value = row.UniqueId?.ToString() ?? "";
            if (row.IsSummary)
            {
                sheet.Range(r, 1, r, Headers.Length).Style.Font.Bold = true;
            }

            // Excel's own grouping, so the plan collapses and expands there too (Excel allows 7 levels).
            if (row.Level > 1)
            {
                sheet.Row(r).OutlineLevel = Math.Min(row.Level - 1, 7);
            }
        }

        sheet.Outline.SummaryVLocation = XLOutlineSummaryVLocation.Top;
        sheet.Column(10).Style.NumberFormat.Format = "0\"%\"";
        sheet.Column(13).Style.Font.FontColor = XLColor.Gray;
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(rows.Count + 1, 2), Headers.Length).SetAutoFilter();
        int[] widths = [6, 10, 8, 14, 48, 10, 16, 16, 16, 11, 28, 40, 38];
        for (var c = 0; c < widths.Length; c++)
        {
            sheet.Column(c + 1).Width = widths[c];
        }

        AddInstructions(book, planName);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Set(IXLCell cell, int? value)
    {
        if (value is { } v)
        {
            cell.Value = v;
        }
    }

    private static void SetDate(IXLCell cell, DateOnly? date)
    {
        if (date is { } d)
        {
            cell.Value = d.ToDateTime(TimeOnly.MinValue);
            cell.Style.NumberFormat.Format = DateFormat;
        }
    }

    private static void AddInstructions(XLWorkbook book, string planName)
    {
        var help = book.AddWorksheet("How to import");
        string[] lines =
        [
            $"Workbench plan: {planName}",
            "",
            "Edit the Plan sheet, save it as .xlsx, then use Import on the project's Plan tab. You'll see what changes before anything is saved.",
            "The file is the whole plan: rows are tasks in order, and tasks missing from the file are removed.",
            "",
            "Outline level: 1 for top-level tasks, 2 for their subtasks, and so on (a row can be at most one level deeper than the row above).",
            "  Without an Outline level column, the Outline number (1.2.3) or the Task name's indent is used.",
            "Duration (days): working days, Monday to Friday less company holidays. 0 makes a milestone. Change Duration or Finish, not both.",
            "Start / Finish: dates. Leave Finish blank to compute it from Start and Duration.",
            "Summary tasks (rows with subtasks) take their dates and % complete from their subtasks; what you type for them is ignored.",
            "Predecessors: IDs from the ID column, e.g. 3 or 3, 5. Add SS, FF or SF for other link types and a lag in working days: 5SS+2d, 7FS-1d.",
            "% Complete: 0 to 100.",
            "Resource names: people's names or emails, separated by semicolons. They must be Workbench users.",
            "WBS code: the task's charge code.",
            "Unique ID: ties a row to its task in Workbench. Leave it blank on new rows; don't copy it to another row.",
        ];
        for (var i = 0; i < lines.Length; i++)
        {
            help.Cell(i + 1, 1).Value = lines[i];
        }

        help.Cell(1, 1).Style.Font.Bold = true;
        help.Cell(1, 1).Style.Font.FontSize = 14;
        help.Column(1).Width = 140;
    }

    /// <summary>
    /// Reads the first sheet of an uploaded .xlsx (or a .csv). Throws <see cref="ArgumentException"/> when the file can't be
    /// read or has no Task name column; problems with single cells are reported on their row.
    /// </summary>
    public static IReadOnlyList<PlanFileRow> Read(string fileName, byte[] content, out bool hasIdColumn)
    {
        var file = TabularFile.Read(fileName, content, Aliases.Values.SelectMany(a => a).Concat(Headers));
        var columns = MapHeaders(file.Headers);
        if (!columns.ContainsKey("Task name"))
        {
            throw new ArgumentException($"{fileName} has no Task name column. Download the template to see the columns Workbench reads.");
        }

        hasIdColumn = columns.ContainsKey("ID");
        var rows = new List<PlanFileRow>();
        foreach (var line in file.Rows)
        {
            string Cell(string column) => columns.TryGetValue(column, out var index) && index < line.Cells.Count ? line.Cells[index].Trim() : "";
            if (line.Cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var errors = new List<string>();
            var name = Cell("Task name");
            if (name.Length == 0)
            {
                errors.Add("Task name is empty.");
            }
            else if (name.Length > 500)
            {
                errors.Add("Task name is longer than 500 characters.");
            }

            int? id = null;
            if (Cell("ID") is { Length: > 0 } idText)
            {
                if (TryInt(idText, out var n) && n > 0)
                {
                    id = n;
                }
                else
                {
                    errors.Add($"ID \"{idText}\" isn't a positive whole number.");
                }
            }

            var outlineNumber = Cell("Outline number");
            var level = 0;
            if (Cell("Outline level") is { Length: > 0 } levelText)
            {
                if (!TryInt(levelText, out level) || level < 1)
                {
                    errors.Add($"Outline level \"{levelText}\" isn't 1 or more.");
                    level = 1;
                }
            }
            else if (outlineNumber.Length > 0)
            {
                level = outlineNumber.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            else
            {
                level = line.Level + 1;
            }

            var durationText = Cell("Duration (days)");
            int? duration = null;
            if (durationText.Length > 0)
            {
                if (ScheduleEngine.TryParseDuration(durationText, out var days))
                {
                    duration = days;
                }
                else
                {
                    errors.Add($"Duration \"{durationText}\" isn't a number of working days (like 5 or 5d; 0 for a milestone).");
                }
            }

            var start = ReadDate(Cell("Start"), "Start", errors);
            var finish = ReadDate(Cell("Finish"), "Finish", errors);
            decimal? percent = null;
            if (Cell("% Complete") is { Length: > 0 } percentText)
            {
                if (TryPercent(percentText, out var p))
                {
                    percent = p;
                }
                else
                {
                    errors.Add($"% Complete \"{percentText}\" isn't between 0 and 100.");
                }
            }

            Guid? uniqueId = null;
            if (Cell("Unique ID") is { Length: > 0 } uidText)
            {
                if (Guid.TryParse(uidText, out var uid))
                {
                    uniqueId = uid;
                }
                else
                {
                    errors.Add($"Unique ID \"{uidText}\" isn't one Workbench wrote. Leave it blank on new rows.");
                }
            }

            var wbs = Cell("WBS code");
            if (wbs.Length > 64)
            {
                errors.Add("WBS code is longer than 64 characters.");
            }

            rows.Add(new PlanFileRow(
                line.Number,
                new PlanSheetRow(id, outlineNumber, level, Blank(wbs), name, duration, start, finish, Blank(Cell("Predecessors")), percent,
                    Blank(Cell("Resource names")), Blank(Cell("Notes")), uniqueId),
                Blank(durationText),
                errors));
        }

        return rows;
    }

    private static Dictionary<string, int> MapHeaders(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < headers.Count; i++)
        {
            var normal = FieldMatcher.Normalize(headers[i]);
            if (headers[i].Trim() == "#")
            {
                normal = "#";
            }

            foreach (var (column, aliases) in Aliases)
            {
                if (!map.ContainsKey(column) && aliases.Contains(normal))
                {
                    map[column] = i;
                    break;
                }
            }
        }

        return map;
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private static bool TryInt(string text, out int value)
    {
        value = 0;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d != Math.Floor(d) || d > int.MaxValue)
        {
            return false;
        }

        value = (int)d;
        return true;
    }

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy", "M/d/yy", "M/d/yyyy h:mm tt", "M/d/yy h:mm tt", "MMM d, yyyy", "MMMM d, yyyy",
        "d MMM yyyy", "MMM d yyyy",
    ];

    /// <summary>Excel dates arrive as yyyy-MM-dd; typed text may also be US dates or MS Project's "Mon 10/6/25".</summary>
    public static DateOnly? ReadDate(string text, string column, List<string> errors)
    {
        if (text.Length == 0 || text.Equals("NA", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var t = text.Trim();
        var space = t.IndexOf(' ');
        if (space is > 1 and <= 4 && t[..space].All(char.IsLetter) && char.IsDigit(t[(space + 1)..].TrimStart().FirstOrDefault(' ')))
        {
            t = t[(space + 1)..].Trim(); // "Mon 10/6/25" → "10/6/25"
        }

        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return DateOnly.FromDateTime(parsed);
        }

        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        errors.Add($"{column} \"{text}\" isn't a date.");
        return null;
    }

    /// <summary>45, 45%, or a fraction Excel stored for a % cell (0.45).</summary>
    public static bool TryPercent(string text, out decimal percent)
    {
        var t = text.Trim();
        var hadSign = t.EndsWith('%');
        t = t.TrimEnd('%').Trim();
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out percent))
        {
            return false;
        }

        if (!hadSign && percent > 0 && percent < 1 && percent != Math.Floor(percent))
        {
            percent *= 100;
        }

        percent = Math.Round(percent, 2);
        return percent is >= 0 and <= 100;
    }
}
