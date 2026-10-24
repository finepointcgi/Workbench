using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Workbench.Contracts.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>
/// Paychex Flex, through the Paychex Flex API (developer.paychex.com) with the customer's own API client: OAuth 2.0
/// client credentials issued by Paychex for their company. The pay group's company code is the Paychex company display
/// id; a profile's provider employee id is the worker's Paychex employee id (the number on their Flex record).
/// <para>
/// Contract used: <c>POST /auth/oauth/v2/token</c> (client_credentials, form client_id/client_secret);
/// <c>GET /companies?displayid=</c> → <c>content[].companyId</c>; <c>GET /companies/{company}/workers</c> →
/// <c>content[]</c> (workerId, employeeId, name.givenName/familyName); <c>GET /companies/{company}/payperiods?status=</c>
/// (INITIAL for open, COMPLETED for processed) → payPeriodId, startDate, endDate, checkDate;
/// <c>GET /companies/{company}/paycomponents</c> → componentId, name; hours go on unprocessed checks with
/// <c>POST /companies/{company}/checks</c> (workerId, payPeriodId, earnings[componentId, payHours, lineDate]);
/// results come from <c>GET /companies/{company}/checks?payperiodid=</c> (workerId, checkDate, grossAmount, netAmount,
/// earnings[amount], taxes[amount, employerAmount? / isEmployerTax], deductions[amount, employerAmount]). Workers are
/// created with <c>POST /companies/{company}/workers</c> (in-progress until finished in Flex), changed with
/// <c>PATCH /workers/{worker}</c>, rates with <c>POST /workers/{worker}/compensation/payrates</c>, and terminated by
/// patching <c>currentStatus</c>. Paychex keeps payroll entry open: the customer reviews and submits it in Flex.
/// </para>
/// </summary>
public sealed class PaychexPayrollProvider(PayrollConnections connections, IPayrollHttpHandlerFactory handlers)
    : ConnectedPayrollProvider(connections, handlers)
{
    public const string ProviderKey = "paychex";

    public override string Key => ProviderKey;

    public override string Name => "Paychex Flex";

    public override IReadOnlyList<EarningCodeMapping> DefaultEarningCodes { get; } =
    [
        new(EarningCodes.Regular, "Hourly"),
        new(EarningCodes.Overtime, "Overtime"),
        new("LV.PTO", "PTO"),
        new("LV.HOL", "Holiday"),
        new("LV.SICK", "Sick"),
    ];

    public override PayrollConnectionSpec ConnectionSpec { get; } = new(
    [
        new("clientId", "API key (client ID)", false, Help: "Issued by Paychex for your company's Flex API access."),
        new("clientSecret", "API secret", true),
    ],
        "Ask your Paychex representative to enable the Paychex Flex API for your company and send you an API key and secret. "
        + "Use the Paychex company display id as each pay group's company code, and each worker's Paychex employee id as their provider employee id.",
        "https://developer.paychex.com/",
        ["production", "sandbox"]);

    public override PayrollExportFile ExportHours(PayrollHoursExport export) =>
        new($"paychex-hours-{export.PayGroupCode}-{export.PeriodEnd:yyyyMMdd}.csv".ToLowerInvariant(), "text/csv", PaychexFormats.WriteHours(export));

    public override ParsedPayrollRegister ParseRegister(string content) => PaychexFormats.ParseRegister(content);

    public override async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var companies = PayrollApi.Items((await api.GetAsync("companies", cancellationToken))?["content"])
            .Select(c => $"{PayrollApi.Text(c["legalName"])} ({PayrollApi.Text(c["displayId"])})").ToList();
        return companies.Count == 0 ? "Signed in to Paychex Flex, but the API client sees no companies." : $"Signed in to Paychex Flex: {string.Join(", ", companies)}.";
    }

    public override async Task<PayrollHoursSubmission> SendHoursAsync(PayrollHoursExport export, CancellationToken cancellationToken)
    {
        RequireLines(export, Name);
        var api = await SignInAsync(cancellationToken);
        var company = await CompanyIdAsync(api, export.CompanyCode, cancellationToken);
        var workers = await WorkersAsync(api, company, cancellationToken);
        var period = await PayPeriodAsync(api, company, "INITIAL", export.PeriodStart, export.PeriodEnd, cancellationToken)
            ?? throw new PayrollProviderApiException($"Paychex Flex has no open pay period for {export.PeriodStart:MMM d}–{export.PeriodEnd:MMM d, yyyy}.");
        var components = PayrollApi.Items((await api.GetAsync($"companies/{company}/paycomponents", cancellationToken))?["content"])
            .Where(c => PayrollApi.Text(c["name"]) is not null)
            .GroupBy(c => PayrollApi.Text(c["name"])!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => PayrollApi.Text(g.First()["componentId"])!, StringComparer.OrdinalIgnoreCase);
        var body = PaychexFormats.Checks(export, workers, PayrollApi.Text(period["payPeriodId"])!, components, out var problems);
        if (problems.Count > 0)
        {
            throw new PayrollProviderApiException(string.Join(" ", problems));
        }

        await api.PostAsync($"companies/{company}/checks", body, cancellationToken);
        return new PayrollHoursSubmission(body.Count, export.Lines.Count,
            $"Hours for {body.Count} worker(s) are on unprocessed checks in Paychex Flex for {export.PeriodStart:MMM d}–{export.PeriodEnd:MMM d}. Review and submit the payroll in Flex.");
    }

    public override async Task<ParsedPayrollRegister> FetchRegisterAsync(PayrollRegisterQuery query, CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var company = await CompanyIdAsync(api, query.CompanyCode, cancellationToken);
        var workers = await WorkersAsync(api, company, cancellationToken);
        var period = await PayPeriodAsync(api, company, "COMPLETED", query.PeriodStart, query.PeriodEnd, cancellationToken)
            ?? throw new PayrollProviderApiException($"Paychex Flex has no processed payroll for {query.PeriodStart:MMM d}–{query.PeriodEnd:MMM d, yyyy} yet.");
        var checks = await api.GetAsync($"companies/{company}/checks?payperiodid={Uri.EscapeDataString(PayrollApi.Text(period["payPeriodId"])!)}", cancellationToken);
        return PaychexFormats.ReadChecks(checks, workers);
    }

    public override async Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(
        string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var company = await CompanyIdAsync(api, companyCode, cancellationToken);
        var workers = await WorkersAsync(api, company, cancellationToken);
        return await EachAsync(changes, async (e, action) =>
        {
            if (action == PayrollSyncAction.Create)
            {
                var created = await api.PostAsync($"companies/{company}/workers", new JsonArray(PaychexFormats.NewWorker(e)), cancellationToken);
                var worker = PayrollApi.Items(created?["content"]).FirstOrDefault() ?? throw new PayrollProviderApiException("Paychex didn't return the new worker.");
                var workerId = PayrollApi.Text(worker["workerId"])!;
                if (PaychexFormats.PayRate(e) is { } rate)
                {
                    await api.PostAsync($"workers/{Uri.EscapeDataString(workerId)}/compensation/payrates", new JsonArray(rate), cancellationToken);
                }

                var employeeId = PayrollApi.Text(worker["employeeId"]) ?? workerId;
                return new PayrollSyncOutcome(e.EmployeeId, action, true, employeeId, $"Added to Paychex Flex as worker {employeeId} (finish onboarding in Flex).");
            }

            var id = workers.TryGetValue(e.ProviderEmployeeId!, out var w) ? w.WorkerId
                : throw new PayrollProviderApiException($"Paychex has no worker with employee id {e.ProviderEmployeeId}.");
            if (action == PayrollSyncAction.Update)
            {
                await api.PatchAsync($"workers/{Uri.EscapeDataString(id)}", new JsonObject
                {
                    ["name"] = new JsonObject { ["givenName"] = e.FirstName, ["familyName"] = e.LastName },
                    ["exemptionType"] = e.IsExempt ? "EXEMPT" : "NON_EXEMPT",
                }, cancellationToken);
                if (PaychexFormats.PayRate(e) is { } rate)
                {
                    await api.PostAsync($"workers/{Uri.EscapeDataString(id)}/compensation/payrates", new JsonArray(rate), cancellationToken);
                }

                return new PayrollSyncOutcome(e.EmployeeId, action, true, e.ProviderEmployeeId, "Updated.");
            }

            await api.PatchAsync($"workers/{Uri.EscapeDataString(id)}", new JsonObject
            {
                ["currentStatus"] = new JsonObject
                {
                    ["statusType"] = "TERMINATED",
                    ["statusReason"] = "Terminated in Workbench",
                    ["effectiveDate"] = e.TerminationDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
            }, cancellationToken);
            return new PayrollSyncOutcome(e.EmployeeId, action, true, e.ProviderEmployeeId, $"Terminated effective {e.TerminationDate:yyyy-MM-dd}.");
        });
    }

    public static string BaseUrl(PayrollCredentials c) => c.IsSandbox ? "https://sandbox.api.paychex.com" : "https://api.paychex.com";

    private async Task<PayrollApi> SignInAsync(CancellationToken ct)
    {
        var credentials = await CredentialsAsync(ct);
        var api = Client(BaseUrl(credentials));
        await api.TokenAsync("auth/oauth/v2/token", new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = credentials["clientId"],
            ["client_secret"] = credentials["clientSecret"],
        }, null, ct);
        return api;
    }

    private static async Task<string> CompanyIdAsync(PayrollApi api, string displayId, CancellationToken ct)
    {
        var found = PayrollApi.Items((await api.GetAsync($"companies?displayid={Uri.EscapeDataString(displayId)}", ct))?["content"]).FirstOrDefault();
        return Uri.EscapeDataString(PayrollApi.Text(found?["companyId"])
            ?? throw new PayrollProviderApiException($"Paychex has no company {displayId} for this API client. Check the pay group's company code."));
    }

    private static async Task<Dictionary<string, PaychexWorker>> WorkersAsync(PayrollApi api, string company, CancellationToken ct) =>
        PaychexFormats.Workers(await api.GetAsync($"companies/{company}/workers", ct));

    private static async Task<JsonNode?> PayPeriodAsync(PayrollApi api, string company, string status, DateOnly start, DateOnly end, CancellationToken ct) =>
        PayrollApi.Items((await api.GetAsync($"companies/{company}/payperiods?status={status}", ct))?["content"])
            .FirstOrDefault(p => PayrollApi.Date(p["startDate"]) == start && PayrollApi.Date(p["endDate"]) == end);
}

/// <summary>A Paychex worker: the API's workerId and the employee id people see in Flex.</summary>
public sealed record PaychexWorker(string WorkerId, string EmployeeId, string Name);

/// <summary>
/// Paychex Flex file formats. <b>Hours import</b> (Flex payroll entry import, one row per worker and pay component):
/// <c>Company ID, Worker ID, Worker Name, Pay Period Begin, Pay Period End, Check Date, Pay Component, Hours, Rate,
/// Amount, Org</c>; Rate and Amount are blank so Paychex pays at the rate on file. <b>Payroll results</b>: the Flex
/// "Payroll Journal" report as CSV (<c>Employee ID</c>/<c>Worker ID</c>, <c>Check Date</c>, <c>Gross</c>,
/// <c>Employee Taxes</c>, <c>Employer Taxes</c>, <c>Deductions</c>, <c>Employer Benefits</c>, <c>Net Pay</c>).
/// </summary>
public static class PaychexFormats
{
    public static readonly string[] HoursHeader =
        ["Company ID", "Worker ID", "Worker Name", "Pay Period Begin", "Pay Period End", "Check Date", "Pay Component", "Hours", "Rate", "Amount", "Org"];

    public static readonly RegisterColumns RegisterColumns = new(
        ["Employee ID", "Worker ID", "Employee Number", "Emp ID"],
        ["Employee Name", "Worker Name", "Name"],
        ["Check Date", "Pay Date"],
        ["Gross", "Gross Pay", "Gross Earnings", "Total Earnings"],
        ["Employee Taxes", "EE Taxes", "Total Employee Taxes", "Withholdings"],
        ["Employer Taxes", "ER Taxes", "Total Employer Taxes"],
        ["Deductions", "Total Deductions", "Employee Deductions"],
        ["Employer Benefits", "Employer Contributions", "ER Benefits"],
        ["Net Pay", "Net", "Net Check"]);

    public static string WriteHours(PayrollHoursExport export)
    {
        var sb = new StringBuilder();
        PayrollFiles.AppendRow(sb, HoursHeader);
        foreach (var line in export.Lines.Where(l => l.Hours != 0).OrderBy(l => l.ProviderEmployeeId, StringComparer.Ordinal).ThenBy(l => l.EarningCode, StringComparer.Ordinal))
        {
            PayrollFiles.AppendRow(sb,
            [
                export.CompanyCode, line.ProviderEmployeeId, line.EmployeeName, PayrollFiles.Date(export.PeriodStart), PayrollFiles.Date(export.PeriodEnd),
                PayrollFiles.Date(export.CheckDate), line.EarningCode, PayrollFiles.Hours(line.Hours), "", "", line.CostCenter ?? "",
            ]);
        }

        return sb.ToString();
    }

    public static ParsedPayrollRegister ParseRegister(string content) =>
        PayrollFiles.ParseRegister(content, RegisterColumns, "Paychex Flex", "Export the Payroll Journal from Paychex Flex as CSV, or import the results through the API.");

    public static Dictionary<string, PaychexWorker> Workers(JsonNode? response) =>
        PayrollApi.Items(response?["content"])
            .Select(w => new PaychexWorker(PayrollApi.Text(w["workerId"]) ?? "", PayrollApi.Text(w["employeeId"]) ?? PayrollApi.Text(w["workerId"]) ?? "",
                $"{PayrollApi.Text(w["name"]?["givenName"])} {PayrollApi.Text(w["name"]?["familyName"])}".Trim()))
            .Where(w => w.EmployeeId.Length > 0)
            .GroupBy(w => w.EmployeeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>One unprocessed check per worker, an earning per pay component (matched by name to the earning code).</summary>
    public static JsonArray Checks(PayrollHoursExport export, IReadOnlyDictionary<string, PaychexWorker> workers, string payPeriodId,
        IReadOnlyDictionary<string, string> components, out List<string> problems)
    {
        problems = [];
        var checks = new JsonArray();
        foreach (var employee in export.Lines.Where(l => l.Hours != 0).GroupBy(l => l.ProviderEmployeeId, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!workers.TryGetValue(employee.Key, out var worker))
            {
                problems.Add($"Paychex has no worker with employee id {employee.Key} ({employee.First().EmployeeName}).");
                continue;
            }

            var earnings = new JsonArray();
            foreach (var code in employee.GroupBy(l => l.EarningCode, StringComparer.OrdinalIgnoreCase))
            {
                if (!components.TryGetValue(code.Key, out var componentId))
                {
                    problems.Add($"Paychex has no pay component named \"{code.Key}\". Fix the earning code mapping.");
                    continue;
                }

                earnings.Add(new JsonObject
                {
                    ["componentId"] = componentId,
                    ["payHours"] = code.Sum(l => l.Hours).ToString("0.00", CultureInfo.InvariantCulture),
                    ["lineDate"] = export.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                });
            }

            checks.Add(new JsonObject { ["workerId"] = worker.WorkerId, ["payPeriodId"] = payPeriodId, ["earnings"] = earnings });
        }

        return checks;
    }

    /// <summary>Processed checks as register rows (taxes split by the employer flag; deductions' employer share as benefits).</summary>
    public static ParsedPayrollRegister ReadChecks(JsonNode? response, IReadOnlyDictionary<string, PaychexWorker> workers)
    {
        var byWorkerId = workers.Values.ToDictionary(w => w.WorkerId, StringComparer.OrdinalIgnoreCase);
        var rows = new List<PayrollRegisterRow>();
        foreach (var check in PayrollApi.Items(response?["content"]))
        {
            var workerId = PayrollApi.Text(check["workerId"]) ?? "";
            var worker = byWorkerId.GetValueOrDefault(workerId);
            var checkDate = PayrollApi.Date(check["checkDate"]) ?? throw new PayrollProviderApiException($"Paychex check for worker {workerId} has no check date.");
            var taxes = PayrollApi.Items(check["taxes"]).ToList();
            bool IsEmployer(JsonNode t) => t["isEmployerTax"]?.GetValue<bool>() == true || string.Equals(PayrollApi.Text(t["payerType"]), "EMPLOYER", StringComparison.OrdinalIgnoreCase);
            var gross = check["grossAmount"] is { } g ? PayrollApi.Decimal(g) : PayrollApi.Items(check["earnings"]).Sum(e => PayrollApi.Decimal(e["amount"]));
            var deductions = PayrollApi.Items(check["deductions"]).ToList();
            rows.Add(new PayrollRegisterRow(
                worker?.EmployeeId ?? workerId,
                worker?.Name ?? "",
                checkDate,
                PayrollFiles.Round(gross),
                PayrollFiles.Round(taxes.Where(t => !IsEmployer(t)).Sum(t => PayrollApi.Decimal(t["amount"]))),
                PayrollFiles.Round(taxes.Where(IsEmployer).Sum(t => PayrollApi.Decimal(t["amount"]))),
                PayrollFiles.Round(deductions.Sum(d => PayrollApi.Decimal(d["amount"]))),
                PayrollFiles.Round(deductions.Sum(d => PayrollApi.Decimal(d["employerAmount"]))),
                PayrollFiles.Round(PayrollApi.Decimal(check["netAmount"]))));
        }

        if (rows.Count == 0)
        {
            throw new PayrollProviderApiException("Paychex returned no checks for the pay period.");
        }

        return PayrollFiles.Merge(rows);
    }

    public static JsonObject NewWorker(PayrollEmployeeRecord e) => new()
    {
        ["workerType"] = "EMPLOYEE",
        ["employmentType"] = "FULL_TIME",
        ["exemptionType"] = e.IsExempt ? "EXEMPT" : "NON_EXEMPT",
        ["hireDate"] = e.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["employeeId"] = e.EmployeeNumber,
        ["name"] = new JsonObject { ["givenName"] = e.FirstName, ["familyName"] = e.LastName },
        ["communications"] = new JsonArray(new JsonObject { ["type"] = "EMAIL", ["usageType"] = "BUSINESS", ["uri"] = e.Email }),
        ["organization"] = e.CostCenter is null ? null : new JsonObject { ["number"] = e.CostCenter },
    };

    public static JsonObject? PayRate(PayrollEmployeeRecord e) => e.PayType == "Salaried"
        ? e.AnnualSalary is { } s ? new JsonObject { ["rateType"] = "SALARY", ["amount"] = s.ToString("0.00", CultureInfo.InvariantCulture), ["payFrequency"] = "ANNUAL", ["rateNumber"] = "1" } : null
        : e.HourlyRate is { } h ? new JsonObject { ["rateType"] = "HOURLY", ["amount"] = h.ToString("0.00", CultureInfo.InvariantCulture), ["rateNumber"] = "1" } : null;
}
