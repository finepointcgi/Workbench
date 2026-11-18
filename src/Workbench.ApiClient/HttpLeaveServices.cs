using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Leave;

namespace Workbench.ApiClient;

public sealed class HttpPayGroupChoiceService(HttpClient http) : IPayGroupChoiceService
{
    public async Task<IReadOnlyList<PayGroupChoiceDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/leave/pay-groups", cancellationToken).ReadListAsync<PayGroupChoiceDto>(cancellationToken);
}

public sealed class HttpLeavePolicyService(HttpClient http) : ILeavePolicyService
{
    private const string Base = "api/v1/leave/policies";

    public async Task<IReadOnlyList<LeavePolicyDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}?includeInactive={includeInactive}", cancellationToken)).ReadAsync<List<LeavePolicyDto>>(cancellationToken);

    public async Task<IReadOnlyList<LeaveChargeCodeDto>> ListLeaveChargeCodesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/charge-codes", cancellationToken)).ReadAsync<List<LeaveChargeCodeDto>>(cancellationToken);

    public async Task<IReadOnlyList<LeavePolicyDto>> ListMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/mine", cancellationToken)).ReadAsync<List<LeavePolicyDto>>(cancellationToken);

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

public static class LeaveClientRegistration
{
    public static IServiceCollection AddLeaveClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPayGroupChoiceService, HttpPayGroupChoiceService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeavePolicyService, HttpLeavePolicyService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IHolidayService, HttpHolidayService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeaveBalanceService, HttpLeaveBalanceService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeaveRequestService, HttpLeaveRequestService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILeaveTimesheetService, HttpLeaveTimesheetService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
