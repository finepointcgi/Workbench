using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Imports;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportKind = Workbench.Contracts.Imports.ImportKind;
using ImportSource = Workbench.Contracts.Imports.ImportSource;

namespace Workbench.Tests.Imports;

public sealed class CostpointImportTests
{
    [Fact]
    public async Task Sample_files_import_people_contract_clins_project_rates_and_budget()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();

        var people = host.Get<IPeopleService>();
        var orgs = await people.ListOrgUnitsAsync();
        var navy = orgs.Single(o => o.Code == "1.01.10");
        Assert.Equal(orgs.Single(o => o.Code == "1.01").Id, navy.ParentId);

        var employees = await people.ListEmployeesAsync(includeInactive: true);
        Assert.Equal(5, employees.Count);
        var robin = employees.Single(e => e.EmployeeNumber == "E2001");
        var chris = employees.Single(e => e.EmployeeNumber == "E2002");
        Assert.Equal("Robin Nguyen", robin.DisplayName);
        Assert.Equal(new DateOnly(2021, 3, 15), robin.HireDate);
        Assert.Equal(robin.Id, chris.SupervisorId);
        Assert.Equal(navy.Id, robin.OrgUnitId);
        var jamie = employees.Single(e => e.EmployeeNumber == "E2005");
        Assert.False(jamie.IsExempt);
        Assert.Equal(30m, jamie.StandardHoursPerWeek);
        var plcs = await people.ListLaborCategoriesAsync();
        Assert.Equal(plcs.Single(l => l.Code == "PM4").Id, robin.DefaultLaborCategoryId);
        var rates = await people.ListCostRatesAsync(robin.Id);
        Assert.Equal([96.20m, 92.50m], rates.Select(r => r.HourlyCost));

        var contract = (await host.Get<IContractService>().ListContractsAsync()).Single(c => c.ContractNumber == "N66001-27-C-2001");
        Assert.Equal(ContractKind.CostPlusFixedFee, contract.Type);
        Assert.Equal(415_000m, contract.FundedValue);
        var clins = await host.Get<IClinService>().ListClinsAsync(contract.Id);
        Assert.Equal(["0001", "0002", "0003"], clins.Select(c => c.Number));
        Assert.Equal(0.07m, clins[0].FeeRate);
        Assert.Equal(ClinPricingKind.CostNoFee, clins[1].Pricing);
        Assert.True(clins[2].IsOption);
        Assert.False(clins[2].IsExercised);
        Assert.Equal(400_000m, clins[0].FundedValue);
        var mods = await host.Get<IContractModificationService>().ListAsync(contract.Id);
        Assert.Single(mods);
        Assert.Equal(415_000m, mods[0].FundingDelta);

        var projects = host.Get<IProjectService>();
        var project = (await projects.ListProjectsAsync()).Single(p => p.Code == "2001");
        Assert.Equal(contract.Id, project.ContractId);
        Assert.Equal(robin.Id, project.ProjectManagerId);
        Assert.Equal(ProjectState.Active, project.Status);
        var wbs = await projects.ListWbsAsync(project.Id);
        Assert.Equal(8, wbs.Count);
        var dev = wbs.Single(w => w.Code == "2001.02.02");
        var platform = wbs.Single(w => w.Code == "2001.02");
        Assert.Equal(platform.Id, dev.ParentId);
        Assert.Null(platform.ParentId);
        Assert.True(dev.IsChargeable);
        Assert.False(platform.IsChargeable);
        Assert.True(dev.RequiresAssignment);
        Assert.False(wbs.Single(w => w.Code == "2001.03").IsOpen);
        var mappings = await host.Get<IClinService>().ListWbsMappingsAsync(project.Id);
        Assert.Equal(clins[0].Id, mappings.Single(m => m.WbsElementId == dev.Id).ClinId);

        var indirect = (await projects.ListProjectsAsync()).Single(p => p.Code == "9000");
        var indirectWbs = await projects.ListWbsAsync(indirect.Id);
        Assert.Equal(ChargeCodeKind.Indirect, indirectWbs.Single(w => w.Code == "9000.10").Kind);
        Assert.Equal(ChargeCodeKind.Leave, indirectWbs.Single(w => w.Code == "9000.90").Kind);
        Assert.False(indirectWbs.Single(w => w.Code == "9000.10").RequiresAssignment);

        var billRates = await host.Get<IContractService>().ListBillRatesAsync(contract.Id);
        Assert.Equal(5, billRates.Count);
        Assert.Equal(212m, billRates.Single(r => r.LaborCategoryId == robin.DefaultLaborCategoryId).HourlyBillRate);

        var budgets = host.Get<IBudgetService>();
        var version = Assert.Single(await budgets.ListVersionsAsync(project.Id));
        Assert.Equal("Costpoint budget", version.Name);
        Assert.Equal(BudgetVersionKind.Current, version.Kind);
        var detail = await budgets.GetVersionAsync(version.Id);
        Assert.Equal(1060m, detail!.Lines.Sum(l => l.Hours));
        Assert.Equal(new DateOnly(2026, 11, 1), detail.Lines.Single(l => l.WbsCode == "2001.02.02" && l.LaborCategoryCode == "SWE3").PeriodStart);
        var travel = detail.Lines.Single(l => l.CostType == BudgetCostKind.Odc);
        Assert.Equal((BudgetOdcType.Travel, 12_000m, (DateOnly?)null), (travel.OdcType!.Value, travel.Amount, travel.PeriodStart));
    }

    [Fact]
    public async Task Header_aliases_are_matched_case_insensitively_and_a_title_row_above_the_headers_is_skipped()
    {
        await using var host = await ImportHost.CreateAsync();
        var csv = """
            Employee Listing Report,,,,
            employee id,Full Name,E-Mail,hire date,plc
            E9,"Doe, Jane",JANE@EXAMPLE.COM,2024-02-01,ENG1
            """;
        var job = await host.Imports.UploadAsync(new UploadImportRequest(ImportSource.Costpoint, ImportKind.Employees, [Samples.Csv("employees.csv", csv)]));
        Assert.Equal("employee id", job.Mapping.Fields["EmployeeNumber"]);
        Assert.Equal("Full Name", job.Mapping.Fields["Name"]);
        Assert.Equal("E-Mail", job.Mapping.Fields["Email"]);
        Assert.Equal("hire date", job.Mapping.Fields["HireDate"]);
        Assert.Equal("plc", job.Mapping.Fields["LaborCategory"]);
        Assert.Contains("Full Name", job.Headers);

        await host.Imports.SaveMappingAsync(job.Job.Id, new SaveImportMappingRequest(job.Mapping, job.Options));
        await host.Imports.ValidateAsync(job.Job.Id);
        var dry = await host.Imports.DryRunAsync(job.Job.Id);
        Assert.Equal(2, dry.Job.Counts.Creates); // the PLC and the employee
        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        var jane = Assert.Single(await host.Get<IPeopleService>().ListEmployeesAsync());
        Assert.Equal(("Jane Doe", "jane@example.com"), (jane.DisplayName, jane.Email));
        Assert.Contains(await host.Get<IPeopleService>().ListLaborCategoriesAsync(), l => l.Code == "ENG1");
    }

    [Fact]
    public async Task Bad_rows_are_reported_with_row_numbers_and_skipped_while_good_rows_import()
    {
        await using var host = await ImportHost.CreateAsync();
        var csv = """
            EMPL_ID,EMPL_NAME,EMAIL_ID,ORIG_HIRE_DT,SUPERVISOR_EMPL_ID,HRLY_AMT
            E1,Ann Able,ann@example.com,01/02/2024,,50
            E2,Bob Baker,,01/02/2024,,50
            E3,Cy Cole,cy@example.com,not-a-date,,50
            E4,Di Dunn,di@example.com,01/02/2024,E99,50
            E1,Ann Again,ann2@example.com,01/02/2024,,50
            E5,Ed Ely,ed@example.com,01/02/2024,,-3
            """;
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.Employees, [Samples.Csv("employees.csv", csv)]);
        var errors = dry.Errors.Where(e => !e.IsWarning).ToList();
        Assert.Contains(errors, e => e.Row == 3 && e.Field == "Email");
        Assert.Contains(errors, e => e.Row == 4 && e.Field == "HireDate");
        Assert.Contains(errors, e => e.Row == 5 && e.Field == "Supervisor" && e.Message.Contains("E99"));
        Assert.Contains(errors, e => e.Row == 6 && e.Message.Contains("more than once"));
        Assert.Contains(errors, e => e.Row == 7 && e.Field == "HourlyCost");
        Assert.Equal(5, dry.Job.Counts.Errors);

        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        var employee = Assert.Single(await host.Get<IPeopleService>().ListEmployeesAsync());
        Assert.Equal("E1", employee.EmployeeNumber);
    }

    [Fact]
    public async Task Missing_required_mapping_is_a_file_level_error()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.Customers, [Samples.Csv("c.csv", "CUST_ID,Something\nA,B\n")]);
        var error = Assert.Single(dry.Errors);
        Assert.Equal((0, "Name"), (error.Row, error.Field));
        Assert.Equal(0, dry.Job.Counts.Creates);
    }

    [Fact]
    public async Task Repeat_imports_skip_unchanged_rows_and_update_changed_ones()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();

        foreach (var (kind, file) in Samples.CostpointOrder)
        {
            var again = await host.PrepareAsync(ImportSource.Costpoint, kind, [Samples.File($"samples/import/costpoint/{file}")]);
            Assert.True(again.Job.Counts.Creates == 0 && again.Job.Counts.Updates == 0,
                $"{file}: {string.Join("; ", again.Plan.Where(p => p.Action != ImportRowAction.Skip).Select(p => $"{p.Action} {p.Key} {p.Summary}"))}");
            Assert.Equal(0, again.Job.Counts.Errors);
        }

        // A renamed employee and more funding update in place.
        var employees = File.ReadAllText(Samples.PathOf("samples/import/costpoint/employees.csv")).Replace("\"Okafor, Chris\"", "\"Okafor, Christopher\"");
        var update = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Employees, [Samples.Csv("employees.csv", employees)]);
        Assert.Equal(1, update.Job.Counts.Updates);
        var people = await host.Get<IPeopleService>().ListEmployeesAsync();
        Assert.Equal(5, people.Count);
        Assert.Equal("Christopher Okafor", people.Single(e => e.EmployeeNumber == "E2002").DisplayName);

        var clins = File.ReadAllText(Samples.PathOf("samples/import/costpoint/clins.csv")).Replace("\"400,000.00\",7,N", "\"550,000.00\",7,N");
        var funding = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Clins, [Samples.Csv("clins.csv", clins)]);
        Assert.Equal(0, funding.Job.Counts.Errors);
        var contract = (await host.Get<IContractService>().ListContractsAsync()).Single(c => c.ContractNumber == "N66001-27-C-2001");
        Assert.Equal(565_000m, contract.FundedValue);
        var mods = await host.Get<IContractModificationService>().ListAsync(contract.Id);
        Assert.Equal(2, mods.Count);
        Assert.Contains(mods, m => m.FundingDelta == 150_000m);
    }

    [Fact]
    public async Task Cost_rate_on_an_existing_date_with_a_different_amount_is_an_error()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.Employees, "employees.csv");
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.CostRates,
            [Samples.Csv("rates.csv", "EMPL_ID,EFFECT_DT,HRLY_AMT\nE2001,10/01/2025,99.99\nE2001,10/01/2025,92.50\nE7777,10/01/2025,10\n")]);
        Assert.Contains(dry.Errors, e => e.Row == 2 && e.Message.Contains("append-only"));
        Assert.Contains(dry.Errors, e => e.Row == 4 && e.Message.Contains("E7777"));
        Assert.Equal(1, dry.Job.Counts.Skips);
    }

    [Fact]
    public async Task Projects_reject_wbs_rows_whose_parent_or_project_is_missing()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.Projects,
            [Samples.Csv("projects.csv", "PROJ_ID,PROJ_NAME,PROJ_TYPE_DC\n3001,Alpha,Direct\n3001.01.01,Orphan,\n4001.01,No project,\n3001.02,Weird,Sideways\n")]);
        Assert.Contains(dry.Errors, e => e.Row == 3 && e.Message.Contains("3001.01"));
        Assert.Contains(dry.Errors, e => e.Row == 4 && e.Message.Contains("4001"));
        Assert.Contains(dry.Errors, e => e.Row == 5 && e.Field == "Indirect");
        Assert.Equal(1, dry.Job.Counts.Creates);
    }

    [Fact]
    public async Task Excel_exports_are_read_like_csv()
    {
        await using var host = await ImportHost.CreateAsync();
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Customers");
        sheet.Cell(1, 1).Value = "Customer Report";
        sheet.Cell(3, 1).Value = "CUST_ID";
        sheet.Cell(3, 2).Value = "CUST_NAME";
        sheet.Cell(4, 1).Value = "NAVY";
        sheet.Cell(4, 2).Value = "Department of the Navy";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var job = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Customers, [new UploadFileRequest("customers.xlsx", stream.ToArray())]);
        Assert.Equal(ImportJobStatus.Imported, job.Job.Status);
        Assert.Equal("Department of the Navy", Assert.Single(await host.Get<IContractService>().ListCustomersAsync()).Name);
    }

    [Fact]
    public async Task Each_import_keeps_an_audit_trail_of_what_it_touched()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.ImportCostpointAsync(ImportKind.Customers, "customers.csv");
        var records = await host.Imports.ListRecordsAsync(job.Job.Id);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(Workbench.Contracts.Imports.ImportRecordAction.Created, r.Action));
        var audit = await host.Get<IAuditLogService>().ListAsync("ImportJob", job.Job.Id);
        Assert.Equal(["Imported", "Uploaded"], audit.Select(a => a.Action));
        var customerAudit = await host.Get<IAuditLogService>().ListAsync("Customer");
        Assert.Equal(2, customerAudit.Count);
    }

    [Fact]
    public async Task Steps_must_run_in_order()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.Imports.UploadAsync(new UploadImportRequest(ImportSource.Costpoint, ImportKind.Customers, [Samples.File("samples/import/costpoint/customers.csv")]));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Imports.ImportAsync(job.Job.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Imports.DryRunAsync(job.Job.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Imports.UploadAsync(new UploadImportRequest(ImportSource.Costpoint, ImportKind.Customers, [Samples.Csv("x.pdf", "nope")])));
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(Workbench.Domain.Imports.ImportJobStatus.Uploaded, (await db.Set<ImportJob>().SingleAsync()).Status);
    }
}
