using System.Net.Http.Json;
using Workbench.Contracts.Scheduling;

namespace Workbench.ApiClient;

public sealed class HttpScheduleHealthService(HttpClient http) : IScheduleHealthService
{
    public async Task<ScheduleHealthDto> GetAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/plan/health", request ?? new ScheduleHealthRequest(), cancellationToken);
        return await response.ReadAsync<ScheduleHealthDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskFloatDto>> GetFloatAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/plan/float", cancellationToken);
        return await response.ReadAsync<List<TaskFloatDto>>(cancellationToken);
    }

    public async Task<PlanFile> ExportPdfAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/plan/health/export/pdf", request ?? new ScheduleHealthRequest(), cancellationToken);
        return await response.ReadAsync<PlanFile>(cancellationToken);
    }

    public async Task<PlanFile> ExportExcelAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/plan/health/export/xlsx", request ?? new ScheduleHealthRequest(), cancellationToken);
        return await response.ReadAsync<PlanFile>(cancellationToken);
    }
}
