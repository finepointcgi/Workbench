using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Workbench.Contracts.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>
/// ADP Workforce Now, through ADP's APIs (developers.adp.com) with the customer's own API credentials: an OAuth 2.0
/// client id and secret plus the client certificate ADP requires for mutual TLS (both from the customer's ADP
/// Marketplace "API Central" project). The pay group's company code is the ADP payroll group (company) code; a
/// profile's provider employee id is the ADP file number, which the API maps to the worker's associate OID.
/// <para>
/// Contract used: <c>POST https://accounts.adp.com/auth/oauth/v2/token</c> (client_credentials, basic auth, client
/// certificate); <c>GET /hr/v2/workers?$top=&amp;$skip=</c> → <c>workers[]</c> (associateOID, person.legalName,
/// workAssignments[payrollFileNumber, payrollGroupCode.codeValue, itemID]); hours with
/// <c>POST /events/payroll/v1/pay-data-input.add</c> (events[].data.transform.payDataInput.payeePayInputs[associateOID,
/// payrollProfilePayInputs[payrollGroupCode, payrollFileNumber, payInputs[earningInputs[earningCode.codeValue,
/// numberOfHours]]]]), in batches of 100 workers; results with <c>GET /payroll/v1/workers/{aoid}/pay-statements?numberoflastpaydates=</c>
/// and <c>GET /payroll/v1/workers/{aoid}/pay-statements/{id}</c> (payDate, grossPayAmount, netPayAmount,
/// statutoryDeductions[], deductions[]); employees with the <c>worker.hire</c>, <c>worker.legal-name.change</c>,
/// <c>worker.business-communication.email.change</c>, <c>worker.work-assignment.modify</c> (base remuneration) and
/// <c>worker.work-assignment.terminate</c> events under <c>/events/hr/v1/</c>. ADP's pay statements carry the employee
/// side only: import ADP's Payroll Register report when the employer taxes and benefits need posting.
/// </para>
/// </summary>
public sealed class AdpPayrollProvider(PayrollConnections connections, IPayrollHttpHandlerFactory handlers)
    : ConnectedPayrollProvider(connections, handlers)
{
    public const string ProviderKey = "adp";
    public const int BatchSize = 100;

    public override string Key => ProviderKey;

    public override string Name => "ADP Workforce Now";

    public override IReadOnlyList<EarningCodeMapping> DefaultEarningCodes { get; } =
    [
        new(EarningCodes.Regular, "REG"),
        new(EarningCodes.Overtime, "OT"),
        new("LV.PTO", "V"),
        new("LV.HOL", "H"),
        new("LV.SICK", "S"),
    ];

    public override PayrollConnectionSpec ConnectionSpec { get; } = new(
    [
        new("clientId", "Client ID", false, Help: "From your project in ADP API Central (ADP Marketplace)."),
        new("clientSecret", "Client secret", true),
        new("certificate", "Client certificate (PEM)", true, Multiline: true, Placeholder: "-----BEGIN CERTIFICATE-----", Help: "The signed certificate ADP issued for your project."),
        new("privateKey", "Certificate private key (PEM)", true, Multiline: true, Placeholder: "-----BEGIN PRIVATE KEY-----", Help: "The key you generated the certificate request with. Stored encrypted."),
    ],
        "In ADP API Central, create a project with the Workers, Pay Data Input and Pay Statements APIs, upload a certificate signing request and paste the "
        + "client ID, secret, signed certificate and private key here. Use the ADP company (payroll group) code as each pay group's company code, and each "
        + "employee's ADP file number as their provider employee id.",
        "https://developers.adp.com/",
        ["production"]);

    public override PayrollExportFile ExportHours(PayrollHoursExport export) =>
        new($"adp-epi-{export.PayGroupCode}-{export.PeriodEnd:yyyyMMdd}.csv".ToLowerInvariant(), "text/csv", AdpFormats.WriteHours(export));

    public override ParsedPayrollRegister ParseRegister(string content) => AdpFormats.ParseRegister(content);

    public override async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var page = await api.GetAsync("hr/v2/workers?$top=1", cancellationToken);
        return PayrollApi.Items(page?["workers"]).Any() ? "Signed in to ADP Workforce Now and read the worker list." : "Signed in to ADP Workforce Now (no workers visible to this project).";
    }

    public override async Task<PayrollHoursSubmission> SendHoursAsync(PayrollHoursExport export, CancellationToken cancellationToken)
    {
        RequireLines(export, Name);
        var api = await SignInAsync(cancellationToken);
        var workers = await WorkersAsync(api, cancellationToken);
        var payees = AdpFormats.PayeeInputs(export, workers, out var problems);
        if (problems.Count > 0)
        {
            throw new PayrollProviderApiException(string.Join(" ", problems));
        }

        foreach (var batch in payees.Chunk(BatchSize))
        {
            await api.PostAsync("events/payroll/v1/pay-data-input.add", AdpFormats.PayDataEvent(batch), cancellationToken);
        }

        return new PayrollHoursSubmission(payees.Count, export.Lines.Count,
            $"Hours for {payees.Count} worker(s) are in ADP's pay data batch for {export.PeriodStart:MMM d}–{export.PeriodEnd:MMM d}. Review and process the payroll in ADP.");
    }

    public override async Task<ParsedPayrollRegister> FetchRegisterAsync(PayrollRegisterQuery query, CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var workers = await WorkersAsync(api, cancellationToken);
        var rows = new List<PayrollRegisterRow>();
        var warnings = new List<string> { "ADP pay statements don't include employer taxes or benefits: import ADP's Payroll Register report instead to post them." };
        foreach (var fileNumber in query.ProviderEmployeeIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!workers.TryGetValue(fileNumber, out var worker))
            {
                warnings.Add($"ADP has no worker with file number {fileNumber}.");
                continue;
            }

            var aoid = Uri.EscapeDataString(worker.AssociateOid);
            var list = await api.GetAsync($"payroll/v1/workers/{aoid}/pay-statements?numberoflastpaydates=5", cancellationToken);
            foreach (var statement in PayrollApi.Items(list?["payStatements"]).Where(s => PayrollApi.Date(s["payDate"]) == query.CheckDate))
            {
                var detail = statement["payDetailUri"]?["href"] is { } href && PayrollApi.Text(href) is { } link
                    ? (await api.GetAsync(link.TrimStart('/'), cancellationToken))?["payStatement"] ?? statement
                    : statement;
                rows.Add(AdpFormats.ReadPayStatement(detail, worker));
            }
        }

        if (rows.Count == 0)
        {
            throw new PayrollProviderApiException($"ADP has no pay statements dated {query.CheckDate:MMM d, yyyy} for this pay group's workers yet.");
        }

        return PayrollFiles.Merge(rows, warnings);
    }

    public override async Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(
        string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken)
    {
        var api = await SignInAsync(cancellationToken);
        var workers = await WorkersAsync(api, cancellationToken);
        return await EachAsync(changes, async (e, action) =>
        {
            if (action == PayrollSyncAction.Create)
            {
                var hired = await api.PostAsync("events/hr/v1/worker.hire", AdpFormats.HireEvent(companyCode, e), cancellationToken);
                var output = PayrollApi.Items(hired?["events"]).FirstOrDefault()?["data"]?["output"]?["worker"];
                var fileNumber = PayrollApi.Text(PayrollApi.Items(output?["workAssignments"]).FirstOrDefault()?["payrollFileNumber"])
                    ?? PayrollApi.Text(output?["workAssignment"]?["payrollFileNumber"])
                    ?? throw new PayrollProviderApiException("ADP didn't return the new worker's file number.");
                return new PayrollSyncOutcome(e.EmployeeId, action, true, fileNumber, $"Hired in ADP as file number {fileNumber}.");
            }

            var worker = workers.TryGetValue(e.ProviderEmployeeId!, out var w) ? w
                : throw new PayrollProviderApiException($"ADP has no worker with file number {e.ProviderEmployeeId}.");
            if (action == PayrollSyncAction.Update)
            {
                await api.PostAsync("events/hr/v1/worker.legal-name.change", AdpFormats.Event(AdpFormats.WorkerContext(worker),
                    new JsonObject { ["worker"] = new JsonObject { ["person"] = new JsonObject { ["legalName"] = AdpFormats.LegalName(e) } } }), cancellationToken);
                await api.PostAsync("events/hr/v1/worker.business-communication.email.change", AdpFormats.Event(AdpFormats.WorkerContext(worker),
                    new JsonObject { ["businessCommunication"] = new JsonObject { ["email"] = new JsonObject { ["emailUri"] = e.Email } } }), cancellationToken);
                if (AdpFormats.BaseRemuneration(e) is { } pay)
                {
                    await api.PostAsync("events/hr/v1/worker.work-assignment.modify", AdpFormats.Event(AdpFormats.WorkerContext(worker, assignment: true),
                        new JsonObject { ["workAssignment"] = new JsonObject { ["baseRemuneration"] = pay } }), cancellationToken);
                }

                return new PayrollSyncOutcome(e.EmployeeId, action, true, e.ProviderEmployeeId, "Updated.");
            }

            await api.PostAsync("events/hr/v1/worker.work-assignment.terminate", AdpFormats.Event(AdpFormats.WorkerContext(worker, assignment: true),
                new JsonObject
                {
                    ["workAssignment"] = new JsonObject
                    {
                        ["terminationDate"] = e.TerminationDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["assignmentStatus"] = new JsonObject { ["reasonCode"] = new JsonObject { ["codeValue"] = "O", ["shortName"] = "Other" } },
                    },
                }), cancellationToken);
            return new PayrollSyncOutcome(e.EmployeeId, action, true, e.ProviderEmployeeId, $"Terminated effective {e.TerminationDate:yyyy-MM-dd}.");
        });
    }

    public const string TokenUrl = "https://accounts.adp.com/auth/oauth/v2/token";
    public const string ApiUrl = "https://api.adp.com";

    private async Task<PayrollApi> SignInAsync(CancellationToken ct)
    {
        var credentials = await CredentialsAsync(ct);
        var api = Client(ApiUrl, Certificate(credentials["certificate"], credentials["privateKey"]));
        await api.TokenAsync(TokenUrl, new Dictionary<string, string> { ["grant_type"] = "client_credentials" }, (credentials["clientId"], credentials["clientSecret"]), ct);
        return api;
    }

    /// <summary>The client certificate for mutual TLS, from the PEM certificate and key on the connect screen.</summary>
    public static X509Certificate2 Certificate(string certificatePem, string keyPem)
    {
        try
        {
            using var pem = X509Certificate2.CreateFromPem(certificatePem, keyPem);

            // Re-import so the key is usable by TLS on every platform (keys from PEM are ephemeral on some).
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (CryptographicException)
        {
            throw new PayrollProviderApiException("The ADP certificate or private key isn't valid PEM, or they don't match. Paste them again on the connect screen.");
        }
    }

    private static async Task<Dictionary<string, AdpWorker>> WorkersAsync(PayrollApi api, CancellationToken ct)
    {
        var all = new Dictionary<string, AdpWorker>(StringComparer.OrdinalIgnoreCase);
        for (var skip = 0; ; skip += BatchSize)
        {
            var page = await api.GetAsync($"hr/v2/workers?$top={BatchSize}&$skip={skip}", ct);
            var workers = AdpFormats.Workers(page);
            foreach (var (fileNumber, worker) in workers)
            {
                all.TryAdd(fileNumber, worker);
            }

            if (PayrollApi.Items(page?["workers"]).Count() < BatchSize)
            {
                return all;
            }
        }
    }
}

/// <summary>An ADP worker: the associate OID the API uses, the file number people see, and the work assignment's item id.</summary>
public sealed record AdpWorker(string AssociateOid, string FileNumber, string? PayrollGroupCode, string? AssignmentId, string Name);

/// <summary>
/// ADP Workforce Now file formats. <b>Hours import</b>: ADP's pay data import (EPI) CSV, one row per worker: <c>Co Code,
/// Batch ID, File #, Reg Hours, O/T Hours</c> then <c>Hours 3 Code, Hours 3 Amount</c> pairs for leave and any other
/// earning code (a worker with more codes gets more rows). <b>Payroll results</b>: ADP's Payroll Register (or
/// "Earnings and Deductions") report as CSV: <c>File Number</c>, <c>Pay Date</c>, <c>Gross Pay</c>, <c>Total Employee
/// Taxes</c>, <c>Total Employer Taxes</c>, <c>Total Deductions</c>, <c>Employer Benefits</c>, <c>Net Pay</c>.
/// </summary>
public static class AdpFormats
{
    public const int ExtraHourColumns = 4;

    public static readonly RegisterColumns RegisterColumns = new(
        ["File Number", "File #", "File No", "Associate ID", "Employee ID"],
        ["Employee Name", "Name", "Associate Name"],
        ["Pay Date", "Check Date"],
        ["Gross Pay", "Gross", "Total Gross"],
        ["Total Employee Taxes", "Employee Taxes", "EE Taxes", "Taxes"],
        ["Total Employer Taxes", "Employer Taxes", "ER Taxes"],
        ["Total Deductions", "Deductions", "Voluntary Deductions"],
        ["Employer Benefits", "Employer Contributions", "Memo Employer Benefits"],
        ["Net Pay", "Net", "Take Home"]);

    public static string WriteHours(PayrollHoursExport export)
    {
        var header = new List<string> { "Co Code", "Batch ID", "File #", "Reg Hours", "O/T Hours" };
        for (var i = 3; i < 3 + ExtraHourColumns; i++)
        {
            header.Add($"Hours {i} Code");
            header.Add($"Hours {i} Amount");
        }

        var sb = new StringBuilder();
        PayrollFiles.AppendRow(sb, header);
        var batch = $"WB{export.PeriodEnd:MMdd}";
        foreach (var employee in export.Lines.Where(l => l.Hours != 0).GroupBy(l => l.ProviderEmployeeId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var regular = employee.Where(l => l.Category == EarningCodes.Regular).Sum(l => l.Hours);
            var overtime = employee.Where(l => l.Category == EarningCodes.Overtime).Sum(l => l.Hours);
            var others = employee.Where(l => l.Category is not (EarningCodes.Regular or EarningCodes.Overtime))
                .GroupBy(l => l.EarningCode, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (Code: g.Key, Hours: g.Sum(l => l.Hours))).ToList();
            var first = true;
            IEnumerable<(string Code, decimal Hours)[]> chunks = others.Count == 0 ? [[]] : others.Chunk(ExtraHourColumns);
            foreach (var chunk in chunks)
            {
                var row = new List<string>
                {
                    export.CompanyCode, batch, employee.Key,
                    first && regular != 0 ? PayrollFiles.Hours(regular) : "",
                    first && overtime != 0 ? PayrollFiles.Hours(overtime) : "",
                };
                for (var i = 0; i < ExtraHourColumns; i++)
                {
                    row.Add(i < chunk.Length ? chunk[i].Code : "");
                    row.Add(i < chunk.Length ? PayrollFiles.Hours(chunk[i].Hours) : "");
                }

                PayrollFiles.AppendRow(sb, row);
                first = false;
            }
        }

        return sb.ToString();
    }

    public static ParsedPayrollRegister ParseRegister(string content) =>
        PayrollFiles.ParseRegister(content, RegisterColumns, "ADP", "Export ADP's Payroll Register report as CSV (with the File Number column).");

    public static Dictionary<string, AdpWorker> Workers(JsonNode? page)
    {
        var result = new Dictionary<string, AdpWorker>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in PayrollApi.Items(page?["workers"]))
        {
            var aoid = PayrollApi.Text(w["associateOID"]);
            var name = w["person"]?["legalName"];
            var display = PayrollApi.Text(name?["formattedName"]) ?? $"{PayrollApi.Text(name?["givenName"])} {PayrollApi.Text(name?["familyName1"])}".Trim();
            foreach (var a in PayrollApi.Items(w["workAssignments"]))
            {
                if (aoid is not null && PayrollApi.Text(a["payrollFileNumber"]) is { Length: > 0 } file)
                {
                    result.TryAdd(file, new AdpWorker(aoid, file, PayrollApi.Text(a["payrollGroupCode"]?["codeValue"]), PayrollApi.Text(a["itemID"]), display));
                }
            }
        }

        return result;
    }

    public static List<JsonObject> PayeeInputs(PayrollHoursExport export, IReadOnlyDictionary<string, AdpWorker> workers, out List<string> problems)
    {
        problems = [];
        var payees = new List<JsonObject>();
        foreach (var employee in export.Lines.Where(l => l.Hours != 0).GroupBy(l => l.ProviderEmployeeId, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!workers.TryGetValue(employee.Key, out var worker))
            {
                problems.Add($"ADP has no worker with file number {employee.Key} ({employee.First().EmployeeName}).");
                continue;
            }

            var earnings = new JsonArray();
            foreach (var code in employee.GroupBy(l => l.EarningCode, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                earnings.Add(new JsonObject
                {
                    ["earningCode"] = new JsonObject { ["codeValue"] = code.Key },
                    ["numberOfHours"] = PayrollFiles.Round(code.Sum(l => l.Hours)),
                });
            }

            payees.Add(new JsonObject
            {
                ["associateOID"] = worker.AssociateOid,
                ["payrollProfilePayInputs"] = new JsonArray(new JsonObject
                {
                    ["payrollGroupCode"] = new JsonObject { ["codeValue"] = worker.PayrollGroupCode ?? export.CompanyCode },
                    ["payrollFileNumber"] = worker.FileNumber,
                    ["payInputs"] = new JsonArray(new JsonObject { ["earningInputs"] = earnings }),
                }),
            });
        }

        return payees;
    }

    public static JsonObject PayDataEvent(IEnumerable<JsonObject> payees) => new()
    {
        ["events"] = new JsonArray(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["eventContext"] = new JsonObject(),
                ["transform"] = new JsonObject { ["payDataInput"] = new JsonObject { ["payeePayInputs"] = new JsonArray(payees.Select(p => (JsonNode)p.DeepClone()).ToArray()) } },
            },
        }),
    };

    /// <summary>A pay statement as a register row. Employer taxes and benefits aren't on pay statements (0).</summary>
    public static PayrollRegisterRow ReadPayStatement(JsonNode statement, AdpWorker worker)
    {
        static decimal Amount(JsonNode? amount) => PayrollApi.Decimal(amount?["amountValue"]);
        var date = PayrollApi.Date(statement["payDate"]) ?? throw new PayrollProviderApiException($"ADP pay statement for {worker.FileNumber} has no pay date.");
        var taxes = PayrollApi.Items(statement["statutoryDeductions"]).Sum(d => Amount(d["deductionAmount"]));
        var deductions = PayrollApi.Items(statement["deductions"]).Sum(d => Amount(d["deductionAmount"]));

        // ADP reports deductions as negative amounts on some statements: use their size.
        return new PayrollRegisterRow(worker.FileNumber, worker.Name, date, PayrollFiles.Round(Amount(statement["grossPayAmount"])),
            PayrollFiles.Round(Math.Abs(taxes)), 0, PayrollFiles.Round(Math.Abs(deductions)), 0, PayrollFiles.Round(Amount(statement["netPayAmount"])));
    }

    public static JsonObject Event(JsonObject context, JsonObject transform) => new()
    {
        ["events"] = new JsonArray(new JsonObject { ["data"] = new JsonObject { ["eventContext"] = context, ["transform"] = transform } }),
    };

    public static JsonObject WorkerContext(AdpWorker worker, bool assignment = false)
    {
        var w = new JsonObject { ["associateOID"] = worker.AssociateOid };
        if (assignment)
        {
            w["workAssignment"] = new JsonObject { ["itemID"] = worker.AssignmentId };
        }

        return new JsonObject { ["worker"] = w };
    }

    public static JsonObject LegalName(PayrollEmployeeRecord e) => new() { ["givenName"] = e.FirstName, ["familyName1"] = e.LastName };

    public static JsonObject? BaseRemuneration(PayrollEmployeeRecord e) => e.PayType == "Salaried"
        ? e.AnnualSalary is { } s ? new JsonObject { ["annualRateAmount"] = new JsonObject { ["amountValue"] = s, ["currencyCode"] = "USD" } } : null
        : e.HourlyRate is { } h ? new JsonObject { ["hourlyRateAmount"] = new JsonObject { ["amountValue"] = h, ["currencyCode"] = "USD" } } : null;

    public static JsonObject HireEvent(string companyCode, PayrollEmployeeRecord e)
    {
        var assignment = new JsonObject
        {
            ["payrollGroupCode"] = new JsonObject { ["codeValue"] = companyCode },
            ["hireDate"] = e.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["workerTypeCode"] = new JsonObject { ["codeValue"] = "F" },
            ["wageLawCoverage"] = new JsonObject { ["coverageCode"] = new JsonObject { ["codeValue"] = e.IsExempt ? "E" : "N" } },
        };
        if (BaseRemuneration(e) is { } pay)
        {
            assignment["baseRemuneration"] = pay;
        }

        if (e.CostCenter is not null)
        {
            assignment["homeOrganizationalUnits"] = new JsonArray(new JsonObject { ["nameCode"] = new JsonObject { ["codeValue"] = e.CostCenter }, ["typeCode"] = new JsonObject { ["codeValue"] = "Department" } });
        }

        return Event(new JsonObject(), new JsonObject
        {
            ["worker"] = new JsonObject
            {
                ["person"] = new JsonObject { ["legalName"] = LegalName(e) },
                ["businessCommunication"] = new JsonObject { ["emails"] = new JsonArray(new JsonObject { ["emailUri"] = e.Email }) },
                ["workerDates"] = new JsonObject { ["originalHireDate"] = e.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                ["workAssignment"] = assignment,
            },
        });
    }
}
