using Microsoft.Extensions.Options;
using Workbench.Application.Payroll;

namespace Workbench.Tests.Payroll;

/// <summary>The Paylocity hours import and payroll register formats, the REST adapter (against a fake) and the stub providers.</summary>
public sealed class PaylocityFormatTests
{
    [Fact]
    public void Hours_export_is_the_Paylocity_payroll_import_csv()
    {
        var csv = PaylocityFormats.WriteHours(new PayrollHoursExport("W1234", "BW", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 23),
        [
            new("2002", "Rivera, Alex", "REG", 72m, "ENG"),
            new("2002", "Rivera, Alex", "PTO", 8m, "ENG"),
            new("1001", "Pat Morgan", "OT", 2.5m, null),
            new("1001", "Pat Morgan", "REG", 0m, null),
        ]));

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Company Code,Employee Id,Employee Name,Pay Period Begin,Pay Period End,Check Date,Det,Det Code,Hours,Rate,Amount,Cost Center 1", lines[0]);
        Assert.Equal(
        [
            "W1234,1001,Pat Morgan,10/05/2026,10/18/2026,10/23/2026,E,OT,2.50,,,",
            "W1234,2002,\"Rivera, Alex\",10/05/2026,10/18/2026,10/23/2026,E,PTO,8.00,,,ENG",
            "W1234,2002,\"Rivera, Alex\",10/05/2026,10/18/2026,10/23/2026,E,REG,72.00,,,ENG",
        ], lines[1..]);
    }

    [Fact]
    public void Register_parses_quoted_amounts_aliases_and_skips_the_totals_row()
    {
        var parsed = PaylocityFormats.ParseRegister("""
            Emp Id,Name,Pay Date,Gross Wages,Employee Taxes,Employer Taxes,Total Deductions,Employer Contributions,Net Check
            1001,"Morgan, Pat",10/23/2026,"$4,000.00",800.00,306.00,150.00,400.00,"3,050.00"
            2002,Alex Rivera,2026-10-23,400,60,30.6,20,50,320
            ,,Totals,,"4,400.00",,,,,
            """);

        Assert.Equal(2, parsed.Rows.Count);
        var pat = parsed.Rows[0];
        Assert.Equal(("1001", "Morgan, Pat", new DateOnly(2026, 10, 23)), (pat.ProviderEmployeeId, pat.EmployeeName, pat.CheckDate));
        Assert.Equal((4_000m, 800m, 306m, 150m, 400m, 3_050m), (pat.GrossWages, pat.EmployeeTaxes, pat.EmployerTaxes, pat.Deductions, pat.EmployerBenefits, pat.NetPay));
        Assert.Equal(30.6m, parsed.Rows[1].EmployerTaxes);
    }

    [Fact]
    public void Register_combines_split_rows_and_rejects_bad_files()
    {
        var parsed = PaylocityFormats.ParseRegister("Employee Id,Check Date,Gross Pay,Net Pay\n7,10/23/2026,100,90\n7,10/23/2026,50,45\n");
        Assert.Equal((150m, 135m), (parsed.Rows.Single().GrossWages, parsed.Rows.Single().NetPay));
        Assert.Single(parsed.Warnings);

        Assert.Contains("Gross Pay", Assert.Throws<ArgumentException>(() => PaylocityFormats.ParseRegister("Employee Id,Check Date,Net Pay\n7,10/23/2026,90\n")).Message);
        Assert.Contains("isn't an amount", Assert.Throws<ArgumentException>(() => PaylocityFormats.ParseRegister("Employee Id,Check Date,Gross Pay,Net Pay\n7,10/23/2026,abc,90\n")).Message);
        Assert.Contains("check date", Assert.Throws<ArgumentException>(() => PaylocityFormats.ParseRegister("Employee Id,Check Date,Gross Pay,Net Pay\n7,soon,100,90\n")).Message);
        Assert.Throws<ArgumentException>(() => PaylocityFormats.ParseRegister(""));
    }

    [Fact]
    public void The_sample_register_parses_and_balances()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "payroll")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var parsed = PaylocityFormats.ParseRegister(File.ReadAllText(Path.Combine(dir.FullName, "samples", "payroll", "paylocity-register-sample.csv")));
        Assert.Equal(["1001", "1002", "1003"], parsed.Rows.Select(r => r.ProviderEmployeeId));
        Assert.All(parsed.Rows, r => Assert.Equal(r.GrossWages - r.EmployeeTaxes - r.Deductions, r.NetPay));
        Assert.Equal(13_780m, parsed.Rows.Sum(r => r.GrossWages));
    }

    [Fact]
    public async Task Employee_sync_goes_through_the_api_client_only_when_configured()
    {
        var fake = new FakePaylocityApi();
        var record = new PayrollEmployeeRecord(Guid.NewGuid(), null, "E1", "Pat", "Morgan", "pm@example.com", new DateOnly(2024, 1, 1), null, true, "Salaried", 104_000m, null, "OPS");

        var unconfigured = new PaylocityPayrollProvider(fake, Options.Create(new PaylocityOptions()));
        Assert.False(unconfigured.ApiConfigured);
        await Assert.ThrowsAsync<ArgumentException>(() => unconfigured.SyncEmployeesAsync("W1234", [(record, PayrollSyncAction.Create)], CancellationToken.None));
        Assert.Empty(fake.Calls);

        var provider = new PaylocityPayrollProvider(fake, Options.Create(new PaylocityOptions { ClientId = "id", ClientSecret = "secret" }));
        var terminated = record with { EmployeeId = Guid.NewGuid(), ProviderEmployeeId = "77", TerminationDate = new DateOnly(2026, 9, 30) };
        var outcomes = await provider.SyncEmployeesAsync("W1234", [(record, PayrollSyncAction.Create), (terminated, PayrollSyncAction.Terminate), (record with { ProviderEmployeeId = "88" }, PayrollSyncAction.Update)], CancellationToken.None);

        Assert.All(outcomes, o => Assert.True(o.Succeeded));
        Assert.Equal("new-1", outcomes[0].ProviderEmployeeId);
        Assert.Equal(["create W1234 Morgan", "terminate W1234 77 2026-09-30", "update W1234 88"], fake.Calls);

        fake.Fail = true;
        var failed = await provider.SyncEmployeesAsync("W1234", [(record with { ProviderEmployeeId = "88" }, PayrollSyncAction.Update)], CancellationToken.None);
        Assert.False(failed.Single().Succeeded);
    }

    private sealed class FakePaylocityApi : IPaylocityApiClient
    {
        public List<string> Calls { get; } = [];
        public bool Fail { get; set; }

        public Task<string> CreateEmployeeAsync(string companyId, PaylocityEmployee employee, CancellationToken cancellationToken)
        {
            Calls.Add($"create {companyId} {employee.LastName}");
            return Task.FromResult($"new-{Calls.Count}");
        }

        public Task UpdateEmployeeAsync(string companyId, string employeeId, PaylocityEmployee employee, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new PaylocityApiException("Paylocity returned 500");
            }

            Calls.Add($"update {companyId} {employeeId}");
            return Task.CompletedTask;
        }

        public Task TerminateEmployeeAsync(string companyId, string employeeId, DateOnly effectiveDate, CancellationToken cancellationToken)
        {
            Calls.Add($"terminate {companyId} {employeeId} {effectiveDate:yyyy-MM-dd}");
            return Task.CompletedTask;
        }
    }
}
