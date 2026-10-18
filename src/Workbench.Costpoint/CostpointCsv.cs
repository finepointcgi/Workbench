using System.Globalization;
using System.Text;

namespace Workbench.Costpoint;

/// <summary>Rows parsed from one CSV file, plus a message for each line that was skipped.</summary>
public sealed record CsvParseResult<T>(IReadOnlyList<T> Rows, IReadOnlyList<string> Errors);

/// <summary>
/// Parses the Costpoint CSV exports described in README.md. Headers are matched by name (case-insensitive,
/// any order, extra columns ignored). A row with a missing required value or an unparseable number, date
/// or period is skipped and reported; a missing required header fails the whole file.
/// </summary>
public static class CostpointCsv
{
    public static CsvParseResult<CpProjectRecord> ParseProjects(TextReader reader, string fileName = "projects.csv") =>
        Parse(reader, fileName, ["project_id", "name"], r => new CpProjectRecord(
            r.Required("project_id"),
            r.Required("name"),
            r.Optional("org_id"),
            r.Date("start_date"),
            r.Date("end_date"),
            r.Bool("active", defaultValue: true)));

    public static CsvParseResult<CpWbsRecord> ParseWbs(TextReader reader, string fileName = "wbs.csv") =>
        Parse(reader, fileName, ["project_id", "wbs_id", "name"], r => new CpWbsRecord(
            r.Required("project_id"),
            r.Required("wbs_id"),
            r.Optional("parent_wbs_id"),
            r.Required("name"),
            r.Int("level", defaultValue: 1),
            r.Bool("charge_allowed", defaultValue: true)));

    public static CsvParseResult<CpEmployeeRecord> ParseEmployees(TextReader reader, string fileName = "employees.csv") =>
        Parse(reader, fileName, ["employee_id", "name"], r => new CpEmployeeRecord(
            r.Required("employee_id"),
            r.Required("name"),
            r.Optional("email"),
            r.Optional("org_id"),
            r.Optional("labor_category_id"),
            r.Bool("active", defaultValue: true)));

    public static CsvParseResult<CpLaborCategoryRecord> ParseLaborCategories(TextReader reader, string fileName = "labor_categories.csv") =>
        Parse(reader, fileName, ["labor_category_id", "name"], r => new CpLaborCategoryRecord(
            r.Required("labor_category_id"),
            r.Required("name")));

    public static CsvParseResult<CpBudgetRecord> ParseBudgets(TextReader reader, string fileName = "budgets.csv") =>
        Parse(reader, fileName, ["project_id", "hours", "amount"], r => new CpBudgetRecord(
            r.Required("project_id"),
            r.Optional("wbs_id"),
            r.BudgetPeriod("fiscal_period"),
            r.Decimal("hours"),
            r.Decimal("amount")));

    public static CsvParseResult<CpActualRecord> ParseActuals(TextReader reader, string fileName = "actuals.csv") =>
        Parse(reader, fileName, ["project_id", "wbs_id", "fiscal_period", "hours", "cost"], r => new CpActualRecord(
            r.Required("project_id"),
            r.Required("wbs_id"),
            r.Period("fiscal_period"),
            r.Decimal("hours"),
            r.Decimal("cost")));

    /// <summary>True when <paramref name="value"/> is a valid <c>yyyy-MM</c> fiscal period.</summary>
    public static bool IsPeriod(string value) =>
        value.Length == 7 && DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static CsvParseResult<T> Parse<T>(TextReader reader, string fileName, string[] requiredHeaders, Func<Row, T> map)
    {
        var rows = new List<T>();
        var errors = new List<string>();
        using var records = ReadRecords(reader).GetEnumerator();
        if (!records.MoveNext())
        {
            throw new FormatException($"{fileName} is empty; expected a header row.");
        }

        var headerCount = records.Current.Fields.Count;
        var headers = records.Current.Fields
            .Select((name, index) => (name: name.Trim().TrimStart('﻿').ToLowerInvariant(), index))
            .GroupBy(h => h.name)
            .ToDictionary(g => g.Key, g => g.First().index);
        var missing = requiredHeaders.Where(h => !headers.ContainsKey(h)).ToList();
        if (missing.Count > 0)
        {
            throw new FormatException($"{fileName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        while (records.MoveNext())
        {
            var (line, fields) = records.Current;
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (fields.Count != headerCount)
            {
                errors.Add($"{fileName} line {line}: expected {headerCount} fields but found {fields.Count}.");
                continue;
            }

            try
            {
                rows.Add(map(new Row(headers, fields)));
            }
            catch (FormatException ex)
            {
                errors.Add($"{fileName} line {line}: {ex.Message}");
            }
        }

        return new CsvParseResult<T>(rows, errors);
    }

    /// <summary>RFC 4180 records: quoted fields may contain commas, doubled quotes and line breaks.</summary>
    public static IEnumerable<(int Line, IReadOnlyList<string> Fields)> ReadRecords(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        line++;
                    }

                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (recordLine, fields);
                    fields = [];
                    any = false;
                    recordLine = ++line;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (any)
        {
            fields.Add(field.ToString());
            yield return (recordLine, fields);
        }
    }

    private sealed class Row(Dictionary<string, int> headers, IReadOnlyList<string> fields)
    {
        public string? Optional(string column) =>
            headers.TryGetValue(column, out var index) && index < fields.Count && !string.IsNullOrWhiteSpace(fields[index])
                ? fields[index].Trim()
                : null;

        public string Required(string column) =>
            Optional(column) ?? throw new FormatException($"{column} is required.");

        public decimal Decimal(string column)
        {
            var value = Required(column);
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new FormatException($"{column} '{value}' is not a number.");
        }

        public int Int(string column, int defaultValue) =>
            Optional(column) is not { } value ? defaultValue
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result
            : throw new FormatException($"{column} '{value}' is not a whole number.");

        public bool Bool(string column, bool defaultValue) =>
            Optional(column)?.ToLowerInvariant() switch
            {
                null => defaultValue,
                "y" or "yes" or "true" or "1" => true,
                "n" or "no" or "false" or "0" => false,
                var value => throw new FormatException($"{column} '{value}' is not Y/N or true/false."),
            };

        public DateOnly? Date(string column) =>
            Optional(column) is not { } value ? null
            : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result
            : throw new FormatException($"{column} '{value}' is not a yyyy-MM-dd date.");

        public string Period(string column)
        {
            var value = Required(column);
            return IsPeriod(value) ? value : throw new FormatException($"{column} '{value}' is not a yyyy-MM period.");
        }

        /// <summary>Blank or <c>TOTAL</c> means a whole-life budget (null).</summary>
        public string? BudgetPeriod(string column) =>
            Optional(column) is not { } value || value.Equals("TOTAL", StringComparison.OrdinalIgnoreCase) ? null
            : IsPeriod(value) ? value
            : throw new FormatException($"{column} '{value}' is not a yyyy-MM period or TOTAL.");
    }
}
