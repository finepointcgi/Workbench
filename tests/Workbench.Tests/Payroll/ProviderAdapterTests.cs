using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Payroll;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;
using static System.Net.Http.HttpMethod;

namespace Workbench.Tests.Payroll;

/// <summary>
/// The ADP Workforce Now, Paychex Flex and Gusto adapters against recorded API responses: hours files, register files,
/// sending hours to the open payroll, reading processed payroll back, employee sync, and the customer's own credentials
/// (encrypted, rotated refresh tokens saved).
/// </summary>
public sealed class ProviderAdapterTests
{
    private static readonly DateOnly Start = new(2026, 10, 5);
    private static readonly DateOnly End = new(2026, 10, 18);
    private static readonly DateOnly Check = new(2026, 10, 23);

    private static PayrollHoursExport Hours(string company, params PayrollHoursLine[] lines) => new(company, "BW", Start, End, Check, lines);

    private static readonly PayrollEmployeeRecord NewHire = new(Guid.NewGuid(), null, "E3", "Val", "Chen", "val@example.com", new DateOnly(2024, 1, 1), null, false, "Hourly", null, 30m, "OPS");

    // ---- Gusto ----

    private static async Task<(GustoPayrollProvider Provider, RecordedProvider Api, PayrollConnections Store)> GustoAsync(bool connected = true)
    {
        var api = new RecordedProvider("gusto").On(Post, "/oauth/token", "token.json");
        var (store, _) = await TestConnections.CreateAsync("gusto", connected
            ? new Dictionary<string, string> { ["clientId"] = "gusto-client", ["clientSecret"] = "gusto-secret", ["refreshToken"] = "gusto-refresh-1" }
            : null);
        return (new GustoPayrollProvider(store, api, TimeProvider.System), api, store);
    }

    [Fact]
    public async Task Gusto_signs_in_with_the_refresh_token_and_saves_the_rotated_one()
    {
        var (gusto, api, store) = await GustoAsync();
        api.On(Get, "/v1/me", "me.json");
        Assert.Equal("Signed in to Gusto: Acme Federal LLC.", await gusto.TestConnectionAsync(default));

        var token = Assert.Single(api.To(Post, "/oauth/token"));
        Assert.Contains("grant_type=refresh_token", token.Body);
        Assert.Contains("refresh_token=gusto-refresh-1", token.Body);
        Assert.Equal("Bearer gusto-access-1", api.To(Get, "/v1/me").Single().Authorization);
        Assert.Equal(GustoPayrollProvider.ApiVersion, api.To(Get, "/v1/me").Single().Headers["X-Gusto-API-Version"]);
        Assert.Equal("gusto-refresh-2", (await store.GetAsync("gusto", default))!["refreshToken"]);
        Assert.True(await gusto.IsConnectedAsync(default));
    }

    [Fact]
    public async Task Gusto_puts_hours_on_the_matching_unprocessed_payroll()
    {
        var (gusto, api, _) = await GustoAsync();
        api.On(Get, "/v1/companies/c0a1/payrolls?processing_statuses=unprocessed", "payrolls-unprocessed.json")
            .On(Put, "/v1/companies/c0a1/payrolls/p-oct/prepare", "prepare.json")
            .On(Put, "/v1/companies/c0a1/payrolls/p-oct", "prepare.json");

        var result = await gusto.SendHoursAsync(Hours("c0a1",
            new PayrollHoursLine("e-alex", "Alex Rivera", "Regular Hours", 40m, "OPS", "REG"),
            new PayrollHoursLine("e-alex", "Alex Rivera", "Overtime", 2.5m, "OPS", "OT"),
            new PayrollHoursLine("e-alex", "Alex Rivera", "Vacation Hours", 8m, "OPS", "LV.PTO"),
            new PayrollHoursLine("e-pat", "Pat Morgan", "Vacation Hours", 16m, "OPS", "LV.PTO")), default);
        Assert.Equal(2, result.Employees);
        Assert.Contains("Review and submit it in Gusto", result.Message);

        var list = api.To(Get, "/v1/companies/c0a1/payrolls").Single();
        Assert.Contains("start_date=2026-10-05&end_date=2026-10-18", list.PathAndQuery);
        var update = api.Requests.Last(r => r.Method == Put && r.PathAndQuery == "/v1/companies/c0a1/payrolls/p-oct").Json!;
        var alex = update["employee_compensations"]![0]!;
        Assert.Equal("e-alex", (string?)alex["employee_uuid"]);
        Assert.Equal("v-alex-1", (string?)alex["version"]);
        Assert.Equal("40.000", (string?)alex["hourly_compensations"]![0]!["hours"]);
        Assert.Equal("j-alex", (string?)alex["hourly_compensations"]![0]!["job_uuid"]);
        Assert.Equal("Overtime", (string?)alex["hourly_compensations"]![1]!["name"]);
        Assert.Equal("2.500", (string?)alex["hourly_compensations"]![1]!["hours"]);
        Assert.Equal("Vacation Hours", (string?)alex["paid_time_off"]![0]!["name"]);
        var pat = update["employee_compensations"]![1]!;
        Assert.Null(pat["hourly_compensations"]);
        Assert.Equal("16.000", (string?)pat["paid_time_off"]![0]!["hours"]);
    }

    [Fact]
    public async Task Gusto_refuses_unknown_earnings_and_people_before_changing_the_payroll()
    {
        var (gusto, api, _) = await GustoAsync();
        api.On(Get, "/v1/companies/c0a1/payrolls", "payrolls-unprocessed.json").On(Put, "/v1/companies/c0a1/payrolls/p-oct/prepare", "prepare.json");
        var ex = await Assert.ThrowsAsync<PayrollProviderApiException>(() => gusto.SendHoursAsync(Hours("c0a1",
            new PayrollHoursLine("e-alex", "Alex Rivera", "Bonus Hours", 1m, null, "REG"),
            new PayrollHoursLine("e-nobody", "No Body", "Regular Hours", 1m, null, "REG")), default));
        Assert.Contains("no \"Bonus Hours\" earning", ex.Message);
        Assert.Contains("isn't on Gusto's payroll", ex.Message);
        Assert.DoesNotContain(api.Requests, r => r.Method == Put && r.PathAndQuery == "/v1/companies/c0a1/payrolls/p-oct");

        // No payroll for the period at all.
        var other = Hours("c0a1", new PayrollHoursLine("e-alex", "Alex", "Regular Hours", 1m, null, "REG")) with { PeriodStart = new DateOnly(2026, 11, 2), PeriodEnd = new DateOnly(2026, 11, 15) };
        Assert.Contains("no unprocessed regular payroll", (await Assert.ThrowsAsync<PayrollProviderApiException>(() => gusto.SendHoursAsync(other, default))).Message);
    }

    [Fact]
    public async Task Gusto_processed_payroll_maps_to_register_rows()
    {
        var (gusto, api, _) = await GustoAsync();
        api.On(Get, "/v1/companies/c0a1/payrolls?processing_statuses=processed", "payrolls-processed.json")
            .On(Get, "/v1/companies/c0a1/payrolls/p-oct?include=", "payroll-processed.json");
        var register = await gusto.FetchRegisterAsync(new PayrollRegisterQuery("c0a1", Start, End, Check, ["e-alex", "e-pat"]), default);

        Assert.Equal(2, register.Rows.Count); // the excluded employee is skipped
        var alex = register.Rows.Single(r => r.ProviderEmployeeId == "e-alex");
        Assert.Equal(new PayrollRegisterRow("e-alex", "Alex Rivera", Check, 400m, 60m, 30.6m, 20m, 50m, 320m), alex);
        Assert.Equal(alex.GrossWages - alex.EmployeeTaxes - alex.Deductions, alex.NetPay);
        Assert.Equal(new PayrollRegisterRow("e-pat", "Pat Morgan", Check, 4000m, 800m, 306m, 150m, 400m, 3050m), register.Rows.Single(r => r.ProviderEmployeeId == "e-pat"));
        Assert.Contains(register.Warnings, w => w.Contains("reimbursements"));
    }

    [Fact]
    public async Task Gusto_employee_sync_creates_with_a_job_and_rate_updates_by_version_and_terminates()
    {
        var (gusto, api, _) = await GustoAsync();
        api.On(Post, "/v1/companies/c0a1/employees", "employee-created.json")
            .On(Post, "/v1/employees/e-new/jobs", "job-created.json")
            .On(Put, "/v1/compensations/comp-new", "job-created.json")
            .On(Get, "/v1/employees/e-alex", "employee.json")
            .On(Put, "/v1/employees/e-alex", "employee.json")
            .On(Post, "/v1/jobs/j-alex/compensations", "job-created.json")
            .On(Post, "/v1/employees/e-old/terminations", "employee.json");
        var alex = NewHire with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "e-alex", FirstName = "Alex", LastName = "Rivera", HourlyRate = 52m };
        var old = NewHire with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "e-old", TerminationDate = new DateOnly(2026, 9, 30) };
        var outcomes = await gusto.SyncEmployeesAsync("c0a1",
            [(NewHire, PayrollSyncAction.Create), (alex, PayrollSyncAction.Update), (old, PayrollSyncAction.Terminate)], default);

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Message));
        Assert.Equal("e-new", outcomes[0].ProviderEmployeeId);
        var comp = api.To(Put, "/v1/compensations/comp-new").Single().Json!;
        Assert.Equal(("cv1", "30.00", "Hour", "Nonexempt"), ((string?)comp["version"], (string?)comp["rate"], (string?)comp["payment_unit"], (string?)comp["flsa_status"]));
        Assert.Equal("2024-01-01", (string?)api.To(Post, "/v1/employees/e-new/jobs").Single().Json!["hire_date"]);
        Assert.Equal("ev7", (string?)api.To(Put, "/v1/employees/e-alex").Single().Json!["version"]);
        Assert.Equal("52.00", (string?)api.To(Post, "/v1/jobs/j-alex/compensations").Single().Json!["rate"]);
        Assert.Equal("2026-09-30", (string?)api.To(Post, "/v1/employees/e-old/terminations").Single().Json!["effective_date"]);
    }

    [Fact]
    public async Task Gusto_files_map_hours_by_column_and_read_the_payroll_journal()
    {
        var (gusto, _, _) = await GustoAsync(connected: false);
        var file = gusto.ExportHours(Hours("c0a1",
            new PayrollHoursLine("e-alex", "Rivera, Alex", "Regular Hours", 40m, null, "REG"),
            new PayrollHoursLine("e-alex", "Rivera, Alex", "Vacation Hours", 8m, null, "LV.PTO"),
            new PayrollHoursLine("e-pat", "Pat Morgan", "Overtime", 1.5m, null, "OT")));
        Assert.Equal("gusto-hours-bw-20261018.csv", file.FileName);
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Employee UUID,First Name,Last Name,Regular Hours,Overtime Hours,Vacation Hours", lines[0]);
        Assert.Equal("e-alex,Alex,Rivera,40.00,0.00,8.00", lines[1]);
        Assert.Equal("e-pat,Pat,Morgan,0.00,1.50,0.00", lines[2]);

        var register = gusto.ParseRegister("""
            Check Date,Employee,Employee UUID,Gross Earnings,Employee Taxes,Employer Taxes,Employee Deductions,Employer Contributions,Net Pay
            10/23/2026,Alex Rivera,e-alex,$400.00,60.00,30.60,20.00,50.00,320.00
            ,Totals,,400.00,60.00,30.60,20.00,50.00,320.00
            """);
        Assert.Equal(new PayrollRegisterRow("e-alex", "Alex Rivera", Check, 400m, 60m, 30.6m, 20m, 50m, 320m), Assert.Single(register.Rows));
        Assert.Contains("Employee UUID", Assert.Throws<ArgumentException>(() => gusto.ParseRegister("Name,Gross\nA,1")).Message);

        // Not connected: API calls say how to connect, before any request.
        Assert.Contains("isn't connected", (await Assert.ThrowsAsync<PayrollProviderApiException>(() => gusto.TestConnectionAsync(default))).Message);
        Assert.False(await gusto.IsConnectedAsync(default));
    }

    // ---- Paychex ----

    private static async Task<(PaychexPayrollProvider Provider, RecordedProvider Api)> PaychexAsync()
    {
        var api = new RecordedProvider("paychex")
            .On(Post, "/auth/oauth/v2/token", "token.json")
            .On(Get, "/companies?displayid=70125496", "companies.json")
            .On(Get, "/companies/00Z5V9BTIHRQF2CF7BTH/workers", "workers.json");
        var (store, _) = await TestConnections.CreateAsync("paychex", new Dictionary<string, string> { ["clientId"] = "pcx-client", ["clientSecret"] = "pcx-secret" }, "sandbox");
        return (new PaychexPayrollProvider(store, api), api);
    }

    [Fact]
    public async Task Paychex_puts_hours_on_unprocessed_checks_by_pay_component()
    {
        var (paychex, api) = await PaychexAsync();
        api.On(Get, "/companies/00Z5V9BTIHRQF2CF7BTH/payperiods?status=INITIAL", "payperiods-initial.json")
            .On(Get, "/companies/00Z5V9BTIHRQF2CF7BTH/paycomponents", "paycomponents.json")
            .On(Post, "/companies/00Z5V9BTIHRQF2CF7BTH/checks", "checks-created.json");

        var result = await paychex.SendHoursAsync(Hours("70125496",
            new PayrollHoursLine("2002", "Alex Rivera", "Hourly", 40m, null, "REG"),
            new PayrollHoursLine("2002", "Alex Rivera", "Overtime", 2m, null, "OT"),
            new PayrollHoursLine("1001", "Pat Morgan", "PTO", 8m, null, "LV.PTO")), default);
        Assert.Equal(2, result.Employees);

        var token = api.To(Post, "/auth/oauth/v2/token").Single();
        Assert.Equal("sandbox.api.paychex.com", token.Uri.Host);
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.Contains("client_id=pcx-client", token.Body);
        var checks = api.To(Post, "/companies/00Z5V9BTIHRQF2CF7BTH/checks").Single().Json!.AsArray();
        var alex = checks.Single(c => (string?)c!["workerId"] == "00Z5V9BTI8J4ZRS2S1QB")!;
        Assert.Equal("1080104833144450", (string?)alex["payPeriodId"]);
        Assert.Equal([("2000051", "40.00"), ("2000052", "2.00")], alex["earnings"]!.AsArray().Select(e => ((string?)e!["componentId"], (string?)e["payHours"])));
        Assert.Equal("2000060", (string?)checks.Single(c => (string?)c!["workerId"] == "00Z5V9BTI8J4ZRS2S1QC")!["earnings"]![0]!["componentId"]);

        var unknown = await Assert.ThrowsAsync<PayrollProviderApiException>(() => paychex.SendHoursAsync(Hours("70125496", new PayrollHoursLine("9999", "Nobody", "Hourly", 1m, null, "REG"), new PayrollHoursLine("2002", "Alex Rivera", "Bonus", 1m, null, "REG")), default));
        Assert.Contains("no worker with employee id 9999", unknown.Message);
        Assert.Contains("no pay component named \"Bonus\"", unknown.Message);
    }

    [Fact]
    public async Task Paychex_checks_map_to_register_rows_by_employee_id()
    {
        var (paychex, api) = await PaychexAsync();
        api.On(Get, "/companies/00Z5V9BTIHRQF2CF7BTH/payperiods?status=COMPLETED", "payperiods-completed.json")
            .On(Get, "/companies/00Z5V9BTIHRQF2CF7BTH/checks?payperiodid=1080104833144450", "checks.json");
        var register = await paychex.FetchRegisterAsync(new PayrollRegisterQuery("70125496", Start, End, Check, ["2002", "1001"]), default);

        Assert.Equal(new PayrollRegisterRow("2002", "Alex Rivera", Check, 400m, 60m, 30.6m, 20m, 50m, 320m), register.Rows.Single(r => r.ProviderEmployeeId == "2002"));
        Assert.Equal(new PayrollRegisterRow("1001", "Pat Morgan", Check, 4000m, 800m, 306m, 150m, 400m, 3050m), register.Rows.Single(r => r.ProviderEmployeeId == "1001"));
    }

    [Fact]
    public async Task Paychex_sync_creates_in_progress_workers_with_a_rate_and_terminates_by_status()
    {
        var (paychex, api) = await PaychexAsync();
        api.On(Post, "/companies/00Z5V9BTIHRQF2CF7BTH/workers", "worker-created.json")
            .On(Post, "/workers/", "worker-created.json")
            .On(Patch, "/workers/", "worker-created.json");
        var alex = NewHire with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "2002", TerminationDate = new DateOnly(2026, 9, 30) };
        var outcomes = await paychex.SyncEmployeesAsync("70125496", [(NewHire, PayrollSyncAction.Create), (alex, PayrollSyncAction.Terminate)], default);
        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Message));
        Assert.Equal("3003", outcomes[0].ProviderEmployeeId);

        var worker = api.To(Post, "/companies/00Z5V9BTIHRQF2CF7BTH/workers").Single().Json!.AsArray()[0]!;
        Assert.Equal(("EMPLOYEE", "NON_EXEMPT", "2024-01-01", "Chen"), ((string?)worker["workerType"], (string?)worker["exemptionType"], (string?)worker["hireDate"], (string?)worker["name"]!["familyName"]));
        var rate = api.To(Post, "/workers/00Z5V9BTI8J4ZRS2NEW1/compensation/payrates").Single().Json!.AsArray()[0]!;
        Assert.Equal(("HOURLY", "30.00"), ((string?)rate["rateType"], (string?)rate["amount"]));
        var terminate = api.To(Patch, "/workers/00Z5V9BTI8J4ZRS2S1QB").Single().Json!;
        Assert.Equal(("TERMINATED", "2026-09-30"), ((string?)terminate["currentStatus"]!["statusType"], (string?)terminate["currentStatus"]!["effectiveDate"]));
    }

    [Fact]
    public async Task Paychex_files_map_hours_and_read_the_payroll_journal()
    {
        var (paychex, _) = await PaychexAsync();
        var file = paychex.ExportHours(Hours("70125496", new PayrollHoursLine("2002", "Alex Rivera", "Hourly", 40m, "OPS", "REG"), new PayrollHoursLine("2002", "Alex Rivera", "PTO", 8m, "OPS", "LV.PTO")));
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', PaychexFormats.HoursHeader), lines[0]);
        Assert.Equal("70125496,2002,Alex Rivera,10/05/2026,10/18/2026,10/23/2026,Hourly,40.00,,,OPS", lines[1]);
        Assert.Equal("70125496,2002,Alex Rivera,10/05/2026,10/18/2026,10/23/2026,PTO,8.00,,,OPS", lines[2]);

        var register = paychex.ParseRegister("""
            Worker ID,Worker Name,Check Date,Gross,Employee Taxes,Employer Taxes,Deductions,Employer Benefits,Net Pay
            2002,Alex Rivera,2026-10-23,400.00,60.00,30.60,20.00,50.00,320.00
            2002,Alex Rivera,2026-10-23,0.00,0.00,0.00,0.00,0.00,0.00
            """);
        Assert.Equal(400m, Assert.Single(register.Rows).GrossWages);
        Assert.Contains(register.Warnings, w => w.Contains("combined"));
    }

    // ---- ADP ----

    private static async Task<(AdpPayrollProvider Provider, RecordedProvider Api)> AdpAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=workbench-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var api = new RecordedProvider("adp")
            .On(Post, "/auth/oauth/v2/token", "token.json")
            .On(Get, "/hr/v2/workers?$top=100&$skip=0", "workers.json");
        var (store, _) = await TestConnections.CreateAsync("adp", new Dictionary<string, string>
        {
            ["clientId"] = "adp-client",
            ["clientSecret"] = "adp-secret",
            ["certificate"] = cert.ExportCertificatePem(),
            ["privateKey"] = key.ExportPkcs8PrivateKeyPem(),
        });
        return (new AdpPayrollProvider(store, api), api);
    }

    [Fact]
    public async Task Adp_signs_in_with_the_client_certificate_and_sends_pay_data_by_associate()
    {
        var (adp, api) = await AdpAsync();
        api.On(Post, "/events/payroll/v1/pay-data-input.add", "pay-data-added.json");
        var result = await adp.SendHoursAsync(Hours("W1A",
            new PayrollHoursLine("002002", "Alex Rivera", "REG", 40m, null, "REG"),
            new PayrollHoursLine("002002", "Alex Rivera", "V", 8m, null, "LV.PTO"),
            new PayrollHoursLine("001001", "Pat Morgan", "OT", 1.25m, null, "OT")), default);
        Assert.Equal(2, result.Employees);

        Assert.Equal("CN=workbench-test", api.ClientCertificate!.Subject);
        Assert.True(api.ClientCertificate.HasPrivateKey);
        var token = api.To(Post, "/auth/oauth/v2/token").Single();
        Assert.Equal("accounts.adp.com", token.Uri.Host);
        Assert.StartsWith("Basic ", token.Authorization);
        Assert.Equal("Bearer adp-access", api.To(Get, "/hr/v2/workers").Single().Authorization);

        var payees = api.To(Post, "/events/payroll/v1/pay-data-input.add").Single().Json!["events"]![0]!["data"]!["transform"]!["payDataInput"]!["payeePayInputs"]!.AsArray();
        var alex = payees.Single(p => (string?)p!["associateOID"] == "G3BGDW1CRJ2Z1X7M")!["payrollProfilePayInputs"]![0]!;
        Assert.Equal(("W1A", "002002"), ((string?)alex["payrollGroupCode"]!["codeValue"], (string?)alex["payrollFileNumber"]));
        Assert.Equal([("REG", 40m), ("V", 8m)], alex["payInputs"]![0]!["earningInputs"]!.AsArray().Select(e => ((string?)e!["earningCode"]!["codeValue"], (decimal)e["numberOfHours"]!)));
    }

    [Fact]
    public async Task Adp_pay_statements_map_to_register_rows_with_a_warning_about_employer_costs()
    {
        var (adp, api) = await AdpAsync();
        api.On(Get, "/payroll/v1/workers/G3BGDW1CRJ2Z1X7M/pay-statements?", "pay-statements-alex.json")
            .On(Get, "/payroll/v1/workers/G3BGDW1CRJ2Z1X7M/pay-statements/ps-alex-1023", "pay-statement-alex.json");
        var register = await adp.FetchRegisterAsync(new PayrollRegisterQuery("W1A", Start, End, Check, ["002002", "009999"]), default);

        Assert.Equal(new PayrollRegisterRow("002002", "Rivera, Alex", Check, 400m, 60m, 0m, 20m, 0m, 320m), Assert.Single(register.Rows));
        Assert.Contains(register.Warnings, w => w.Contains("employer taxes"));
        Assert.Contains(register.Warnings, w => w.Contains("009999"));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("ps-alex-1009"));
    }

    [Fact]
    public async Task Adp_sync_hires_changes_and_terminates_through_worker_events()
    {
        var (adp, api) = await AdpAsync();
        api.On(Post, "/events/hr/v1/worker.", "event-ok.json").On(Post, "/events/hr/v1/worker.hire", "hired.json");
        var alex = NewHire with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "002002", FirstName = "Alex", LastName = "Rivera" };
        var pat = NewHire with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "001001", TerminationDate = new DateOnly(2026, 9, 30) };
        var gone = NewHire with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "404404" };
        var outcomes = await adp.SyncEmployeesAsync("W1A",
            [(NewHire, PayrollSyncAction.Create), (alex, PayrollSyncAction.Update), (pat, PayrollSyncAction.Terminate), (gone, PayrollSyncAction.Update)], default);

        Assert.Equal("003003", outcomes[0].ProviderEmployeeId);
        Assert.True(outcomes[1].Succeeded && outcomes[2].Succeeded);
        Assert.False(outcomes[3].Succeeded);
        Assert.Contains("no worker with file number 404404", outcomes[3].Message);

        var hire = api.To(Post, "/events/hr/v1/worker.hire").Single().Json!["events"]![0]!["data"]!["transform"]!["worker"]!;
        Assert.Equal(("W1A", 30m, "N"), ((string?)hire["workAssignment"]!["payrollGroupCode"]!["codeValue"],
            (decimal)hire["workAssignment"]!["baseRemuneration"]!["hourlyRateAmount"]!["amountValue"]!, (string?)hire["workAssignment"]!["wageLawCoverage"]!["coverageCode"]!["codeValue"]));
        var name = api.To(Post, "/events/hr/v1/worker.legal-name.change").Single().Json!["events"]![0]!["data"]!;
        Assert.Equal("G3BGDW1CRJ2Z1X7M", (string?)name["eventContext"]!["worker"]!["associateOID"]);
        var terminate = api.To(Post, "/events/hr/v1/worker.work-assignment.terminate").Single().Json!["events"]![0]!["data"]!;
        Assert.Equal(("35477310N", "2026-09-30"), ((string?)terminate["eventContext"]!["worker"]!["workAssignment"]!["itemID"], (string?)terminate["transform"]!["workAssignment"]!["terminationDate"]));
    }

    [Fact]
    public async Task Adp_files_are_epi_pay_data_and_the_payroll_register()
    {
        var (adp, _) = await AdpAsync();
        var file = adp.ExportHours(Hours("W1A",
            new PayrollHoursLine("002002", "Alex Rivera", "REG", 40m, null, "REG"),
            new PayrollHoursLine("002002", "Alex Rivera", "OT", 2m, null, "OT"),
            new PayrollHoursLine("002002", "Alex Rivera", "V", 8m, null, "LV.PTO"),
            new PayrollHoursLine("001001", "Pat Morgan", "H", 8m, null, "LV.HOL")));
        Assert.Equal("adp-epi-bw-20261018.csv", file.FileName);
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Co Code,Batch ID,File #,Reg Hours,O/T Hours,Hours 3 Code,Hours 3 Amount", lines[0]);
        Assert.StartsWith("W1A,WB1018,001001,,,H,8.00,", lines[1]);
        Assert.StartsWith("W1A,WB1018,002002,40.00,2.00,V,8.00,", lines[2]);

        var register = adp.ParseRegister("""
            File Number,Employee Name,Pay Date,Gross Pay,Total Employee Taxes,Total Employer Taxes,Total Deductions,Employer Benefits,Net Pay
            002002,"Rivera, Alex",10/23/2026,400.00,60.00,30.60,20.00,50.00,320.00
            """);
        Assert.Equal(new PayrollRegisterRow("002002", "Rivera, Alex", Check, 400m, 60m, 30.6m, 20m, 50m, 320m), Assert.Single(register.Rows));
        Assert.Contains("isn't valid PEM", Assert.Throws<PayrollProviderApiException>(() => AdpPayrollProvider.Certificate("nope", "nope")).Message);
    }

    [Fact]
    public async Task Provider_errors_come_back_as_plain_messages_without_credentials()
    {
        var (paychex, api) = await PaychexAsync();
        api.OnStatus(Post, "/auth/oauth/v2/token", System.Net.HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}");
        var refused = await Assert.ThrowsAsync<PayrollProviderApiException>(() => paychex.TestConnectionAsync(default));
        Assert.Contains("refused the credentials (401)", refused.Message);
        Assert.DoesNotContain("pcx-secret", refused.Message);

        var (gusto, gapi, _) = await GustoAsync();
        gapi.OnStatus(Get, "/v1/me", System.Net.HttpStatusCode.InternalServerError, "boom");
        var failed = await Assert.ThrowsAsync<PayrollProviderApiException>(() => gusto.TestConnectionAsync(default));
        Assert.Contains("Gusto returned 500 for GET v1/me: boom", failed.Message);
    }
}

/// <summary>The connect screen through the real services: credentials encrypted at rest, never returned or audited.</summary>
public sealed class PayrollConnectionServiceTests
{
    [Fact]
    public async Task Saving_credentials_encrypts_them_and_the_screen_shows_only_what_is_on_file()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var screen = await kit.Providers.GetConnectionAsync("gusto");
        Assert.False(screen.Connected);
        Assert.Equal(["clientId", "clientSecret", "refreshToken"], screen.Fields.Select(f => f.Key));
        Assert.Equal(["production", "sandbox"], screen.Environments);

        var saved = await kit.Providers.SaveConnectionAsync("gusto", new SavePayrollConnectionRequest("sandbox",
            new Dictionary<string, string?> { ["clientId"] = "client-abcdef123", ["clientSecret"] = "s3cret-value", ["refreshToken"] = "refresh-value" }, TestNow: false));
        Assert.True(saved.Connected);
        Assert.Equal("sandbox", saved.Environment);
        Assert.Equal("client-abcdef123", saved.Values["clientId"]);
        Assert.False(saved.Values.ContainsKey("clientSecret"));
        Assert.Equal(["clientSecret", "refreshToken"], saved.SecretsOnFile);
        Assert.Equal("Client ID client…", saved.Summary);

        // A blank secret keeps the one on file; a missing required one is refused.
        var kept = await kit.Providers.SaveConnectionAsync("gusto", new SavePayrollConnectionRequest("production",
            new Dictionary<string, string?> { ["clientId"] = "client-2", ["clientSecret"] = "", ["refreshToken"] = null }, TestNow: false));
        Assert.Equal(["clientSecret", "refreshToken"], kept.SecretsOnFile);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Providers.SaveConnectionAsync("paychex",
            new SavePayrollConnectionRequest("production", new Dictionary<string, string?> { ["clientId"] = "x" }, TestNow: false)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Providers.SaveConnectionAsync("gusto",
            new SavePayrollConnectionRequest("staging", new Dictionary<string, string?>(), TestNow: false)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Providers.GetConnectionAsync("paylocity"));

        await using (var db = await kit.Factory.CreateAsync())
        {
            var row = await db.Set<PayrollProviderConnection>().AsNoTracking().SingleAsync();
            Assert.DoesNotContain("s3cret-value", row.ProtectedCredentials);
            Assert.DoesNotContain("refresh-value", row.ProtectedCredentials);
            var audit = await db.Set<Domain.ProjectAccounting.AuditEvent>().AsNoTracking().Where(a => a.EntityType == PayrollProviderService.ConnectionAuditType).ToListAsync();
            Assert.Equal(["Connected", "Credentials changed"], audit.OrderBy(a => a.OccurredAtUtc).Select(a => a.Action));
            Assert.All(audit, a => Assert.DoesNotContain("s3cret", a.AfterJson ?? ""));
        }

        var listed = (await kit.Providers.ListProvidersAsync()).Single(p => p.Key == "gusto");
        Assert.True(listed.ApiConfigured);
        Assert.Equal("API connected", listed.Status);
        Assert.True(listed.SupportsHoursApi && listed.SupportsRegisterApi);

        var gone = await kit.Providers.DisconnectAsync("gusto");
        Assert.False(gone.Connected);
        Assert.Empty(gone.SecretsOnFile);

        // Only payroll setup may see or change connections.
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => kit.As(Identity.SeededHost.Viewer).Providers.GetConnectionAsync("gusto"));
    }

    [Fact]
    public async Task A_run_for_an_unconnected_provider_still_exports_the_file_but_not_through_the_api()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var group = await kit.Groups.CreateAsync(new SavePayGroupRequest("GU", "Gusto biweekly", PayFrequencyKind.Biweekly, "gusto", "c0a1", PayrollKit.PeriodStart, 5));
        await kit.Groups.GeneratePeriodsAsync(group.Id, 2026);
        var period = (await kit.Groups.ListPeriodsAsync(group.Id, 2026)).Single(p => p.Start == PayrollKit.PeriodStart);
        await kit.Profiles.SaveAsync(kit.Fx.AnalystId, new SavePayProfileRequest(group.Id, "e-alex", PayTypeKind.Hourly));
        var run = await kit.Runs.StartAsync(group.Id, period.Id);

        var settings = await kit.Providers.GetSettingsAsync("gusto");
        Assert.Equal("Regular Hours", settings.EarningCodes.Single(m => m.Category == "REG").EarningCode);
        var file = await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        Assert.StartsWith("gusto-hours-gu-", file.FileName);
        Assert.Contains("e-alex,Alex,Rivera,8.00", file.Content);

        Assert.Contains("isn't connected", (await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.SendHoursToProviderAsync(run.Id, new ExportHoursRequest(true)))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ImportRegisterFromProviderAsync(run.Id)); // not connected: the provider says so
    }
}
