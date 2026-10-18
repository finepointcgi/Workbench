using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.ApiClient;

public sealed class HttpPeopleService(HttpClient http) : IPeopleService
{
    public async Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/employees?includeInactive={includeInactive}", cancellationToken).ReadListAsync<EmployeeDto>(cancellationToken);

    public Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<EmployeeDto>($"api/v1/employees/{employeeId}", cancellationToken);

    public Task<EmployeeDto?> GetCurrentEmployeeAsync(CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<EmployeeDto>("api/v1/employees/me", cancellationToken);

    public async Task<EmployeeDto> CreateEmployeeAsync(SaveEmployeeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/employees", request, cancellationToken)).ReadAsync<EmployeeDto>(cancellationToken);

    public async Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/employees/{employeeId}", request, cancellationToken)).ReadAsync<EmployeeDto>(cancellationToken);

    public async Task<IReadOnlyList<OrgUnitDto>> ListOrgUnitsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/org-units", cancellationToken).ReadListAsync<OrgUnitDto>(cancellationToken);

    public async Task<OrgUnitDto> CreateOrgUnitAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/org-units", request, cancellationToken)).ReadAsync<OrgUnitDto>(cancellationToken);

    public async Task<IReadOnlyList<LaborCategoryDto>> ListLaborCategoriesAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/labor-categories", cancellationToken).ReadListAsync<LaborCategoryDto>(cancellationToken);

    public async Task<LaborCategoryDto> CreateLaborCategoryAsync(SaveLaborCategoryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/labor-categories", request, cancellationToken)).ReadAsync<LaborCategoryDto>(cancellationToken);

    public async Task<LaborCategoryDto> UpdateLaborCategoryAsync(Guid laborCategoryId, SaveLaborCategoryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/labor-categories/{laborCategoryId}", request, cancellationToken)).ReadAsync<LaborCategoryDto>(cancellationToken);

    public async Task<IReadOnlyList<CostRateDto>> ListCostRatesAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/employees/{employeeId}/cost-rates", cancellationToken).ReadListAsync<CostRateDto>(cancellationToken);

    public async Task<CostRateDto> AddCostRateAsync(Guid employeeId, AddCostRateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/employees/{employeeId}/cost-rates", request, cancellationToken)).ReadAsync<CostRateDto>(cancellationToken);
}

public sealed class HttpContractService(HttpClient http) : IContractService
{
    public async Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/customers", cancellationToken).ReadListAsync<CustomerDto>(cancellationToken);

    public async Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/customers", request, cancellationToken)).ReadAsync<CustomerDto>(cancellationToken);

    public async Task<IReadOnlyList<ContractDto>> ListContractsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/contracts", cancellationToken).ReadListAsync<ContractDto>(cancellationToken);

    public Task<ContractDto?> GetContractAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<ContractDto>($"api/v1/contracts/{contractId}", cancellationToken);

    public async Task<ContractDto> CreateContractAsync(SaveContractRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/contracts", request, cancellationToken)).ReadAsync<ContractDto>(cancellationToken);

    public async Task<ContractDto> UpdateContractAsync(Guid contractId, SaveContractRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/contracts/{contractId}", request, cancellationToken)).ReadAsync<ContractDto>(cancellationToken);

    public async Task<IReadOnlyList<BillRateDto>> ListBillRatesAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/contracts/{contractId}/bill-rates", cancellationToken).ReadListAsync<BillRateDto>(cancellationToken);

    public async Task<BillRateDto> AddBillRateAsync(Guid contractId, AddBillRateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/contracts/{contractId}/bill-rates", request, cancellationToken)).ReadAsync<BillRateDto>(cancellationToken);
}

public sealed class HttpProjectService(HttpClient http) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/projects", cancellationToken).ReadListAsync<ProjectDto>(cancellationToken);

    public Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<ProjectDto>($"api/v1/projects/{projectId}", cancellationToken);

    public Task<ProjectDto?> GetProjectForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<ProjectDto>($"api/v1/projects/by-sheet/{sheetId}", cancellationToken);

    public async Task<ProjectDto> CreateProjectAsync(SaveProjectRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/projects", request, cancellationToken)).ReadAsync<ProjectDto>(cancellationToken);

    public async Task<ProjectDto> UpdateProjectAsync(Guid projectId, SaveProjectRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/projects/{projectId}", request, cancellationToken)).ReadAsync<ProjectDto>(cancellationToken);

    public async Task<IReadOnlyList<WbsElementDto>> ListWbsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/projects/{projectId}/wbs", cancellationToken).ReadListAsync<WbsElementDto>(cancellationToken);

    public async Task<WbsElementDto> CreateWbsElementAsync(Guid projectId, SaveWbsElementRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/wbs", request, cancellationToken)).ReadAsync<WbsElementDto>(cancellationToken);

    public async Task<WbsElementDto> UpdateWbsElementAsync(Guid wbsElementId, SaveWbsElementRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/wbs/{wbsElementId}", request, cancellationToken)).ReadAsync<WbsElementDto>(cancellationToken);

    public async Task<IReadOnlyList<ChargeAssignmentDto>> ListAssignmentsAsync(Guid wbsElementId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/wbs/{wbsElementId}/assignments", cancellationToken).ReadListAsync<ChargeAssignmentDto>(cancellationToken);

    public async Task<ChargeAssignmentDto> AddAssignmentAsync(Guid wbsElementId, AddChargeAssignmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/wbs/{wbsElementId}/assignments", request, cancellationToken)).ReadAsync<ChargeAssignmentDto>(cancellationToken);

    public async Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/assignments/{assignmentId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChargeCodeDto>> ListChargeCodesAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/employees/{employeeId}/charge-codes?date={date:yyyy-MM-dd}", cancellationToken).ReadListAsync<ChargeCodeDto>(cancellationToken);
}

public sealed class HttpFiscalCalendarService(HttpClient http) : IFiscalCalendarService
{
    public async Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default) =>
        await http.GetAsync(fiscalYear is { } y ? $"api/v1/fiscal/periods?year={y}" : "api/v1/fiscal/periods", cancellationToken).ReadListAsync<FiscalPeriodDto>(cancellationToken);

    public Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<FiscalPeriodDto>($"api/v1/fiscal/periods/for-date?date={date:yyyy-MM-dd}", cancellationToken);

    public async Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/fiscal/years", new { fiscalYear, start }, cancellationToken)).ReadAsync<List<FiscalPeriodDto>>(cancellationToken);

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/fiscal/periods/{periodId}/status", new { status, reason }, cancellationToken)).ReadAsync<FiscalPeriodDto>(cancellationToken);
}

public sealed class HttpAuditLogService(HttpClient http) : IAuditLogService
{
    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(string? entityType = null, Guid? entityId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        var query = $"api/v1/audit?limit={limit}"
            + (entityType is null ? "" : $"&entityType={Uri.EscapeDataString(entityType)}")
            + (entityId is null ? "" : $"&entityId={entityId}");
        return await http.GetAsync(query, cancellationToken).ReadListAsync<AuditEventDto>(cancellationToken);
    }
}

internal static class HttpProjectAccountingExtensions
{
    public static async Task<IReadOnlyList<T>> ReadListAsync<T>(this Task<HttpResponseMessage> responseTask, CancellationToken cancellationToken)
    {
        using var response = await responseTask;
        return await response.ReadAsync<List<T>>(cancellationToken);
    }

    public static async Task<T?> GetOrNullAsync<T>(this HttpClient http, string url, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(url, cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.ReadAsync<T>(cancellationToken);
    }

}

public static class ProjectAccountingClientRegistration
{
    public static IServiceCollection AddProjectAccountingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPeopleService, HttpPeopleService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IContractService, HttpContractService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProjectService, HttpProjectService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IFiscalCalendarService, HttpFiscalCalendarService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAuditLogService, HttpAuditLogService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
