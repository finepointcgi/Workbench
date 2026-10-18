using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>Organization roles in front of the project-accounting services, and the current-user context.</summary>
public sealed class OrgRoleTests
{
    private static readonly DateOnly Hired = new(2025, 1, 6);

    public static TheoryData<string> FinanceOrAdmin => new() { Finance, Admin };

    public static TheoryData<string> NotFinance => new() { Editor, Timekeeper, Owner };

    /// <summary>An employee for analyst@, a customer and a contract, created by an admin.</summary>
    private static async Task<(Guid EmployeeId, Guid ContractId, Guid CategoryId)> SetUpAsync(SeededHost seeded)
    {
        var host = seeded.As(Admin).Host;
        var people = host.Get<IPeopleService>();
        var category = await people.CreateLaborCategoryAsync(new SaveLaborCategoryRequest("ENG2", "Engineer II"));
        var employee = await people.CreateEmployeeAsync(new SaveEmployeeRequest("E9001", Editor, "Alex", null, null, category.Id, Hired));
        var contracts = host.Get<IContractService>();
        var customer = await contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "Army"));
        var contract = await contracts.CreateContractAsync(new SaveContractRequest(customer.Id, "W91-1", "Support", ContractKind.TimeAndMaterials,
            ContractState.Active, true, Hired, Hired.AddYears(3), 1_000_000m, 250_000m));
        return (employee.Id, contract.Id, category.Id);
    }

    [Theory]
    [MemberData(nameof(NotFinance))]
    public async Task Rates_setup_writes_fiscal_close_and_audit_need_finance(string email)
    {
        await using var seeded = await CreateAsync();
        var (employeeId, contractId, categoryId) = await SetUpAsync(seeded);
        await seeded.As(Admin).Host.Get<IFiscalCalendarService>().CreateFiscalYearAsync(2026, new DateOnly(2025, 10, 1));
        var periodId = (await seeded.Host.Get<IFiscalCalendarService>().ListPeriodsAsync(2026))[0].Id;
        var host = seeded.As(email).Host;
        var people = host.Get<IPeopleService>();
        var contracts = host.Get<IContractService>();
        var projects = host.Get<IProjectService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => people.ListCostRatesAsync(employeeId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => people.AddCostRateAsync(employeeId, new AddCostRateRequest(Hired, 55m, "Hire")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => people.CreateLaborCategoryAsync(new SaveLaborCategoryRequest("X", "X")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => contracts.ListBillRatesAsync(contractId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => contracts.AddBillRateAsync(contractId, new AddBillRateRequest(categoryId, Hired, 120m)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => contracts.CreateCustomerAsync(new SaveCustomerRequest("NAVY", "Navy")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.CreateProjectAsync(new SaveProjectRequest("2001", "New", contractId, null, ProjectState.Planning, null, null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.Get<IFiscalCalendarService>().SetPeriodStatusAsync(periodId, FiscalPeriodState.Closed, "Month end"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IAuditLogService>().ListAsync());

        // Setup data itself stays readable: timesheets and plans need it.
        Assert.NotEmpty(await people.ListEmployeesAsync());
        Assert.NotEmpty(await contracts.ListContractsAsync());
        Assert.NotEmpty(await host.Get<IFiscalCalendarService>().ListPeriodsAsync(2026));
    }

    [Theory]
    [MemberData(nameof(FinanceOrAdmin))]
    public async Task Finance_and_admins_manage_rates_setup_and_fiscal_close(string email)
    {
        await using var seeded = await CreateAsync();
        var (employeeId, contractId, categoryId) = await SetUpAsync(seeded);
        var host = seeded.As(email).Host;

        await host.Get<IPeopleService>().AddCostRateAsync(employeeId, new AddCostRateRequest(Hired, 55m, "Hire"));
        await host.Get<IContractService>().AddBillRateAsync(contractId, new AddBillRateRequest(categoryId, Hired, 120m));
        var project = await host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest("2001", "New", contractId, null, ProjectState.Active, null, null));
        await host.Get<IProjectService>().CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "2001.01", "Task 1"));
        var periods = await host.Get<IFiscalCalendarService>().CreateFiscalYearAsync(2026, new DateOnly(2025, 10, 1));
        var closed = await host.Get<IFiscalCalendarService>().SetPeriodStatusAsync(periods[0].Id, FiscalPeriodState.Closed, "Month end");

        Assert.Single(await host.Get<IPeopleService>().ListCostRatesAsync(employeeId));
        Assert.Single(await host.Get<IContractService>().ListBillRatesAsync(contractId));
        Assert.Equal(FiscalPeriodState.Closed, closed.Status);
        Assert.Contains(await host.Get<IAuditLogService>().ListAsync(), e => e.Actor == email);
    }

    [Fact]
    public async Task Current_user_context_has_roles_and_the_matching_employee()
    {
        await using var seeded = await CreateAsync();
        var (employeeId, _, _) = await SetUpAsync(seeded);

        var analyst = await seeded.As(Editor).Host.Get<ICurrentUserContext>().GetAsync();
        var finance = await seeded.As(Finance).Host.Get<ICurrentUserContext>().GetAsync();
        var timekeeper = await seeded.As(Timekeeper).Host.Get<ICurrentUserContext>().GetAsync();
        var admin = await seeded.As(Admin).Host.Get<ICurrentUserContext>().GetAsync();
        var current = await seeded.As(Editor).Host.Get<IPeopleService>().GetCurrentEmployeeAsync();

        Assert.Equal((OrgRoles.Employee, (Guid?)employeeId, false), (analyst.Roles, analyst.EmployeeId, analyst.CanViewAllTimesheets));
        Assert.Equal(employeeId, current!.Id);
        Assert.True(finance.IsFinance);
        Assert.Null(finance.EmployeeId);
        Assert.True(timekeeper.CanViewAllTimesheets);
        Assert.False(timekeeper.IsFinance);
        Assert.True(admin.IsAdmin && admin.IsFinance && admin.CanViewAllTimesheets);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(string.Empty).Host.Get<ICurrentUserContext>().GetAsync());
    }

    [Fact]
    public void Role_checks_combine_flags()
    {
        Assert.False(OrgRoles.Employee.IsFinance());
        Assert.True(OrgRoles.Finance.IsFinance());
        Assert.True(OrgRoles.Admin.IsFinance());
        Assert.False(OrgRoles.Timekeeper.IsFinance());
        Assert.True((OrgRoles.Timekeeper | OrgRoles.Finance).CanViewAllTimesheets());
        Assert.False(OrgRoles.Employee.CanViewAllTimesheets());
        Assert.False(OrgRoles.Finance.IsAdmin());
    }
}
