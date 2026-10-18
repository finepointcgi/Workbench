using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;

namespace Workbench.ApiClient;

public sealed class HttpPayGroupService(HttpClient http) : IPayGroupService
{
    private const string Base = "api/v1/payroll/pay-groups";

    public async Task<IReadOnlyList<PayGroupDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadAsync<List<PayGroupDto>>(cancellationToken);

    public async Task<PayGroupDto> GetAsync(Guid payGroupId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{payGroupId}", cancellationToken)).ReadAsync<PayGroupDto>(cancellationToken);

    public async Task<PayGroupDto> CreateAsync(SavePayGroupRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadAsync<PayGroupDto>(cancellationToken);

    public async Task<PayGroupDto> UpdateAsync(Guid payGroupId, SavePayGroupRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{payGroupId}", request, cancellationToken)).ReadAsync<PayGroupDto>(cancellationToken);

    public async Task<int> GeneratePeriodsAsync(Guid payGroupId, int year, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{payGroupId}/periods/{year}", null, cancellationToken)).ReadAsync<int>(cancellationToken);

    public async Task<IReadOnlyList<PayPeriodDto>> ListPeriodsAsync(Guid payGroupId, int? year = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/{payGroupId}/periods", ("year", year)), cancellationToken)).ReadAsync<List<PayPeriodDto>>(cancellationToken);
}

public sealed class HttpPayrollProfileService(HttpClient http) : IPayrollProfileService
{
    private const string Base = "api/v1/payroll/profiles";

    public async Task<IReadOnlyList<PayProfileDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadAsync<List<PayProfileDto>>(cancellationToken);

    public async Task<PayProfileDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{employeeId}", cancellationToken)).ReadAsync<PayProfileDto>(cancellationToken);

    public async Task<PayProfileDto> SaveAsync(Guid employeeId, SavePayProfileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{employeeId}", request, cancellationToken)).ReadAsync<PayProfileDto>(cancellationToken);

    public async Task<PayProfileDto> RevealAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{employeeId}/reveal", null, cancellationToken)).ReadAsync<PayProfileDto>(cancellationToken);
}

public sealed class HttpPayrollProviderService(HttpClient http) : IPayrollProviderService
{
    private const string Base = "api/v1/payroll/providers";

    public async Task<IReadOnlyList<PayrollProviderDto>> ListProvidersAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadAsync<List<PayrollProviderDto>>(cancellationToken);

    public async Task<PayrollProviderSettingsDto> GetSettingsAsync(string providerKey, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{Uri.EscapeDataString(providerKey)}/settings", cancellationToken)).ReadAsync<PayrollProviderSettingsDto>(cancellationToken);

    public async Task<PayrollProviderSettingsDto> SaveSettingsAsync(string providerKey, SavePayrollProviderSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{Uri.EscapeDataString(providerKey)}/settings", request, cancellationToken)).ReadAsync<PayrollProviderSettingsDto>(cancellationToken);

    public async Task<EmployeeSyncResultDto> SyncEmployeesAsync(Guid payGroupId, bool dryRun, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/payroll/pay-groups/{payGroupId}/sync-employees?dryRun={dryRun}", null, cancellationToken)).ReadAsync<EmployeeSyncResultDto>(cancellationToken);
}

public sealed class HttpPayrollRunService(HttpClient http) : IPayrollRunService
{
    private const string Base = "api/v1/payroll/runs";

    public async Task<IReadOnlyList<PayrollRunDto>> ListAsync(Guid? payGroupId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("payGroupId", payGroupId)), cancellationToken)).ReadAsync<List<PayrollRunDto>>(cancellationToken);

    public async Task<PayrollRunDto> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{runId}", cancellationToken)).ReadAsync<PayrollRunDto>(cancellationToken);

    public async Task<PayrollRunDto> StartAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, new { payGroupId, payPeriodId }, cancellationToken)).ReadAsync<PayrollRunDto>(cancellationToken);

    public async Task<HoursPreviewDto> PreviewHoursAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{runId}/hours", cancellationToken)).ReadAsync<HoursPreviewDto>(cancellationToken);

    public async Task<PayrollFileDto> ExportHoursAsync(Guid runId, ExportHoursRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{runId}/export", request, cancellationToken)).ReadAsync<PayrollFileDto>(cancellationToken);

    public async Task<RegisterImportResultDto> ImportRegisterAsync(Guid runId, ImportRegisterRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{runId}/register", request, cancellationToken)).ReadAsync<RegisterImportResultDto>(cancellationToken);

    public async Task<IReadOnlyList<RegisterLineDto>> GetRegisterAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{runId}/register", cancellationToken)).ReadAsync<List<RegisterLineDto>>(cancellationToken);

    public async Task<PayrollReconciliationDto> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{runId}/reconciliation", cancellationToken)).ReadAsync<PayrollReconciliationDto>(cancellationToken);

    public async Task<PayrollRunDto> PostAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{runId}/post", null, cancellationToken)).ReadAsync<PayrollRunDto>(cancellationToken);

    public async Task<PayrollRunDto> VoidAsync(Guid runId, VoidPayrollRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{runId}/void", request, cancellationToken)).ReadAsync<PayrollRunDto>(cancellationToken);
}

public sealed class HttpLeavePolicyService(HttpClient http) : ILeavePolicyService
{
    private const string Base = "api/v1/leave/policies";

    public async Task<IReadOnlyList<LeavePolicyDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}?includeInactive={includeInactive}", cancellationToken)).ReadAsync<List<LeavePolicyDto>>(cancellationToken);

    public async Task<IReadOnlyList<LeaveChargeCodeDto>> ListLeaveChargeCodesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/charge-codes", cancellationToken)).ReadAsync<List<LeaveChargeCodeDto>>(cancellationToken);

    public async Task<LeavePolicyDto> CreateAsync(SaveLeavePolicyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadAsync<LeavePolicyDto>(cancellationToken);

    public async Task<LeavePolicyDto> UpdateAsync(Guid policyId, SaveLeavePolicyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{policyId}", request, cancellationToken)).ReadAsync<LeavePolicyDto>(cancellationToken);
}

public sealed class HttpHolidayService(HttpClient http) : IHolidayService
{
    private const string Base = "api/v1/leave/holidays";

    public async Task<IReadOnlyList<HolidayDto>> ListAsync(int year, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{year}", cancellationToken)).ReadAsync<List<HolidayDto>>(cancellationToken);

    public async Task<HolidayDto> AddAsync(SaveHolidayRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadAsync<HolidayDto>(cancellationToken);

    public async Task RemoveAsync(Guid holidayId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{holidayId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<int> ApplyFederalTemplateAsync(int year, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{year}/federal", null, cancellationToken)).ReadAsync<int>(cancellationToken);
}

public sealed class HttpLeaveBalanceService(HttpClient http) : ILeaveBalanceService
{
    private const string Base = "api/v1/leave";

    public async Task<IReadOnlyList<LeaveBalanceDto>> GetMyBalancesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/balances/mine", cancellationToken)).ReadAsync<List<LeaveBalanceDto>>(cancellationToken);

    public async Task<IReadOnlyList<LeaveBalanceDto>> ListBalancesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/balances", cancellationToken)).ReadAsync<List<LeaveBalanceDto>>(cancellationToken);

    public async Task<IReadOnlyList<LeaveLedgerEntryDto>> GetHistoryAsync(Guid employeeId, Guid policyId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/balances/{employeeId}/{policyId}/history", cancellationToken)).ReadAsync<List<LeaveLedgerEntryDto>>(cancellationToken);

    public async Task<LeaveLedgerEntryDto> AdjustAsync(AdjustLeaveRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/balances/adjust", request, cancellationToken)).ReadAsync<LeaveLedgerEntryDto>(cancellationToken);

    public async Task<AccrualRunResultDto> RunAccrualAsync(Guid payPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/accruals/{payPeriodId}", null, cancellationToken)).ReadAsync<AccrualRunResultDto>(cancellationToken);

    public async Task<YearEndResultDto> RunYearEndAsync(int year, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/year-end/{year}", null, cancellationToken)).ReadAsync<YearEndResultDto>(cancellationToken);
}

public sealed class HttpLeaveRequestService(HttpClient http) : ILeaveRequestService
{
    private const string Base = "api/v1/leave/requests";

    public async Task<IReadOnlyList<LeaveRequestDto>> ListMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/mine", cancellationToken)).ReadAsync<List<LeaveRequestDto>>(cancellationToken);

    public async Task<LeaveRequestDto> CreateAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadAsync<LeaveRequestDto>(cancellationToken);

    public async Task<LeaveRequestDto> CancelAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{requestId}/cancel", null, cancellationToken)).ReadAsync<LeaveRequestDto>(cancellationToken);

    public async Task<bool> IsApproverAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/is-approver", cancellationToken)).ReadAsync<bool>(cancellationToken);

    public async Task<IReadOnlyList<LeaveRequestDto>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/approvals", cancellationToken)).ReadAsync<List<LeaveRequestDto>>(cancellationToken);

    public async Task<LeaveRequestDto> ApproveAsync(Guid requestId, DecideLeaveRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{requestId}/approve", request, cancellationToken)).ReadAsync<LeaveRequestDto>(cancellationToken);

    public async Task<LeaveRequestDto> RejectAsync(Guid requestId, DecideLeaveRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{requestId}/reject", request, cancellationToken)).ReadAsync<LeaveRequestDto>(cancellationToken);

    public async Task<IReadOnlyList<TeamCalendarEntryDto>> GetTeamCalendarAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/leave/calendar?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", cancellationToken)).ReadAsync<List<TeamCalendarEntryDto>>(cancellationToken);
}

public sealed class HttpLeaveTimesheetService(HttpClient http) : ILeaveTimesheetService
{
    public async Task<TimesheetLeaveDto> GetForTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/leave/timesheets/{timesheetId}", cancellationToken)).ReadAsync<TimesheetLeaveDto>(cancellationToken);
}

public static class PayrollLeaveClientRegistration
{
    public static IServiceCollection AddPayrollLeaveClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPayGroupService, HttpPayGroupService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPayrollProfileService, HttpPayrollProfileService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPayrollProviderService, HttpPayrollProviderService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPayrollRunService, HttpPayrollRunService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeavePolicyService, HttpLeavePolicyService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IHolidayService, HttpHolidayService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeaveBalanceService, HttpLeaveBalanceService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeaveRequestService, HttpLeaveRequestService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeaveTimesheetService, HttpLeaveTimesheetService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
