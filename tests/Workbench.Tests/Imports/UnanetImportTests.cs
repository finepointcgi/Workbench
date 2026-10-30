using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportKind = Workbench.Contracts.Imports.ImportKind;
using ImportSource = Workbench.Contracts.Imports.ImportSource;

namespace Workbench.Tests.Imports;

public sealed class UnanetImportTests
{
    private static readonly (ImportKind Kind, string File)[] Order =
    [
        (ImportKind.OrgUnits, "organizations.csv"),
        (ImportKind.LaborCategories, "labor-categories.csv"),
        (ImportKind.Employees, "people.csv"),
        (ImportKind.CostRates, "cost-rates.csv"),
        (ImportKind.Customers, "customers.csv"),
        (ImportKind.Contracts, "projects.csv"),
        (ImportKind.Clins, "clins.csv"),
        (ImportKind.Projects, "projects.csv"),
        (ImportKind.BillRates, "bill-rates.csv"),
        (ImportKind.Budgets, "budgets.csv"),
        (ImportKind.Assignments, "assignments.csv"),
        (ImportKind.Vendors, "vendors.csv"),
        (ImportKind.OpenApBills, "open-ap.csv"),
        (ImportKind.OpenArInvoices, "open-ar.csv"),
        (ImportKind.Timesheets, "timesheets.csv"),
    ];

    internal static async Task ImportAllAsync(ImportHost host)
    {
        foreach (var (kind, file) in Order)
        {
            var job = await host.ImportAsync(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]);
            Assert.True(job.Job.Status == ImportJobStatus.Imported, $"{kind}: {job.Job.Status} {job.Job.Message}");
            Assert.True(job.Job.Counts.Errors == 0, $"{kind}: {string.Join("; ", job.Errors.Where(e => !e.IsWarning).Select(e => $"row {e.Row}: {e.Message}"))}");
        }
    }

    [Fact]
    public async Task Every_unanet_kind_maps_its_own_headings_with_no_manual_mapping()
    {
        await using var host = await ImportHost.CreateAsync();
        var kinds = await host.Imports.ListKindsAsync();
        Assert.Equal(Order.Select(o => o.Kind).Distinct().Order(), kinds.Where(k => k.Source == ImportSource.Unanet).Select(k => k.Kind).Order());

        foreach (var (kind, file) in Order)
        {
            var job = await host.Imports.UploadAsync(new UploadImportRequest(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]));
            var unmapped = job.Definition.Fields.Where(f => f.Required && job.Mapping.Fields.GetValueOrDefault(f.Name) is null).Select(f => f.Label);
            Assert.True(!unmapped.Any(), $"{kind}: needs a manual mapping for {string.Join(", ", unmapped)}");
        }
    }

    [Fact]
    public async Task Sample_files_import_people_contracts_projects_budgets_assignments_and_open_items()
    {
        await using var host = await ImportHost.CreateAsync();
        await ImportAllAsync(host);

        var people = host.Get<IPeopleService>();
        var orgs = await people.ListOrgUnitsAsync();
        Assert.Equal(orgs.Single(o => o.Code == "ENG").Id, orgs.Single(o => o.Code == "ENG-CY").ParentId);
        Assert.Equal(orgs.Single(o => o.Code == "HQ").Id, orgs.Single(o => o.Code == "ENG").ParentId);

        var employees = (await people.ListEmployeesAsync(includeInactive: true)).ToDictionary(e => e.EmployeeNumber);
        Assert.Equal(6, employees.Count);
        Assert.Equal("Daniel Okafor", employees["H1002"].DisplayName);
        Assert.Equal(employees["H1001"].Id, employees["H1002"].SupervisorId);
        Assert.False(employees["H1004"].IsExempt);
        Assert.True(employees["H1001"].IsExempt);
        Assert.Equal(new DateOnly(2019, 3, 4), employees["H1001"].HireDate);
        var rates = await people.ListCostRatesAsync(employees["H1002"].Id);
        Assert.Equal([69.25m, 66.00m], rates.Select(r => r.HourlyCost));

        var contracts = (await host.Get<IContractService>().ListContractsAsync()).ToDictionary(c => c.ContractNumber);
        Assert.Equal(["HHS-3305-26", "N00178-26-D-2201"], contracts.Keys.Order());
        Assert.Equal(ContractKind.TimeAndMaterials, contracts["N00178-26-D-2201"].Type);
        Assert.Equal(ContractKind.FirmFixedPrice, contracts["HHS-3305-26"].Type);
        Assert.Equal(1_500_000m, contracts["N00178-26-D-2201"].FundedValue);
        Assert.Equal(900_000m, contracts["HHS-3305-26"].FundedValue);

        var projects = host.Get<IProjectService>();
        var all = (await projects.ListProjectsAsync()).ToDictionary(p => p.Code);
        Assert.Equal(["HV-2201", "HV-3305", "HV-BP", "HV-LV", "HV-OH"], all.Keys.Order());
        Assert.Equal(contracts["N00178-26-D-2201"].Id, all["HV-2201"].ContractId);
        Assert.Null(all["HV-OH"].ContractId);

        var wbs = (await projects.ListWbsAsync(all["HV-2201"].Id)).ToDictionary(w => w.Code);
        Assert.Equal(["HV-2201.100", "HV-2201.200", "HV-2201.200.210", "HV-2201.200.220"], wbs.Keys.Order());
        Assert.Equal(wbs["HV-2201.200"].Id, wbs["HV-2201.200.210"].ParentId);
        Assert.True(wbs["HV-2201.200.210"].IsChargeable);
        Assert.False(wbs["HV-2201.200"].IsChargeable);
        var overhead = Assert.Single(await projects.ListWbsAsync(all["HV-OH"].Id));
        Assert.Equal("HV-OH.00", overhead.Code);
        Assert.Equal(ChargeCodeKind.Indirect, overhead.Kind);
        Assert.Equal(ChargeCodeKind.Leave, Assert.Single(await projects.ListWbsAsync(all["HV-LV"].Id)).Kind);

        var assigned = await projects.ListAssignmentsAsync(wbs["HV-2201.200.210"].Id);
        Assert.Equal(2, assigned.Count);

        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(2, await db.Set<Workbench.Domain.Billing.Invoice>().CountAsync());
    }

    [Fact]
    public async Task Approved_time_comes_in_as_approved_history_and_a_second_run_changes_nothing()
    {
        await using var host = await ImportHost.CreateAsync();
        foreach (var (kind, file) in Order.TakeWhile(o => o.Kind != ImportKind.Timesheets).Where(o => o.Kind is not (ImportKind.Vendors or ImportKind.OpenApBills or ImportKind.OpenArInvoices)))
        {
            await host.ImportAsync(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]);
        }

        var dry = await host.PrepareAsync(ImportSource.Unanet, ImportKind.Timesheets, [Samples.File("samples/import/unanet/timesheets.csv")]);
        Assert.True(dry.Job.Counts.Creates > 0);
        Assert.Equal(0, await CountAsync<Timesheet>(host));

        var job = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, job.Job.Status);
        Assert.Equal(0, job.Job.Counts.Errors);
        Assert.Contains(job.Errors, e => e.IsWarning && e.Message.Contains("Submitted"));

        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var sheets = await db.Set<Timesheet>().Include(t => t.Entries).ToListAsync();
        Assert.NotEmpty(sheets);
        Assert.All(sheets, s => Assert.Equal(TimesheetStatus.Approved, s.Status));
        Assert.All(sheets, s => Assert.All(s.Entries, e => Assert.InRange(e.WorkDate, s.PeriodStart, s.PeriodEnd)));
        Assert.Equal(6, sheets.Select(s => s.EmployeeId).Distinct().Count());

        // 22 weekdays in September 2026 (21 working days plus Labor Day as leave), 8 hours each, for each of six people.
        var approved = sheets.SelectMany(s => s.Entries).Sum(e => e.Hours);
        Assert.Equal(6 * 22 * 8m, approved);
        var leave = (await host.Get<IProjectService>().ListProjectsAsync()).Single(p => p.Code == "HV-LV");
        var leaveElement = Assert.Single(await host.Get<IProjectService>().ListWbsAsync(leave.Id));
        Assert.Equal(6 * 8m, sheets.SelectMany(s => s.Entries).Where(e => e.WbsElementId == leaveElement.Id).Sum(e => e.Hours));
        Assert.DoesNotContain(sheets.SelectMany(s => s.Entries), e => e.WorkDate >= new DateOnly(2026, 10, 1));

        var again = await host.ImportAsync(ImportSource.Unanet, ImportKind.Timesheets, [Samples.File("samples/import/unanet/timesheets.csv")]);
        Assert.Equal(0, again.Job.Counts.Creates);
        Assert.True(again.Job.Counts.Skips > 0);
        Assert.Equal(sheets.Count, await CountAsync<Timesheet>(host));
    }

    [Fact]
    public async Task Rolling_back_a_timesheet_import_removes_the_timesheets_and_entries()
    {
        await using var host = await ImportHost.CreateAsync();
        foreach (var (kind, file) in Order.Where(o => o.Kind is ImportKind.OrgUnits or ImportKind.LaborCategories or ImportKind.Employees or ImportKind.Customers
            or ImportKind.Contracts or ImportKind.Projects))
        {
            await host.ImportAsync(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]);
        }

        var job = await host.ImportAsync(ImportSource.Unanet, ImportKind.Timesheets, [Samples.File("samples/import/unanet/timesheets.csv")]);
        Assert.True(await CountAsync<TimeEntry>(host) > 100);
        var check = await host.Imports.CheckRollbackAsync(job.Job.Id);
        Assert.True(check.CanRollBack, string.Join("; ", check.Blockers));

        var rolled = await host.Imports.RollBackAsync(job.Job.Id);
        Assert.Equal(ImportJobStatus.RolledBack, rolled.Job.Status);
        Assert.Equal(0, await CountAsync<Timesheet>(host));
        Assert.Equal(0, await CountAsync<TimeEntry>(host));
    }

    [Fact]
    public async Task One_bad_row_leaves_out_that_persons_whole_period_instead_of_importing_half_of_it()
    {
        await using var host = await ImportHost.CreateAsync();
        foreach (var (kind, file) in Order.Where(o => o.Kind is ImportKind.OrgUnits or ImportKind.LaborCategories or ImportKind.Employees or ImportKind.Customers
            or ImportKind.Contracts or ImportKind.Projects))
        {
            await host.ImportAsync(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]);
        }

        var csv = "Employee ID,Project Code,Task Code,Work Date,Hours,Timesheet Status\n"
            + "H1002,HV-2201,210,09/01/2026,8,Approved\n"
            + "H1002,HV-2201,210,09/02/2026,8,Approved\n"
            + "H1002,HV-2201,999,09/03/2026,8,Approved\n"
            + "H1003,HV-2201,210,09/01/2026,8,Approved\n";
        var job = await host.ImportAsync(ImportSource.Unanet, ImportKind.Timesheets, [Samples.Csv("time.csv", csv)]);
        Assert.Contains(job.Errors, e => !e.IsWarning && e.Row == 4 && e.Message.Contains("999"));
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var employee = (await host.Get<IPeopleService>().ListEmployeesAsync(true)).Single(e => e.EmployeeNumber == "H1003");
        var sheet = Assert.Single(await db.Set<Timesheet>().Include(t => t.Entries).ToListAsync());
        Assert.Equal(employee.Id, sheet.EmployeeId);
        Assert.Contains(job.Plan, p => p.Action == ImportRowAction.Skip && p.Summary.Contains("error"));
    }

    [Fact]
    public async Task Weekly_layout_with_a_week_ending_date_and_a_column_per_weekday_is_read()
    {
        await using var host = await ImportHost.CreateAsync();
        foreach (var (kind, file) in Order.Where(o => o.Kind is ImportKind.OrgUnits or ImportKind.LaborCategories or ImportKind.Employees or ImportKind.Customers
            or ImportKind.Contracts or ImportKind.Projects))
        {
            await host.ImportAsync(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]);
        }

        var csv = "Employee ID,Project Code,Task Code,Week Ending,Sun,Mon,Tue,Wed,Thu,Fri,Sat,Timesheet Status\n"
            + "H1002,HV-2201,210,09/12/2026,0,8,8,8,4,0,0,Approved\n"
            + "H1002,HV-2201,220,09/12/2026,0,0,0,0,4,8,0,Approved\n";
        var job = await host.ImportAsync(ImportSource.Unanet, ImportKind.Timesheets, [Samples.Csv("weekly.csv", csv)]);
        Assert.Equal(0, job.Job.Counts.Errors);
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var entries = await db.Set<TimeEntry>().ToListAsync();
        Assert.Equal(40m, entries.Sum(e => e.Hours));
        Assert.Contains(entries, e => e.WorkDate == new DateOnly(2026, 9, 7) && e.Hours == 8);
        Assert.Contains(entries, e => e.WorkDate == new DateOnly(2026, 9, 11) && e.Hours == 8);
        Assert.DoesNotContain(entries, e => e.WorkDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public async Task Dry_run_says_in_plain_words_what_it_will_do_and_changes_nothing()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await host.PrepareAsync(ImportSource.Unanet, ImportKind.Employees, [Samples.File("samples/import/unanet/people.csv")]);
        Assert.Equal(6, dry.Plan.Count(p => p.Action == ImportRowAction.Create && p.EntityType == "Employee"));
        Assert.Empty(await host.Get<IPeopleService>().ListEmployeesAsync(true));
        Assert.Equal(ImportJobStatus.DryRunDone, dry.Job.Status);
    }

    private static async Task<int> CountAsync<T>(ImportHost host) where T : class
    {
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<T>().CountAsync();
    }
}
