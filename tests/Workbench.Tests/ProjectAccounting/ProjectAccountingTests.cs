using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Tests.ProjectAccounting;

public sealed class ProjectAccountingTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);
    private readonly PeopleService _people;
    private readonly ProjectService _projects;
    private readonly ContractService _contracts;
    private readonly FiscalCalendarService _calendar;
    private readonly AuditLogService _audit;

    public ProjectAccountingTests()
    {
        var factory = TestDb.CreateFactory();
        var user = new TestUser("finance@example.com");
        var recorder = new AuditRecorder(user, TimeProvider.System);
        _people = new PeopleService(factory, recorder, user, TimeProvider.System);
        _projects = new ProjectService(factory, recorder);
        _contracts = new ContractService(factory, recorder);
        _calendar = new FiscalCalendarService(factory, recorder);
        _audit = new AuditLogService(factory);
    }

    private async Task<(EmployeeDto Employee, LaborCategoryDto Category)> NewEmployeeAsync(string email = "alex@example.com", bool defaultCategory = true)
    {
        var category = await _people.CreateLaborCategoryAsync(new SaveLaborCategoryRequest($"C{Guid.NewGuid():N}"[..8], "Analyst"));
        var employee = await _people.CreateEmployeeAsync(new SaveEmployeeRequest($"E{Guid.NewGuid():N}"[..8], email, "Alex", null, null,
            defaultCategory ? category.Id : null, Today.AddYears(-1)));
        return (employee, category);
    }

    private async Task<ProjectDto> NewProjectAsync(string code = "1001", ProjectState status = ProjectState.Active) =>
        await _projects.CreateProjectAsync(new SaveProjectRequest(code, "Modernization", null, null, status, null, null));

    [Fact]
    public async Task Charge_codes_include_assigned_leaves_and_unassigned_indirect_codes_only()
    {
        var (employee, _) = await NewEmployeeAsync();
        var project = await NewProjectAsync();
        var root = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "Root", IsChargeable: false));
        var assigned = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.01", "Assigned"));
        await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.02", "Not assigned"));
        await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.03", "Closed", IsOpen: false, RequiresAssignment: false));
        await _projects.AddAssignmentAsync(assigned.Id, new AddChargeAssignmentRequest(employee.Id));

        var indirect = await NewProjectAsync("IND");
        await _projects.CreateWbsElementAsync(indirect.Id, new SaveWbsElementRequest(null, "IND.OH", "Overhead", ChargeCodeKind.Indirect, RequiresAssignment: false));

        var codes = await _projects.ListChargeCodesAsync(employee.Id, Today);

        Assert.Equal(["1001.01.01", "IND.OH"], codes.Select(c => c.Code));
        Assert.All(codes, c => Assert.Equal(employee.DefaultLaborCategoryId, c.LaborCategoryId));
    }

    [Fact]
    public async Task Charge_codes_respect_assignment_dates_and_project_status()
    {
        var (employee, _) = await NewEmployeeAsync();
        var project = await NewProjectAsync();
        var code = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "Work"));
        await _projects.AddAssignmentAsync(code.Id, new AddChargeAssignmentRequest(employee.Id, From: Today.AddDays(1)));
        var onHold = await NewProjectAsync("2002", ProjectState.OnHold);
        await _projects.CreateWbsElementAsync(onHold.Id, new SaveWbsElementRequest(null, "2002.01", "Paused", RequiresAssignment: false));

        Assert.Empty(await _projects.ListChargeCodesAsync(employee.Id, Today));
        Assert.Single(await _projects.ListChargeCodesAsync(employee.Id, Today.AddDays(1)));
    }

    [Fact]
    public async Task Employee_without_a_labor_category_needs_one_on_the_assignment()
    {
        var (employee, category) = await NewEmployeeAsync(defaultCategory: false);
        var project = await NewProjectAsync();
        var a = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "No category"));
        var b = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.02", "With category"));
        await _projects.AddAssignmentAsync(a.Id, new AddChargeAssignmentRequest(employee.Id));
        await _projects.AddAssignmentAsync(b.Id, new AddChargeAssignmentRequest(employee.Id, category.Id));

        var code = Assert.Single(await _projects.ListChargeCodesAsync(employee.Id, Today));
        Assert.Equal("1001.02", code.Code);
    }

    [Fact]
    public async Task Wbs_codes_must_extend_the_parent_code_and_be_unique()
    {
        var project = await NewProjectAsync();
        var root = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "Root"));

        await Assert.ThrowsAsync<ArgumentException>(() => _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1002.01", "Wrong prefix")));
        await Assert.ThrowsAsync<ArgumentException>(() => _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "Duplicate")));
    }

    [Fact]
    public async Task Cost_rates_are_append_only_and_audited_with_a_reason()
    {
        var (employee, _) = await NewEmployeeAsync();

        await _people.AddCostRateAsync(employee.Id, new AddCostRateRequest(Today, 55m, "Annual merit increase"));

        await Assert.ThrowsAsync<ArgumentException>(() => _people.AddCostRateAsync(employee.Id, new AddCostRateRequest(Today, 60m, "Duplicate date")));
        await Assert.ThrowsAsync<ArgumentException>(() => _people.AddCostRateAsync(employee.Id, new AddCostRateRequest(Today.AddDays(1), 60m, " ")));
        var entry = Assert.Single(await _audit.ListAsync("EmployeeCostRate"));
        Assert.Equal("Annual merit increase", entry.Reason);
        Assert.Equal("finance@example.com", entry.Actor);
    }

    [Fact]
    public async Task Contract_funding_cannot_exceed_ceiling()
    {
        var customer = await _contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "U.S. Army"));

        await Assert.ThrowsAsync<ArgumentException>(() => _contracts.CreateContractAsync(new SaveContractRequest(
            customer.Id, "W1", "Support", ContractKind.TimeAndMaterials, ContractState.Active, true, Today, Today.AddYears(1), 100m, 200m)));
    }

    [Fact]
    public async Task Fiscal_year_has_twelve_monthly_periods_and_closing_needs_a_reason()
    {
        var periods = await _calendar.CreateFiscalYearAsync(2027, new DateOnly(2026, 10, 1));

        Assert.Equal(12, periods.Count);
        Assert.Equal(new DateOnly(2026, 10, 31), periods[0].End);
        Assert.Equal(new DateOnly(2027, 9, 30), periods[^1].End);
        Assert.Equal("2026-11", (await _calendar.GetPeriodForDateAsync(new DateOnly(2026, 11, 15)))!.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => _calendar.SetPeriodStatusAsync(periods[0].Id, FiscalPeriodState.Closed, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => _calendar.CreateFiscalYearAsync(2028, new DateOnly(2027, 3, 1)));
    }

    [Fact]
    public async Task Current_employee_is_matched_by_sign_in_email()
    {
        await NewEmployeeAsync("Finance@Example.com");

        var me = await _people.GetCurrentEmployeeAsync();

        Assert.Equal("finance@example.com", me!.Email);
    }

    private sealed class TestUser(string email) : ICurrentUser
    {
        public string Email => email;
    }
}
