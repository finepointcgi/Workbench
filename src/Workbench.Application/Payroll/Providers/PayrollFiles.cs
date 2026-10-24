using System.Globalization;
using System.Text;

namespace Workbench.Application.Payroll;

/// <summary>Register column aliases for one provider's payroll register export (matched ignoring case, spaces and punctuation).</summary>
public sealed record RegisterColumns(
    IReadOnlyList<string> EmployeeId,
    IReadOnlyList<string> EmployeeName,
    IReadOnlyList<string> CheckDate,
    IReadOnlyList<string> Gross,
    IReadOnlyList<string> EmployeeTaxes,
    IReadOnlyList<string> EmployerTaxes,
    IReadOnlyList<string> Deductions,
    IReadOnlyList<string> EmployerBenefits,
    IReadOnlyList<string> Net);

/// <summary>CSV helpers shared by the ADP, Paychex and Gusto file formats (Paylocity has its own configurable layouts).</summary>
public static class PayrollFiles
{
    public const string UsDate = "MM/dd/yyyy";

    public static string Hours(decimal hours) => hours.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date, string format = UsDate) => date.ToString(format, CultureInfo.InvariantCulture);

    public static void AppendRow(StringBuilder sb, IEnumerable<string> cells) => sb.AppendJoin(',', cells.Select(Escape)).Append("\r\n");

    /// <summary>Neutralizes spreadsheet formulas and quotes anything with separators.</summary>
    public static string Escape(string value)
    {
        var v = value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    /// <summary>
    /// Reads a register CSV: a header row, then one row per check (rows for the same employee and check date are added
    /// together; rows without an employee id, like totals, are skipped). Required: id, check date, gross, net.
    /// </summary>
    public static ParsedPayrollRegister ParseRegister(string content, RegisterColumns columns, string providerName, string exportHint)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("The register file is empty.");
        }

        var rows = PaylocityFormats.ReadCsv(content).Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count < 2)
        {
            throw new ArgumentException("The register needs a header row and at least one employee row.");
        }

        var header = rows[0].Select(Key).ToList();
        int Col(IReadOnlyList<string> aliases) => aliases.Select(a => header.IndexOf(Key(a))).FirstOrDefault(i => i >= 0, -1);
        var (id, name, date, gross, eeTax, erTax, ded, erBen, net) = (Col(columns.EmployeeId), Col(columns.EmployeeName), Col(columns.CheckDate), Col(columns.Gross),
            Col(columns.EmployeeTaxes), Col(columns.EmployerTaxes), Col(columns.Deductions), Col(columns.EmployerBenefits), Col(columns.Net));
        var missing = new[] { (id, columns.EmployeeId[0]), (date, columns.CheckDate[0]), (gross, columns.Gross[0]), (net, columns.Net[0]) }
            .Where(c => c.Item1 < 0).Select(c => c.Item2).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"The register is missing the column(s) {string.Join(", ", missing)}. {exportHint}");
        }

        var parsed = new List<PayrollRegisterRow>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            string Cell(int c) => c >= 0 && c < row.Count ? row[c].Trim() : "";
            var employeeId = Cell(id);
            if (employeeId.Length == 0)
            {
                continue;
            }

            var line = i + 1;
            if (!TryDate(Cell(date), out var checkDate))
            {
                throw new ArgumentException($"Line {line}: \"{Cell(date)}\" isn't a check date (use MM/dd/yyyy or yyyy-MM-dd).");
            }

            decimal Amount(int c, bool required = false)
            {
                var text = Cell(c);
                if (text.Length == 0 && !required)
                {
                    return 0;
                }

                return TryMoney(text, out var value) ? value : throw new ArgumentException($"Line {line}: \"{text}\" isn't an amount.");
            }

            parsed.Add(new PayrollRegisterRow(employeeId, Cell(name), checkDate, Amount(gross, true), Amount(eeTax), Amount(erTax), Amount(ded), Amount(erBen), Amount(net, true)));
        }

        if (parsed.Count == 0)
        {
            throw new ArgumentException("The register has no employee rows.");
        }

        return Merge(parsed);
    }

    /// <summary>Adds together rows for the same employee and check date (a register can split one check over several rows).</summary>
    public static ParsedPayrollRegister Merge(IReadOnlyList<PayrollRegisterRow> parsed, IEnumerable<string>? warnings = null)
    {
        var all = (warnings ?? []).ToList();
        var merged = parsed.GroupBy(r => (r.ProviderEmployeeId, r.CheckDate))
            .Select(g => g.Count() == 1 ? g.First() : new PayrollRegisterRow(g.Key.ProviderEmployeeId, g.Select(r => r.EmployeeName).FirstOrDefault(n => n.Length > 0) ?? "",
                g.Key.CheckDate, g.Sum(r => r.GrossWages), g.Sum(r => r.EmployeeTaxes), g.Sum(r => r.EmployerTaxes), g.Sum(r => r.Deductions), g.Sum(r => r.EmployerBenefits), g.Sum(r => r.NetPay)))
            .ToList();
        if (merged.Count < parsed.Count)
        {
            all.Add($"{parsed.Count - merged.Count} row(s) were combined with another row for the same employee and check date.");
        }

        return new ParsedPayrollRegister(merged, all);
    }

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Key(string header) => new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "MM/dd/yy", "M/d/yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryMoney(string text, out decimal value)
    {
        var t = text.Replace("$", "").Replace(",", "").Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative)
        {
            t = t[1..^1];
        }

        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            value = Round(negative ? -value : value);
            return true;
        }

        return false;
    }
}
