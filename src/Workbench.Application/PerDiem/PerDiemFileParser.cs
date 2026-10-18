using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Workbench.Domain.PerDiem;

namespace Workbench.Application.PerDiem;

/// <summary>A rate row read from a file or the GSA API, before it's saved.</summary>
public sealed record ParsedPerDiemRate(
    string Country,
    string State,
    string Destination,
    string? County,
    DateOnly SeasonStart,
    DateOnly SeasonEnd,
    decimal LodgingMax,
    decimal MieRate,
    bool IsStandard);

public sealed record ParsedMieTier(decimal Total, decimal Breakfast, decimal Lunch, decimal Dinner, decimal Incidentals, decimal FirstLastDay);

public sealed record PerDiemParseResult(IReadOnlyList<ParsedPerDiemRate> Rates, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads per diem tables: GSA's CSV and Excel files, DoD/State OCONUS CSVs in the same shape, and our own sample file.
/// Headers are matched loosely (case-insensitive, by keyword), and title rows above the header are skipped.
/// </summary>
public static class PerDiemFileParser
{
    public const string StandardDestination = "Standard CONUS";

    public static PerDiemParseResult Parse(string fileName, byte[] content, int fiscalYear, PerDiemRateKind kind)
    {
        var rows = IsExcel(fileName, content) ? XlsxReader.ReadFirstSheet(content) : ReadCsv(Encoding.UTF8.GetString(content));
        return ParseRows(rows, fiscalYear, kind);
    }

    public static bool IsExcel(string fileName, byte[] content) =>
        fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || (content.Length > 4 && content[0] == 'P' && content[1] == 'K');

    public static PerDiemParseResult ParseRows(IReadOnlyList<IReadOnlyList<string>> rows, int fiscalYear, PerDiemRateKind kind)
    {
        var warnings = new List<string>();
        var headerIndex = -1;
        Columns? columns = null;
        for (var i = 0; i < Math.Min(rows.Count, 20) && columns is null; i++)
        {
            columns = Columns.TryMatch(rows[i]);
            headerIndex = i;
        }

        if (columns is null)
        {
            throw new ArgumentException("The file has no header row with destination, lodging and M&IE columns.");
        }

        var rates = new List<ParsedPerDiemRate>();
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            string Cell(int index) => index >= 0 && index < row.Count ? row[index].Trim() : "";
            var destination = Cell(columns.Destination);
            if (destination.Length == 0 && row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var line = i + 1;
            if (!TryMoney(Cell(columns.Lodging), out var lodging) || !TryMoney(Cell(columns.Mie), out var mie))
            {
                warnings.Add($"Row {line}: lodging or M&IE isn't a number; skipped.");
                continue;
            }

            if (!TrySeason(Cell(columns.SeasonBegin), fiscalYear, isEnd: false, out var start)
                || !TrySeason(Cell(columns.SeasonEnd), fiscalYear, isEnd: true, out var end))
            {
                warnings.Add($"Row {line}: season dates aren't recognized; skipped.");
                continue;
            }

            if (end < start)
            {
                warnings.Add($"Row {line}: the season ends before it starts; skipped.");
                continue;
            }

            var state = Cell(columns.State);
            var country = columns.Country >= 0 && Cell(columns.Country).Length > 0 ? Cell(columns.Country) : "US";
            var isStandard = IsStandardName(destination) || (kind == PerDiemRateKind.Conus && destination.Length == 0 && state.Length == 0);
            if (destination.Length == 0 && !isStandard)
            {
                warnings.Add($"Row {line}: no destination; skipped.");
                continue;
            }

            if (isStandard)
            {
                (destination, state) = (StandardDestination, "");
            }

            var county = Cell(columns.County);
            rates.Add(new ParsedPerDiemRate(country, state.ToUpperInvariant().Length == 2 ? state.ToUpperInvariant() : state, destination,
                county.Length == 0 ? null : county, start, end, lodging, mie, isStandard));
        }

        if (rates.Count == 0)
        {
            throw new ArgumentException("The file has no rate rows.");
        }

        return new PerDiemParseResult(rates, warnings);
    }

    public static bool IsStandardName(string destination) =>
        destination.StartsWith("Standard", StringComparison.OrdinalIgnoreCase);

    private static bool TryMoney(string text, out decimal value) =>
        decimal.TryParse(text.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value >= 0;

    private static readonly string[] MonthDayFormats = ["MMMM d", "MMM d", "M/d", "MM/dd", "M-d", "MMMM", "MMM"];

    /// <summary>
    /// A season boundary inside the federal fiscal year: "October 1", "10/1", a month alone, or a full date. October to
    /// December fall in the calendar year before <paramref name="fiscalYear"/>. Blank means the start or end of the year.
    /// </summary>
    public static bool TrySeason(string text, int fiscalYear, bool isEnd, out DateOnly date)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            date = isEnd ? new DateOnly(fiscalYear, 9, 30) : new DateOnly(fiscalYear - 1, 10, 1);
            return true;
        }

        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        if (DateTime.TryParseExact(text, MonthDayFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            var monthOnly = !text.Any(char.IsDigit);
            var year = parsed.Month >= 10 ? fiscalYear - 1 : fiscalYear;
            var day = monthOnly && isEnd ? DateTime.DaysInMonth(year, parsed.Month) : monthOnly ? 1 : parsed.Day;
            date = new DateOnly(year, parsed.Month, Math.Min(day, DateTime.DaysInMonth(year, parsed.Month)));
            return true;
        }

        date = default;
        return false;
    }

    // ---- CSV ----

    public static IReadOnlyList<IReadOnlyList<string>> ReadCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else if (c != '﻿')
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private sealed record Columns(int Country, int State, int Destination, int County, int SeasonBegin, int SeasonEnd, int Lodging, int Mie)
    {
        public static Columns? TryMatch(IReadOnlyList<string> header)
        {
            var names = header.Select(h => h.Trim().ToUpperInvariant()).ToList();
            int Find(Func<string, bool> match) => names.FindIndex(n => match(n));
            var destination = Find(n => n is "DESTINATION" or "CITY" or "LOCATION" or "PRIMARY DESTINATION" || n.StartsWith("DESTINATION", StringComparison.Ordinal));
            var lodging = Find(n => n.Contains("LODGING", StringComparison.Ordinal));
            var mie = Find(n => n.Contains("M&IE", StringComparison.Ordinal) || n.Contains("M & IE", StringComparison.Ordinal) || n is "MIE" or "MEALS");
            if (destination < 0 || lodging < 0 || mie < 0)
            {
                return null;
            }

            return new Columns(
                Find(n => n is "COUNTRY" or "COUNTRY/STATE"),
                Find(n => n is "STATE" or "ST" or "REGION"),
                destination,
                Find(n => n.StartsWith("COUNTY", StringComparison.Ordinal) || n.StartsWith("LOCATIONS", StringComparison.Ordinal)),
                Find(n => n.Contains("BEGIN", StringComparison.Ordinal) || n.Contains("SEASON START", StringComparison.Ordinal) || n == "START"),
                Find(n => (n.Contains("SEASON", StringComparison.Ordinal) && n.Contains("END", StringComparison.Ordinal)) || n == "END"),
                lodging,
                mie);
        }
    }
}

/// <summary>Just enough of the .xlsx format to read the first worksheet's cell text (shared and inline strings, numbers).</summary>
internal static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static IReadOnlyList<IReadOnlyList<string>> ReadFirstSheet(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
            {
                var doc = Load(sharedEntry);
                shared.AddRange(doc.Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))));
            }

            var sheetPath = FirstSheetPath(zip);
            var sheet = Load(zip.GetEntry(sheetPath) ?? throw new ArgumentException("The workbook has no worksheet."));
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in sheet.Descendants(Main + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(Main + "c"))
                {
                    var index = ColumnIndex((string?)c.Attribute("r"));
                    while (index >= 0 && cells.Count < index)
                    {
                        cells.Add("");
                    }

                    var type = (string?)c.Attribute("t");
                    var value = c.Element(Main + "v")?.Value ?? "";
                    cells.Add(type switch
                    {
                        "s" when int.TryParse(value, out var i) && i < shared.Count => shared[i],
                        "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
                        _ => value,
                    });
                }

                rows.Add(cells);
            }

            return rows;
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("The file isn't a readable Excel (.xlsx) workbook.");
        }
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = Load(zip.GetEntry("xl/workbook.xml") ?? throw new ArgumentException("The file isn't an Excel workbook."));
        var relId = (string?)workbook.Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Rel + "id");
        if (relId is not null && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } relsEntry)
        {
            var target = (string?)Load(relsEntry).Descendants(PackageRel + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == relId)?.Attribute("Target");
            if (target is not null)
            {
                return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            }
        }

        return "xl/worksheets/sheet1.xml";
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return -1;
        }

        var index = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter))
        {
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return index - 1;
    }
}
