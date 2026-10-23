using System.Net.Http.Json;
using Workbench.Contracts.Scheduling;

namespace Workbench.ApiClient;

public sealed class HttpBaselineService(HttpClient http) : IBaselineService
{
    public async Task<BaselineSummary> CaptureAsync(Guid sheetId, CaptureBaselineRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/baselines", request, cancellationToken);
        return await response.ReadAsync<BaselineSummary>(cancellationToken);
    }

    public async Task<IReadOnlyList<BaselineSummary>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/baselines", cancellationToken);
        return await response.ReadAsync<List<BaselineSummary>>(cancellationToken);
    }

    public async Task<BaselineDetail> GetAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/baselines/{baselineId}", cancellationToken);
        return await response.ReadAsync<BaselineDetail>(cancellationToken);
    }

    public async Task<BaselineComparison> CompareAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/baselines/{baselineId}/variance", cancellationToken);
        return await response.ReadAsync<BaselineComparison>(cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/baselines/{baselineId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }
}
