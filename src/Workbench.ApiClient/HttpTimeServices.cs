using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Time;

namespace Workbench.ApiClient;

public sealed class HttpTimesheetService(HttpClient http) : ITimesheetService
{
    public async Task<TimesheetDetailDto> GetOrCreateMyTimesheetAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/time/timesheets/mine?date={date:yyyy-MM-dd}", null, cancellationToken)).ReadTimeAsync<TimesheetDetailDto>(cancellationToken);

    public async Task<IReadOnlyList<TimesheetDto>> ListMyTimesheetsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/time/timesheets/mine", cancellationToken)).ReadTimeAsync<List<TimesheetDto>>(cancellationToken);

    public async Task<TimesheetDetailDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/time/timesheets/{timesheetId}", cancellationToken)).ReadTimeAsync<TimesheetDetailDto>(cancellationToken);

    public async Task<TimesheetDetailDto> SaveEntriesAsync(Guid timesheetId, SaveTimeEntriesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/time/timesheets/{timesheetId}/entries", request, cancellationToken)).ReadTimeAsync<TimesheetDetailDto>(cancellationToken);

    public async Task<SubmitTimesheetResult> SubmitAsync(Guid timesheetId, SubmitTimesheetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/time/timesheets/{timesheetId}/submit", request, cancellationToken)).ReadTimeAsync<SubmitTimesheetResult>(cancellationToken);

    public async Task<TimesheetDetailDto> ReopenAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/time/timesheets/{timesheetId}/reopen", new ReasonRequest(reason), cancellationToken)).ReadTimeAsync<TimesheetDetailDto>(cancellationToken);
}

public sealed class HttpTimesheetApprovalService(HttpClient http) : ITimesheetApprovalService
{
    public async Task<bool> IsApproverAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/time/approvals/is-approver", cancellationToken)).ReadTimeAsync<bool>(cancellationToken);

    public async Task<IReadOnlyList<TimesheetDto>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/time/approvals/pending", cancellationToken)).ReadTimeAsync<List<TimesheetDto>>(cancellationToken);

    public async Task<TimesheetDto> ApproveAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/time/approvals/{timesheetId}/approve", null, cancellationToken)).ReadTimeAsync<TimesheetDto>(cancellationToken);

    public async Task<TimesheetDto> RejectAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/time/approvals/{timesheetId}/reject", new ReasonRequest(reason), cancellationToken)).ReadTimeAsync<TimesheetDto>(cancellationToken);

    public async Task<TeamStatusDto> GetTeamStatusAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/time/team?date={date:yyyy-MM-dd}", cancellationToken)).ReadTimeAsync<TeamStatusDto>(cancellationToken);
}

public sealed class HttpTimeReportService(HttpClient http) : ITimeReportService
{
    public async Task<IReadOnlyList<HoursByChargeCodeRow>> GetHoursByChargeCodeAsync(DateOnly from, DateOnly to, bool approvedOnly = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/time/reports/hours?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&approvedOnly={approvedOnly}", cancellationToken))
            .ReadTimeAsync<List<HoursByChargeCodeRow>>(cancellationToken);

    public async Task<IReadOnlyList<DailyEntryComplianceRow>> GetDailyEntryComplianceAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/time/reports/daily-entry?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", cancellationToken))
            .ReadTimeAsync<List<DailyEntryComplianceRow>>(cancellationToken);

    public async Task<string> ExportLaborDistributionCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/time/reports/labor-distribution.csv?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", cancellationToken);
        await response.EnsureTimeOkAsync(cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

internal static class HttpTimeExtensions
{
    /// <summary>Like <see cref="ApiResponses.ReadAsync{T}"/>, plus 403 → <see cref="UnauthorizedAccessException"/> as the in-process services throw.</summary>
    public static async Task<T> ReadTimeAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            await response.EnsureTimeOkAsync(cancellationToken);
            return await response.ReadAsync<T>(cancellationToken);
        }
    }

    public static async Task EnsureTimeOkAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        await response.EnsureOkAsync(cancellationToken);
    }
}

public static class TimekeepingClientRegistration
{
    public static IServiceCollection AddTimekeepingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ITimesheetService, HttpTimesheetService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITimesheetApprovalService, HttpTimesheetApprovalService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITimesheetTimerService, HttpTimesheetTimerService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITimeReportService, HttpTimeReportService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IOrganizationSettingsService, HttpOrganizationSettingsService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
