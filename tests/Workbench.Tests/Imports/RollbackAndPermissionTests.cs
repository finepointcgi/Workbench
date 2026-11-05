using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Imports;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportKind = Workbench.Contracts.Imports.ImportKind;
using ImportSource = Workbench.Contracts.Imports.ImportSource;

namespace Workbench.Tests.Imports;

public sealed class ImportRollbackTests
{
    [Fact]
    public async Task Rolling_back_deletes_what_the_import_created()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.LaborCategories, "labor-categories.csv");
        var job = await host.ImportCostpointAsync(ImportKind.Employees, "employees.csv");
        Assert.Equal(5, (await host.Get<IPeopleService>().ListEmployeesAsync()).Count);

        var check = await host.Imports.CheckRollbackAsync(job.Job.Id);
        Assert.True(check.CanRollBack, string.Join(" ", check.Blockers));
        var rolledBack = await host.Imports.RollBackAsync(job.Job.Id);
        Assert.Equal(ImportJobStatus.RolledBack, rolledBack.Job.Status);
        Assert.Equal(ImportHost.Finance, rolledBack.Job.RolledBackBy);
        Assert.Empty(await host.Get<IPeopleService>().ListEmployeesAsync(includeInactive: true));
        Assert.Equal(5, (await host.Get<IPeopleService>().ListLaborCategoriesAsync()).Count);

        var audit = await host.Get<IAuditLogService>().ListAsync("Employee");
        Assert.Contains(audit, a => a.Action == "RolledBack" && a.Reason!.Contains(job.Job.Id.ToString()[..8]));
        Assert.False((await host.Imports.CheckRollbackAsync(job.Job.Id)).CanRollBack);

        // Importing the same file again after a rollback creates the employees again.
        var again = await host.ImportCostpointAsync(ImportKind.Employees, "employees.csv");
        Assert.Equal(ImportJobStatus.Imported, again.Job.Status);
        Assert.Equal(5, (await host.Get<IPeopleService>().ListEmployeesAsync()).Count);
    }

    [Fact]
    public async Task Rolling_back_an_update_restores_the_previous_values()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.Customers, "customers.csv");
        var rename = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Customers,
            [Samples.Csv("customers.csv", "CUST_ID,CUST_NAME,CUST_TYPE\nNIWCPAC,NIWC Pacific,DoD\n")]);
        Assert.Equal(1, rename.Job.Counts.Updates);
        Assert.Contains(await host.Get<IContractService>().ListCustomersAsync(), c => c.Name == "NIWC Pacific");

        await host.Imports.RollBackAsync(rename.Job.Id);
        var customers = await host.Get<IContractService>().ListCustomersAsync();
        Assert.Equal(2, customers.Count);
        Assert.Contains(customers, c => c.Code == "NIWCPAC" && c.Name == "Naval Information Warfare Center Pacific");
    }

    [Fact]
    public async Task Rollback_is_refused_when_something_references_the_imported_records()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();
        var jobs = await host.Imports.ListJobsAsync();
        var projectsJob = jobs.Single(j => j.Kind == ImportKind.Projects);

        // The budget import (a later job) uses the WBS.
        var blocked = await host.Imports.CheckRollbackAsync(projectsJob.Id);
        Assert.False(blocked.CanRollBack);
        Assert.Contains(blocked.Blockers, b => b.Contains("Budget line") && b.Contains("roll that back first"));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Imports.RollBackAsync(projectsJob.Id));
        Assert.Contains("can't be rolled back", ex.Message);

        // Roll back the budget first; then a charge assignment added by hand still blocks.
        await host.Imports.RollBackAsync(jobs.Single(j => j.Kind == ImportKind.Budgets).Id);
        var projects = host.Get<IProjectService>();
        var project = (await projects.ListProjectsAsync()).Single(p => p.Code == "2001");
        var dev = (await projects.ListWbsAsync(project.Id)).Single(w => w.Code == "2001.02.02");
        var employee = (await host.Get<IPeopleService>().ListEmployeesAsync()).First();
        var assignment = await projects.AddAssignmentAsync(dev.Id, new AddChargeAssignmentRequest(employee.Id));
        var check = await host.Imports.CheckRollbackAsync(projectsJob.Id);
        Assert.Contains(check.Blockers, b => b.Contains("Charge assignment") && b.Contains("2001.02.02"));

        await projects.RemoveAssignmentAsync(assignment.Id);
        Assert.True((await host.Imports.CheckRollbackAsync(projectsJob.Id)).CanRollBack);
        await host.Imports.RollBackAsync(projectsJob.Id);
        Assert.DoesNotContain(await projects.ListProjectsAsync(), p => p.Code is "2001" or "9000");
    }

    [Fact]
    public async Task Rollback_is_refused_when_an_imported_record_was_changed_since()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.ImportCostpointAsync(ImportKind.Customers, "customers.csv");
        var customer = (await host.Get<IContractService>().ListCustomersAsync()).First();
        await host.Get<ISetupAdminService>().UpdateCustomerAsync(customer.Id, new SaveCustomerRequest(customer.Code, "Edited by hand", customer.Category));

        var check = await host.Imports.CheckRollbackAsync(job.Job.Id);
        Assert.False(check.CanRollBack);
        Assert.Contains(check.Blockers, b => b.Contains($"Customer {customer.Code}") && b.Contains("changed"));
    }

    [Fact]
    public async Task Rolling_back_clin_funding_removes_the_modification_and_restores_totals()
    {
        await using var host = await ImportHost.CreateAsync();
        foreach (var (kind, file) in Samples.CostpointOrder.Take(6))
        {
            await host.ImportCostpointAsync(kind, file);
        }

        var clinJob = await host.ImportCostpointAsync(ImportKind.Clins, "clins.csv");
        var contract = (await host.Get<IContractService>().ListContractsAsync()).Single();
        Assert.Equal(415_000m, contract.FundedValue);

        await host.Imports.RollBackAsync(clinJob.Job.Id);
        contract = (await host.Get<IContractService>().ListContractsAsync()).Single();
        Assert.Equal(0m, contract.FundedValue);
        Assert.Equal(1_850_000m, contract.CeilingValue);
        Assert.Empty(await host.Get<IClinService>().ListClinsAsync(contract.Id));
        Assert.Empty(await host.Get<IContractModificationService>().ListAsync(contract.Id));
    }

    [Fact]
    public async Task Rolling_back_a_budget_update_puts_the_old_lines_back()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();
        var project = (await host.Get<IProjectService>().ListProjectsAsync()).Single(p => p.Code == "2001");
        var budgets = host.Get<IBudgetService>();
        var version = Assert.Single(await budgets.ListVersionsAsync(project.Id));

        var changed = File.ReadAllText(Samples.PathOf("samples/import/costpoint/budgets.csv")).Replace("2001.02.03,TW1,2027,3,40", "2001.02.03,TW1,2027,3,400");
        var update = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Budgets, [Samples.Csv("budgets.csv", changed)]);
        Assert.Equal(1, update.Job.Counts.Updates);
        Assert.Equal(1420m, (await budgets.GetVersionAsync(version.Id))!.Lines.Sum(l => l.Hours));

        await host.Imports.RollBackAsync(update.Job.Id);
        var restored = (await budgets.GetVersionAsync(version.Id))!;
        Assert.Equal(1060m, restored.Lines.Sum(l => l.Hours));
        Assert.Equal(11, restored.Lines.Count);
    }

    [Fact]
    public async Task Rolling_back_a_smartsheet_import_deletes_the_sheet_and_unlinks_the_wbs()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();
        var project = (await host.Get<IProjectService>().ListProjectsAsync()).Single(p => p.Code == "2001");
        var job = await host.ImportAsync(ImportSource.SmartsheetExcel, ImportKind.Sheets, [Samples.File("samples/import/smartsheet/project-plan.xlsx")],
            new ImportOptionsDto(ProjectId: project.Id));
        Assert.NotNull((await host.Get<IProjectService>().GetProjectAsync(project.Id))!.SheetId);

        var check = await host.Imports.CheckRollbackAsync(job.Job.Id);
        Assert.True(check.CanRollBack, string.Join(" ", check.Blockers));
        await host.Imports.RollBackAsync(job.Job.Id);

        Assert.Empty(await host.Get<ISheetService>().ListAsync());
        Assert.Null((await host.Get<IProjectService>().GetProjectAsync(project.Id))!.SheetId);
        Assert.All(await host.Get<IProjectService>().ListWbsAsync(project.Id), w => Assert.Null(w.ItemId));
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Empty(await db.Set<ImportKeyMap>().ToListAsync());
        Assert.Empty(await db.Items.ToListAsync());
    }

    [Fact]
    public async Task A_comment_added_to_an_imported_row_blocks_rollback()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.ImportAsync(ImportSource.SmartsheetExcel, ImportKind.Sheets, [Samples.File("samples/import/smartsheet/project-plan.xlsx")]);
        var sheet = Assert.Single(await host.Get<ISheetService>().ListAsync());
        var item = (await host.Get<IItemService>().ListAsync(sheet.Id)).First();
        await host.Get<Workbench.Contracts.Collaboration.ICommentService>().CreateAsync(sheet.Id, item.Id, new Workbench.Contracts.Collaboration.CreateCommentRequest("New note"));

        var check = await host.Imports.CheckRollbackAsync(job.Job.Id);
        Assert.Contains(check.Blockers, b => b.Contains("comment"));
    }
}

public sealed class ImportPermissionTests
{
    [Fact]
    public async Task Only_finance_and_admins_can_import()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.Imports.UploadAsync(new UploadImportRequest(ImportSource.Costpoint, ImportKind.Customers, [Samples.File("samples/import/costpoint/customers.csv")]));

        host.User.Email = ImportHost.Employee;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Imports.ListJobsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Imports.ListKindsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Imports.GetJobAsync(job.Job.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.Imports.UploadAsync(new UploadImportRequest(ImportSource.Costpoint, ImportKind.Customers, [Samples.File("samples/import/costpoint/customers.csv")])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Imports.ImportAsync(job.Job.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Imports.RollBackAsync(job.Job.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Imports.ConnectSmartsheetAsync(new ConnectSmartsheetRequest(ImportHost.Token)));

        host.User.Email = ImportHost.Finance;
        Assert.Single(await host.Imports.ListJobsAsync());
    }

    [Fact]
    public async Task Import_kinds_describe_their_fields_and_sample_files()
    {
        await using var host = await ImportHost.CreateAsync();
        var kinds = await host.Imports.ListKindsAsync();
        Assert.Equal(21, kinds.Count(k => k.Source != ImportSource.Unanet && k.Source != ImportSource.CaptureExec)); // 18 Costpoint and Smartsheet, plus 3 Costpoint calendar/rate kinds; Unanet and Capture Exec are counted by their own tests.
        foreach (var kind in kinds.Where(k => k.SampleFile is not null))
        {
            Assert.True(File.Exists(Samples.PathOf(kind.SampleFile!)), kind.SampleFile);
        }

        var employees = kinds.Single(k => k.Kind == ImportKind.Employees && k.Source == ImportSource.Costpoint);
        Assert.Contains(employees.Fields, f => f.Name == "EmployeeNumber" && f.Required && f.Aliases.Contains("EMPL_ID"));
    }
}
