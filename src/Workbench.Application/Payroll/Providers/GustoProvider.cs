using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Workbench.Contracts.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>
/// Gusto, through its public REST API (docs.gusto.com, API version <see cref="ApiVersion"/>) with the customer's own
/// Gusto developer application: OAuth 2.0 client id and secret plus a refresh token from the authorization they granted.
/// Gusto rotates refresh tokens, so each new one is saved (encrypted) as soon as it's issued. The pay group's company
/// code is the Gusto company UUID; a profile's provider employee id is the Gusto employee UUID.
/// <para>
/// Contract used (see <see cref="GustoFormats"/> for the file formats):
/// <list type="bullet">
/// <item><c>POST /oauth/token</c> (grant_type=refresh_token) → access_token, refresh_token, expires_in.</item>
/// <item><c>GET /v1/companies/{company}</c> (connection test).</item>
/// <item>Hours: <c>GET /v1/companies/{company}/payrolls?processing_statuses=unprocessed&amp;payroll_types=regular&amp;start_date&amp;end_date</c>
/// → the payroll whose <c>pay_period</c> matches; <c>PUT .../payrolls/{payroll}/prepare</c> → each <c>employee_compensations[]</c>
/// with its <c>version</c>, <c>hourly_compensations[]</c> (name, job_uuid) and <c>paid_time_off[]</c> (name); then
/// <c>PUT .../payrolls/{payroll}</c> with <c>employee_compensations[]</c> (employee_uuid, version, hourly_compensations[name,
/// hours, job_uuid], paid_time_off[name, hours]). Hours are strings with three decimals. The payroll stays unprocessed:
/// the customer reviews and submits it in Gusto.</item>
/// <item>Results: <c>GET .../payrolls?processing_statuses=processed&amp;start_date&amp;end_date</c>, then
/// <c>GET .../payrolls/{payroll}?include=taxes,benefits,deductions</c> → <c>check_date</c> and per employee
/// <c>gross_pay</c>, <c>net_pay</c>, <c>taxes[]</c> (amount, employer), <c>benefits[]</c> (employee_deduction,
/// company_contribution), <c>deductions[]</c> (amount).</item>
/// <item>Employees: <c>POST /v1/companies/{company}/employees</c>, <c>POST /v1/employees/{employee}/jobs</c> (hire_date) whose
/// first compensation is set with <c>PUT /v1/compensations/{compensation}</c> (version, rate, payment_unit Hour|Year,
/// flsa_status); updates <c>GET</c>/<c>PUT /v1/employees/{employee}</c> (version) and <c>POST /v1/jobs/{job}/compensations</c>
/// for a changed rate; <c>POST /v1/employees/{employee}/terminations</c> (effective_date).</item>
/// </list>
/// </para>
/// </summary>
public sealed class GustoPayrollProvider(PayrollConnections connections, IPayrollHttpHandlerFactory handlers, TimeProvider clock)
    : ConnectedPayrollProvider(connections, handlers)
{
    public const string ProviderKey = "gusto";
    public const string ApiVersion = "2026-06-15";

    public override string Key => ProviderKey;

    public override string Name => "Gusto";

    public override IReadOnlyList<EarningCodeMapping> DefaultEarningCodes { get; } =
    [
        new(EarningCodes.Regular, "Regular Hours"),
        new(EarningCodes.Overtime, "Overtime"),
        new("LV.PTO", "Vacation Hours"),
        new("LV.HOL", "Holiday Hours"),
        new("LV.SICK", "Sick Hours"),
    ];

    public override PayrollConnectionSpec ConnectionSpec { get; } = new(
    [
        new("clientId", "Client ID", false, Help: "From your application in the Gusto developer portal."),
        new("clientSecret", "Client secret", true),
        new("refreshToken", "Refresh token", true, Help: "Issued when your Gusto admin authorized the application. Workbench keeps the rotated token up to date."),
    ],
        "Create an application in the Gusto developer portal, have a Gusto admin authorize it for your company, and paste the client ID, secret and refresh token here. "
        + "Use the Gusto company UUID as each pay group's company code, and each employee's Gusto UUID as their provider employee id.",
        "https://docs.gusto.com/app-integrations/docs/introduction",
        ["production", "sandbox"]);

    public override PayrollExportFile ExportHours(PayrollHoursExport export) =>
        new($"gusto-hours-{export.PayGroupCode}-{export.PeriodEnd:yyyyMMdd}.csv".ToLowerInvariant(), "text/csv", GustoFormats.WriteHours(export));

    public override ParsedPayrollRegister ParseRegister(string content) => GustoFormats.ParseRegister(content);

    public override async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var me = await api.GetAsync("v1/me", cancellationToken);
        var companies = PayrollApi.Items(me?["roles"]?["payroll_admin"]?["companies"]).Select(c => PayrollApi.Text(c["name"])).OfType<string>().ToList();
        return companies.Count == 0 ? "Signed in to Gusto." : $"Signed in to Gusto: {string.Join(", ", companies)}.";
    }

    public override async Task<PayrollHoursSubmission> SendHoursAsync(PayrollHoursExport export, CancellationToken cancellationToken)
    {
        RequireLines(export, Name);
        var api = await SignInAsync(cancellationToken);
        var company = Uri.EscapeDataString(export.CompanyCode);
        var payroll = await FindPayrollAsync(api, company, "unprocessed", export.PeriodStart, export.PeriodEnd, cancellationToken)
            ?? throw new PayrollProviderApiException($"Gusto has no unprocessed regular payroll for {export.PeriodStart:MMM d}–{export.PeriodEnd:MMM d, yyyy}. Check the pay schedule in Gusto.");
        var payrollId = Uri.EscapeDataString(PayrollApi.Text(payroll["payroll_uuid"] ?? payroll["uuid"])!);
        var prepared = await api.PutAsync($"v1/companies/{company}/payrolls/{payrollId}/prepare", new JsonObject(), cancellationToken);
        var compensations = PayrollApi.Items(prepared?["employee_compensations"]).ToDictionary(c => PayrollApi.Text(c["employee_uuid"]) ?? "", StringComparer.OrdinalIgnoreCase);
        var body = GustoFormats.PayrollUpdate(export, compensations, out var problems);
        if (problems.Count > 0)
        {
            throw new PayrollProviderApiException(string.Join(" ", problems));
        }

        await api.PutAsync($"v1/companies/{company}/payrolls/{payrollId}", body, cancellationToken);
        var employees = export.Lines.Select(l => l.ProviderEmployeeId).Distinct().Count();
        return new PayrollHoursSubmission(employees, export.Lines.Count,
            $"Hours for {employees} employee(s) are on Gusto's payroll for {export.PeriodStart:MMM d}–{export.PeriodEnd:MMM d}. Review and submit it in Gusto.");
    }

    public override async Task<ParsedPayrollRegister> FetchRegisterAsync(PayrollRegisterQuery query, CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var company = Uri.EscapeDataString(query.CompanyCode);
        var payroll = await FindPayrollAsync(api, company, "processed", query.PeriodStart, query.PeriodEnd, cancellationToken)
            ?? throw new PayrollProviderApiException($"Gusto has no processed payroll for {query.PeriodStart:MMM d}–{query.PeriodEnd:MMM d, yyyy} yet.");
        var payrollId = Uri.EscapeDataString(PayrollApi.Text(payroll["payroll_uuid"] ?? payroll["uuid"])!);
        var detail = await api.GetAsync($"v1/companies/{company}/payrolls/{payrollId}?include=taxes,benefits,deductions", cancellationToken)
            ?? throw new PayrollProviderApiException("Gusto returned an empty payroll.");
        return GustoFormats.ReadPayroll(detail);
    }

    public override async Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(
        string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var company = Uri.EscapeDataString(companyCode);
        return await EachAsync(changes, async (e, action) =>
        {
            switch (action)
            {
                case PayrollSyncAction.Create:
                    var created = await api.PostAsync($"v1/companies/{company}/employees", new { first_name = e.FirstName, last_name = e.LastName, email = e.Email }, cancellationToken);
                    var uuid = PayrollApi.Text(created?["uuid"]) ?? throw new PayrollProviderApiException("Gusto didn't return the new employee's id.");
                    var job = await api.PostAsync($"v1/employees/{uuid}/jobs", new { title = "Employee", hire_date = e.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken);
                    if (PayrollApi.Items(job?["compensations"]).FirstOrDefault() is { } comp && GustoFormats.Rate(e) is { } rate)
                    {
                        await api.PutAsync($"v1/compensations/{PayrollApi.Text(comp["uuid"])}",
                            new { version = PayrollApi.Text(comp["version"]), rate = rate.Rate, payment_unit = rate.Unit, flsa_status = e.IsExempt ? "Exempt" : "Nonexempt" }, cancellationToken);
                    }

                    return new PayrollSyncOutcome(e.EmployeeId, action, true, uuid, $"Created as Gusto employee {uuid}.");
                case PayrollSyncAction.Update:
                    var id = Uri.EscapeDataString(e.ProviderEmployeeId!);
                    var current = await api.GetAsync($"v1/employees/{id}", cancellationToken);
                    await api.PutAsync($"v1/employees/{id}", new { version = PayrollApi.Text(current?["version"]), first_name = e.FirstName, last_name = e.LastName, email = e.Email }, cancellationToken);
                    var primary = PayrollApi.Items(current?["jobs"]).FirstOrDefault(j => j["primary"]?.GetValue<bool>() == true) ?? PayrollApi.Items(current?["jobs"]).FirstOrDefault();
                    if (primary is not null && GustoFormats.Rate(e) is { } newRate && GustoFormats.RateChanged(primary, newRate))
                    {
                        await api.PostAsync($"v1/jobs/{PayrollApi.Text(primary["uuid"])}/compensations",
                            new
                            {
                                rate = newRate.Rate,
                                payment_unit = newRate.Unit,
                                flsa_status = e.IsExempt ? "Exempt" : "Nonexempt",
                                effective_date = DateOnly.FromDateTime(clock.GetLocalNow().DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            }, cancellationToken);
                    }

                    return new PayrollSyncOutcome(e.EmployeeId, action, true, e.ProviderEmployeeId, "Updated.");
                default:
                    await api.PostAsync($"v1/employees/{Uri.EscapeDataString(e.ProviderEmployeeId!)}/terminations",
                        new { effective_date = e.TerminationDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken);
                    return new PayrollSyncOutcome(e.EmployeeId, action, true, e.ProviderEmployeeId, $"Terminated effective {e.TerminationDate:yyyy-MM-dd}.");
            }
        });
    }

    public static string BaseUrl(PayrollCredentials c) => c.IsSandbox ? "https://api.gusto-demo.com" : "https://api.gusto.com";

    private async Task<PayrollApi> SignInAsync(CancellationToken ct)
    {
        var credentials = await CredentialsAsync(ct);
        var api = Client(BaseUrl(credentials));
        var token = await api.TokenAsync("oauth/token", new Dictionary<string, string>
        {
            ["client_id"] = credentials["clientId"],
            ["client_secret"] = credentials["clientSecret"],
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials["refreshToken"],
        }, null, ct);

        // Gusto issues a new refresh token with every access token and retires the old one.
        if (PayrollApi.Text(token["refresh_token"]) is { Length: > 0 } rotated && rotated != credentials["refreshToken"])
        {
            await Connections.UpdateValueAsync(Key, "refreshToken", rotated, ct);
        }

        api.Headers["X-Gusto-API-Version"] = ApiVersion;
        return api;
    }

    private static async Task<JsonNode?> FindPayrollAsync(PayrollApi api, string company, string status, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var list = await api.GetAsync($"v1/companies/{company}/payrolls?processing_statuses={status}&payroll_types=regular"
            + $"&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}", ct);
        return PayrollApi.Items(list).FirstOrDefault(p => PayrollApi.Date(p["pay_period"]?["start_date"]) == start && PayrollApi.Date(p["pay_period"]?["end_date"]) == end);
    }
}

/// <summary>
/// Gusto's file formats. <b>Hours import</b> (Gusto's "Import hours from a CSV" template, one row per employee):
/// <c>Employee UUID, First Name, Last Name, Regular Hours, Overtime Hours</c> then one column per other earning or time-off
/// name in the mapping (e.g. <c>Vacation Hours</c>). <b>Payroll results</b>: Gusto's Payroll Journal report as CSV
/// (<c>Employee UUID</c> or <c>Employee ID</c>, <c>Check Date</c>, <c>Gross Earnings</c>, <c>Employee Taxes</c>,
/// <c>Employer Taxes</c>, <c>Employee Deductions</c>, <c>Employer Contributions</c>, <c>Net Pay</c>).
/// </summary>
public static class GustoFormats
{
    public static readonly RegisterColumns RegisterColumns = new(
        ["Employee UUID", "Employee ID", "Employee Id", "Employee Number"],
        ["Employee", "Employee Name", "Name"],
        ["Check Date", "Pay Date"],
        ["Gross Earnings", "Gross Pay", "Gross"],
        ["Employee Taxes", "Total Employee Taxes"],
        ["Employer Taxes", "Total Employer Taxes"],
        ["Employee Deductions", "Deductions", "Employee Benefit Deductions"],
        ["Employer Contributions", "Employer Benefits", "Company Contributions"],
        ["Net Pay", "Net"]);

    public static string WriteHours(PayrollHoursExport export)
    {
        var lines = export.Lines.Where(l => l.Hours != 0).ToList();
        var regular = lines.Where(l => l.Category == EarningCodes.Regular).Select(l => l.EarningCode).FirstOrDefault() ?? "Regular Hours";
        var overtime = lines.Where(l => l.Category == EarningCodes.Overtime).Select(l => l.EarningCode).FirstOrDefault() ?? "Overtime";
        var others = lines.Where(l => l.Category is not (EarningCodes.Regular or EarningCodes.Overtime)).Select(l => l.EarningCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        PayrollFiles.AppendRow(sb, ["Employee UUID", "First Name", "Last Name", "Regular Hours", "Overtime Hours", .. others]);
        foreach (var employee in lines.GroupBy(l => l.ProviderEmployeeId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var (first, last) = SplitName(employee.First().EmployeeName);
            decimal Sum(string code, string? category) => employee.Where(l => category is null ? l.EarningCode == code && l.Category is not (EarningCodes.Regular or EarningCodes.Overtime) : l.Category == category).Sum(l => l.Hours);
            PayrollFiles.AppendRow(sb,
            [
                employee.Key, first, last, PayrollFiles.Hours(Sum(regular, EarningCodes.Regular)), PayrollFiles.Hours(Sum(overtime, EarningCodes.Overtime)),
                .. others.Select(o => PayrollFiles.Hours(Sum(o, null))),
            ]);
        }

        return sb.ToString();
    }

    public static ParsedPayrollRegister ParseRegister(string content) =>
        PayrollFiles.ParseRegister(content, RegisterColumns, "Gusto", "Export the Payroll Journal report from Gusto as CSV with the Employee UUID column, or import the results through the API.");

    /// <summary>
    /// The body for Gusto's update-payroll call. Regular and overtime hours go on the employee's hourly compensations
    /// (job from the prepared payroll); leave goes in <c>paid_time_off</c> by policy name.
    /// </summary>
    public static JsonObject PayrollUpdate(PayrollHoursExport export, IReadOnlyDictionary<string, JsonNode> prepared, out List<string> problems)
    {
        problems = [];
        var compensations = new JsonArray();
        foreach (var employee in export.Lines.Where(l => l.Hours != 0).GroupBy(l => l.ProviderEmployeeId, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!prepared.TryGetValue(employee.Key, out var comp))
            {
                problems.Add($"{employee.First().EmployeeName} ({employee.Key}) isn't on Gusto's payroll for the period.");
                continue;
            }

            var hourlyNames = PayrollApi.Items(comp["hourly_compensations"]).ToList();
            var timeOffNames = PayrollApi.Items(comp["paid_time_off"]).Select(p => PayrollApi.Text(p["name"])).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hourly = new JsonArray();
            var timeOff = new JsonArray();
            foreach (var line in employee.GroupBy(l => (l.Category, l.EarningCode)))
            {
                var hours = line.Sum(l => l.Hours).ToString("0.000", CultureInfo.InvariantCulture);
                var match = hourlyNames.FirstOrDefault(h => string.Equals(PayrollApi.Text(h["name"]), line.Key.EarningCode, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    hourly.Add(new JsonObject { ["name"] = PayrollApi.Text(match["name"]), ["hours"] = hours, ["job_uuid"] = PayrollApi.Text(match["job_uuid"]) });
                }
                else if (timeOffNames.Contains(line.Key.EarningCode))
                {
                    timeOff.Add(new JsonObject { ["name"] = line.Key.EarningCode, ["hours"] = hours });
                }
                else
                {
                    problems.Add($"{employee.First().EmployeeName}: Gusto has no \"{line.Key.EarningCode}\" earning or time off policy. Fix the earning code mapping.");
                }
            }

            var row = new JsonObject { ["employee_uuid"] = employee.Key, ["version"] = PayrollApi.Text(comp["version"]) };
            if (hourly.Count > 0)
            {
                row["hourly_compensations"] = hourly;
            }

            if (timeOff.Count > 0)
            {
                row["paid_time_off"] = timeOff;
            }

            compensations.Add(row);
        }

        return new JsonObject { ["employee_compensations"] = compensations };
    }

    /// <summary>A processed payroll as register rows: taxes split by the <c>employer</c> flag, benefits into the employee and company shares.</summary>
    public static ParsedPayrollRegister ReadPayroll(JsonNode payroll)
    {
        var checkDate = PayrollApi.Date(payroll["check_date"]) ?? throw new PayrollProviderApiException("Gusto's payroll has no check date.");
        var rows = new List<PayrollRegisterRow>();
        var warnings = new List<string>();
        foreach (var c in PayrollApi.Items(payroll["employee_compensations"]))
        {
            if (c["excluded"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var taxes = PayrollApi.Items(c["taxes"]).ToList();
            var benefits = PayrollApi.Items(c["benefits"]).ToList();
            var gross = PayrollFiles.Round(PayrollApi.Decimal(c["gross_pay"]));
            var eeTaxes = PayrollFiles.Round(taxes.Where(t => t["employer"]?.GetValue<bool>() != true).Sum(t => PayrollApi.Decimal(t["amount"])));
            var erTaxes = PayrollFiles.Round(taxes.Where(t => t["employer"]?.GetValue<bool>() == true).Sum(t => PayrollApi.Decimal(t["amount"])));
            var deductions = PayrollFiles.Round(benefits.Sum(b => PayrollApi.Decimal(b["employee_deduction"])) + PayrollApi.Items(c["deductions"]).Sum(d => PayrollApi.Decimal(d["amount"])));
            var erBenefits = PayrollFiles.Round(benefits.Sum(b => PayrollApi.Decimal(b["company_contribution"])));
            var net = PayrollFiles.Round(PayrollApi.Decimal(c["net_pay"]));
            var name = $"{PayrollApi.Text(c["first_name"])} {PayrollApi.Text(c["last_name"])}".Trim();
            var reimbursed = PayrollFiles.Round(PayrollApi.Items(c["reimbursements"]).Sum(r => PayrollApi.Decimal(r["amount"])));
            if (reimbursed != 0)
            {
                warnings.Add($"{(name.Length > 0 ? name : PayrollApi.Text(c["employee_uuid"]))}: {reimbursed:N2} of reimbursements aren't part of gross pay and aren't posted from payroll.");
            }

            rows.Add(new PayrollRegisterRow(PayrollApi.Text(c["employee_uuid"]) ?? "", name, checkDate, gross, eeTaxes, erTaxes, deductions, erBenefits, net));
        }

        if (rows.Count == 0)
        {
            throw new PayrollProviderApiException("Gusto's payroll has no employee checks.");
        }

        return PayrollFiles.Merge(rows, warnings);
    }

    public static (string Rate, string Unit)? Rate(PayrollEmployeeRecord e) => e.PayType == "Salaried"
        ? e.AnnualSalary is { } s ? (s.ToString("0.00", CultureInfo.InvariantCulture), "Year") : null
        : e.HourlyRate is { } h ? (h.ToString("0.00", CultureInfo.InvariantCulture), "Hour") : null;

    public static bool RateChanged(JsonNode job, (string Rate, string Unit) rate)
    {
        var current = PayrollApi.Items(job["compensations"]).FirstOrDefault(c => PayrollApi.Text(c["uuid"]) == PayrollApi.Text(job["current_compensation_uuid"]))
            ?? PayrollApi.Items(job["compensations"]).LastOrDefault();
        return current is null || PayrollApi.Decimal(current["rate"]) != decimal.Parse(rate.Rate, CultureInfo.InvariantCulture)
            || !string.Equals(PayrollApi.Text(current["payment_unit"]), rate.Unit, StringComparison.OrdinalIgnoreCase);
    }

    private static (string First, string Last) SplitName(string name)
    {
        var comma = name.IndexOf(',');
        if (comma > 0)
        {
            return (name[(comma + 1)..].Trim(), name[..comma].Trim());
        }

        var space = name.LastIndexOf(' ');
        return space > 0 ? (name[..space].Trim(), name[(space + 1)..].Trim()) : (name, "");
    }
}
