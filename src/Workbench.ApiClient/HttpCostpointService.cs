using Workbench.Contracts.Costpoint;

namespace Workbench.ApiClient;

public sealed class HttpCostpointService(HttpClient http) : ICostpointService
{
    public async Task<CostpointStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/costpoint/status", cancellationToken);
        return await response.ReadAsync<CostpointStatusDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<CostpointSyncRunDto>> ListSyncRunsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/costpoint/sync-runs?take={take}", cancellationToken);
        return await response.ReadAsync<List<CostpointSyncRunDto>>(cancellationToken);
    }

    public async Task<CostpointSyncRunDto> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/v1/costpoint/sync", null, cancellationToken);
        return await response.ReadAsync<CostpointSyncRunDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<CostpointProjectDto>> SearchProjectsAsync(string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/costpoint/projects?q={Uri.EscapeDataString(query ?? "")}&take={take}", cancellationToken);
        return await response.ReadAsync<List<CostpointProjectDto>>(cancellationToken);
    }

    public async Task<IReadOnlyList<CostpointWbsDto>> SearchWbsAsync(string? projectId, string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        var url = $"api/v1/costpoint/wbs?projectId={Uri.EscapeDataString(projectId ?? "")}&q={Uri.EscapeDataString(query ?? "")}&take={take}";
        using var response = await http.GetAsync(url, cancellationToken);
        return await response.ReadAsync<List<CostpointWbsDto>>(cancellationToken);
    }

    public async Task<PlanVsActualDto> GetPlanVsActualAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/plan-vs-actual", cancellationToken);
        return await response.ReadAsync<PlanVsActualDto>(cancellationToken);
    }
}
