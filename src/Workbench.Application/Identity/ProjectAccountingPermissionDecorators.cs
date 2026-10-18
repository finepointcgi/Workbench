using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.Identity;

// Organization-role checks in front of the project-accounting services. Anyone signed in can read setup data
// (employees, contracts, projects, WBS, the fiscal calendar) because timesheets and plans need it. Rates are
// sensitive both ways; setup writes, fiscal close and the audit log are for Finance and Admin.

public sealed class PermissionCheckingPeopleService(IPeopleService inner, AccessControl access) : IPeopleService
{
    public Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListEmployeesAsync(includeInactive, cancellationToken);
    }

    public Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetEmployeeAsync(employeeId, cancellationToken);
    }

    public Task<EmployeeDto?> GetCurrentEmployeeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetCurrentEmployeeAsync(cancellationToken);
    }

    public async Task<EmployeeDto> CreateEmployeeAsync(SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("add employees", cancellationToken);
        return await inner.CreateEmployeeAsync(request, cancellationToken);
    }

    public async Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change employees", cancellationToken);
        return await inner.UpdateEmployeeAsync(employeeId, request, cancellationToken);
    }

    public Task<IReadOnlyList<OrgUnitDto>> ListOrgUnitsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListOrgUnitsAsync(cancellationToken);
    }

    public async Task<OrgUnitDto> CreateOrgUnitAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("add organization units", cancellationToken);
        return await inner.CreateOrgUnitAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<LaborCategoryDto>> ListLaborCategoriesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListLaborCategoriesAsync(cancellationToken);
    }

    public async Task<LaborCategoryDto> CreateLaborCategoryAsync(SaveLaborCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up labor categories", cancellationToken);
        return await inner.CreateLaborCategoryAsync(request, cancellationToken);
    }

    public async Task<LaborCategoryDto> UpdateLaborCategoryAsync(Guid laborCategoryId, SaveLaborCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up labor categories", cancellationToken);
        return await inner.UpdateLaborCategoryAsync(laborCategoryId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CostRateDto>> ListCostRatesAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("see cost rates", cancellationToken);
        return await inner.ListCostRatesAsync(employeeId, cancellationToken);
    }

    public async Task<CostRateDto> AddCostRateAsync(Guid employeeId, AddCostRateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change cost rates", cancellationToken);
        return await inner.AddCostRateAsync(employeeId, request, cancellationToken);
    }
}

/// <summary>
/// Anyone signed in can list contracts (pickers and labels need their numbers and names), but ceiling and funded values
/// are funding data: only Finance, administrators and the managers of a project on the contract see them; everyone else
/// gets zeros.
/// </summary>
public sealed class PermissionCheckingContractService(IContractService inner, AccessControl access, ProjectAccess projects) : IContractService
{
    public Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListCustomersAsync(cancellationToken);
    }

    public async Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up customers", cancellationToken);
        return await inner.CreateCustomerAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ContractDto>> ListContractsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var contracts = await inner.ListContractsAsync(cancellationToken);
        var managed = await projects.ManagedContractIdsAsync(cancellationToken);
        return managed is null ? contracts : contracts.Select(c => WithoutFunding(c, managed)).ToList();
    }

    public async Task<ContractDto?> GetContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var contract = await inner.GetContractAsync(contractId, cancellationToken);
        var managed = contract is null ? null : await projects.ManagedContractIdsAsync(cancellationToken);
        return contract is null || managed is null ? contract : WithoutFunding(contract, managed);
    }

    private static ContractDto WithoutFunding(ContractDto contract, IReadOnlySet<Guid> managed) =>
        managed.Contains(contract.Id) ? contract : contract with { CeilingValue = 0, FundedValue = 0 };

    public async Task<ContractDto> CreateContractAsync(SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up contracts", cancellationToken);
        return await inner.CreateContractAsync(request, cancellationToken);
    }

    public async Task<ContractDto> UpdateContractAsync(Guid contractId, SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change contracts", cancellationToken);
        return await inner.UpdateContractAsync(contractId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<BillRateDto>> ListBillRatesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("see bill rates", cancellationToken);
        return await inner.ListBillRatesAsync(contractId, cancellationToken);
    }

    public async Task<BillRateDto> AddBillRateAsync(Guid contractId, AddBillRateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change bill rates", cancellationToken);
        return await inner.AddBillRateAsync(contractId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingProjectService(IProjectService inner, AccessControl access) : IProjectService
{
    public Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListProjectsAsync(cancellationToken);
    }

    public Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetProjectAsync(projectId, cancellationToken);
    }

    public Task<ProjectDto?> GetProjectForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetProjectForSheetAsync(sheetId, cancellationToken);
    }

    public async Task<ProjectDto> CreateProjectAsync(SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up projects", cancellationToken);
        return await inner.CreateProjectAsync(request, cancellationToken);
    }

    public async Task<ProjectDto> UpdateProjectAsync(Guid projectId, SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change project setup", cancellationToken);
        return await inner.UpdateProjectAsync(projectId, request, cancellationToken);
    }

    public Task<IReadOnlyList<WbsElementDto>> ListWbsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListWbsAsync(projectId, cancellationToken);
    }

    public async Task<WbsElementDto> CreateWbsElementAsync(Guid projectId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up the WBS", cancellationToken);
        return await inner.CreateWbsElementAsync(projectId, request, cancellationToken);
    }

    public async Task<WbsElementDto> UpdateWbsElementAsync(Guid wbsElementId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change the WBS", cancellationToken);
        return await inner.UpdateWbsElementAsync(wbsElementId, request, cancellationToken);
    }

    public Task<IReadOnlyList<ChargeAssignmentDto>> ListAssignmentsAsync(Guid wbsElementId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListAssignmentsAsync(wbsElementId, cancellationToken);
    }

    public async Task<ChargeAssignmentDto> AddAssignmentAsync(Guid wbsElementId, AddChargeAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("assign people to charge codes", cancellationToken);
        return await inner.AddAssignmentAsync(wbsElementId, request, cancellationToken);
    }

    public async Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("remove charge assignments", cancellationToken);
        await inner.RemoveAssignmentAsync(assignmentId, cancellationToken);
    }

    public Task<IReadOnlyList<ChargeCodeDto>> ListChargeCodesAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListChargeCodesAsync(employeeId, date, cancellationToken);
    }
}

public sealed class PermissionCheckingFiscalCalendarService(IFiscalCalendarService inner, AccessControl access) : IFiscalCalendarService
{
    public Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListPeriodsAsync(fiscalYear, cancellationToken);
    }

    public Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetPeriodForDateAsync(date, cancellationToken);
    }

    public async Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up fiscal years", cancellationToken);
        return await inner.CreateFiscalYearAsync(fiscalYear, start, cancellationToken);
    }

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("close or reopen fiscal periods", cancellationToken);
        return await inner.SetPeriodStatusAsync(periodId, status, reason, cancellationToken);
    }
}

public sealed class PermissionCheckingAuditLogService(IAuditLogService inner, AccessControl access) : IAuditLogService
{
    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(string? entityType = null, Guid? entityId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("read the audit log", cancellationToken);
        return await inner.ListAsync(entityType, entityId, limit, cancellationToken);
    }
}
