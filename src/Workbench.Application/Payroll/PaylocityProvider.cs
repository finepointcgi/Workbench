using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Workbench.Application.Payroll;

/// <summary>
/// Paylocity. Hours go out as a payroll import CSV (<see cref="PaylocityFormats.HoursHeader"/>), the register comes back
/// from Paylocity's payroll register export (<see cref="PaylocityFormats.ParseRegister"/>), and employees sync through
/// Paylocity's public REST API when <c>Payroll:Paylocity</c> credentials are configured.
/// </summary>
public sealed class PaylocityPayrollProvider(IPaylocityApiClient api, IOptions<PaylocityOptions> options) : IPayrollProvider
{
    public const string ProviderKey = "paylocity";

    public string Key => ProviderKey;

    public string Name => "Paylocity";

    public bool IsSupported => true;

    public bool SupportsEmployeeSync => true;

    public bool ApiConfigured => options.Value.IsConfigured;

    public PayrollExportFile ExportHours(PayrollHoursExport export) =>
        new($"paylocity-hours-{export.PayGroupCode}-{export.PeriodEnd:yyyyMMdd}.csv".ToLowerInvariant(), "text/csv", PaylocityFormats.WriteHours(export));

    public ParsedPayrollRegister ParseRegister(string content) => PaylocityFormats.ParseRegister(content);

    public async Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(
        string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken)
    {
        if (!ApiConfigured)
        {
            throw new ArgumentException("The Paylocity API isn't configured. Set Payroll:Paylocity:ClientId and ClientSecret (from a vault or environment variables).");
        }

        var outcomes = new List<PayrollSyncOutcome>();
        foreach (var (employee, action) in changes)
        {
            try
            {
                switch (action)
                {
                    case PayrollSyncAction.Create:
                        var id = await api.CreateEmployeeAsync(companyCode, PaylocityEmployee.From(employee), cancellationToken);
                        outcomes.Add(new(employee.EmployeeId, action, true, id, $"Created as Paylocity employee {id}."));
                        break;
                    case PayrollSyncAction.Update:
                        await api.UpdateEmployeeAsync(companyCode, employee.ProviderEmployeeId!, PaylocityEmployee.From(employee), cancellationToken);
                        outcomes.Add(new(employee.EmployeeId, action, true, employee.ProviderEmployeeId, "Updated."));
                        break;
                    case PayrollSyncAction.Terminate:
                        await api.TerminateEmployeeAsync(companyCode, employee.ProviderEmployeeId!, employee.TerminationDate!.Value, cancellationToken);
                        outcomes.Add(new(employee.EmployeeId, action, true, employee.ProviderEmployeeId, $"Terminated effective {employee.TerminationDate:yyyy-MM-dd}."));
                        break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or PaylocityApiException or TaskCanceledException)
            {
                outcomes.Add(new(employee.EmployeeId, action, false, employee.ProviderEmployeeId, ex.Message));
            }
        }

        return outcomes;
    }
}

/// <summary>
/// The Paylocity file formats Workbench assumes. Check them against the customer's own Paylocity setup: Paylocity
/// imports are configured per company, so column names and order can differ.
/// <para>
/// <b>Hours import</b> (Paylocity "Payroll import" / Web Pay entry import, one row per employee and earning):
/// <c>Company Code, Employee Id, Employee Name, Pay Period Begin, Pay Period End, Check Date, Det, Det Code, Hours, Rate,
/// Amount, Cost Center 1</c>. <c>Det</c> is always <c>E</c> (earning); <c>Det Code</c> is the earning code from the
/// provider settings (REG, OT, PTO, HOL by default); <c>Rate</c> and <c>Amount</c> are left blank so Paylocity pays at
/// the rate on file (Workbench sends hours, never pay). Dates are <c>MM/dd/yyyy</c>; hours have two decimals;
/// <c>Cost Center 1</c> is the employee's org unit code.
/// </para>
/// <para>
/// <b>Payroll register</b> (Paylocity payroll register report exported as CSV, one row per check): a header row, then
/// rows with the columns below; matching ignores case, spaces and punctuation and accepts the aliases listed.
/// Required: <c>Employee Id</c> (Emp Id, Employee Number), <c>Check Date</c> (Pay Date), <c>Gross Pay</c> (Gross, Gross
/// Wages), <c>Net Pay</c> (Net, Net Check). Optional (default 0): <c>Employee Name</c> (Name), <c>EE Taxes</c> (Employee
/// Taxes, Total EE Taxes, Withholding), <c>ER Taxes</c> (Employer Taxes, Total ER Taxes), <c>Deductions</c> (Total
/// Deductions, EE Deductions), <c>ER Benefits</c> (Employer Benefits, ER Contributions, Employer Contributions).
/// Several rows for one employee and check date are added together; a trailing totals row (no employee id) is skipped.
/// </para>
/// </summary>
public static class PaylocityFormats
{
    public static readonly string[] HoursHeader =
        ["Company Code", "Employee Id", "Employee Name", "Pay Period Begin", "Pay Period End", "Check Date", "Det", "Det Code", "Hours", "Rate", "Amount", "Cost Center 1"];

    private const string DateFormat = "MM/dd/yyyy";

    public static string WriteHours(PayrollHoursExport export)
    {
        var sb = new StringBuilder();
        AppendRow(sb, HoursHeader);
        foreach (var line in export.Lines.Where(l => l.Hours != 0).OrderBy(l => l.ProviderEmployeeId, StringComparer.Ordinal).ThenBy(l => l.EarningCode, StringComparer.Ordinal))
        {
            AppendRow(sb,
            [
                export.CompanyCode,
                line.ProviderEmployeeId,
                line.EmployeeName,
                export.PeriodStart.ToString(DateFormat, CultureInfo.InvariantCulture),
                export.PeriodEnd.ToString(DateFormat, CultureInfo.InvariantCulture),
                export.CheckDate.ToString(DateFormat, CultureInfo.InvariantCulture),
                "E",
                line.EarningCode,
                line.Hours.ToString("0.00", CultureInfo.InvariantCulture),
                "",
                "",
                line.CostCenter ?? "",
            ]);
        }

        return sb.ToString();
    }

    private static readonly Dictionary<string, string[]> RegisterColumns = new()
    {
        ["id"] = ["employeeid", "empid", "employeenumber", "employee", "id"],
        ["name"] = ["employeename", "name", "empname"],
        ["checkdate"] = ["checkdate", "paydate"],
        ["gross"] = ["grosspay", "gross", "grosswages", "totalgross"],
        ["eetax"] = ["eetaxes", "employeetaxes", "totaleetaxes", "withholding", "eetax", "taxes"],
        ["ertax"] = ["ertaxes", "employertaxes", "totalertaxes", "ertax"],
        ["ded"] = ["deductions", "totaldeductions", "eedeductions"],
        ["erben"] = ["erbenefits", "employerbenefits", "ercontributions", "employercontributions", "benefits"],
        ["net"] = ["netpay", "net", "netcheck", "netamount"],
    };

    public static ParsedPayrollRegister ParseRegister(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("The register file is empty.");
        }

        var rows = ReadCsv(content).Where(r => r.Any(c => c.Length > 0)).ToList();
        if (rows.Count < 2)
        {
            throw new ArgumentException("The register needs a header row and at least one employee row.");
        }

        var header = rows[0].Select(Key).ToList();
        int Col(string name) => RegisterColumns[name].Select(alias => header.IndexOf(alias)).FirstOrDefault(i => i >= 0, -1);
        var cols = RegisterColumns.Keys.ToDictionary(k => k, Col);
        var missing = new[] { ("id", "Employee Id"), ("checkdate", "Check Date"), ("gross", "Gross Pay"), ("net", "Net Pay") }.Where(c => cols[c.Item1] < 0).Select(c => c.Item2).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"The register is missing the column(s) {string.Join(", ", missing)}. Export the Paylocity payroll register as CSV with a header row.");
        }

        var warnings = new List<string>();
        var parsed = new List<PayrollRegisterRow>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            string Cell(string name) => cols[name] is var c && c >= 0 && c < row.Count ? row[c].Trim() : "";
            var id = Cell("id");
            if (id.Length == 0)
            {
                // Totals and blank rows at the end of a report.
                continue;
            }

            var line = i + 1;
            if (!TryDate(Cell("checkdate"), out var checkDate))
            {
                throw new ArgumentException($"Line {line}: \"{Cell("checkdate")}\" isn't a check date (use MM/dd/yyyy or yyyy-MM-dd).");
            }

            decimal Amount(string name, bool required = false)
            {
                var text = Cell(name);
                if (text.Length == 0 && !required)
                {
                    return 0;
                }

                return TryMoney(text, out var value) ? value : throw new ArgumentException($"Line {line}: \"{text}\" isn't an amount.");
            }

            parsed.Add(new PayrollRegisterRow(id, Cell("name"), checkDate, Amount("gross", true), Amount("eetax"), Amount("ertax"), Amount("ded"), Amount("erben"), Amount("net", true)));
        }

        if (parsed.Count == 0)
        {
            throw new ArgumentException("The register has no employee rows.");
        }

        // A register can split one check over several rows (earnings detail); add them together.
        var merged = parsed.GroupBy(r => (r.ProviderEmployeeId, r.CheckDate))
            .Select(g => g.Count() == 1 ? g.First() : new PayrollRegisterRow(g.Key.ProviderEmployeeId, g.Select(r => r.EmployeeName).FirstOrDefault(n => n.Length > 0) ?? "",
                g.Key.CheckDate, g.Sum(r => r.GrossWages), g.Sum(r => r.EmployeeTaxes), g.Sum(r => r.EmployerTaxes), g.Sum(r => r.Deductions), g.Sum(r => r.EmployerBenefits), g.Sum(r => r.NetPay)))
            .ToList();
        if (merged.Count < parsed.Count)
        {
            warnings.Add($"{parsed.Count - merged.Count} row(s) were combined with another row for the same employee and check date.");
        }

        return new ParsedPayrollRegister(merged, warnings);
    }

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
            value = Math.Round(negative ? -value : value, 2, MidpointRounding.AwayFromZero);
            return true;
        }

        return false;
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.AppendJoin(',', cells.Select(Escape)).Append("\r\n");
    }

    private static string Escape(string value)
    {
        // Neutralize spreadsheet formulas and quote anything with separators.
        var v = value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    /// <summary>RFC 4180 CSV: quoted fields, doubled quotes, CRLF or LF line ends.</summary>
    internal static IEnumerable<List<string>> ReadCsv(string content)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
