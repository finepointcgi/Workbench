using System.Net.Http.Json;
using Workbench.Contracts.Scheduling;

namespace Workbench.ApiClient;

public sealed class HttpDependencyService(HttpClient http) : IDependencyService
{
    public async Task<IReadOnlyList<DependencyDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/dependencies", cancellationToken);
        return await response.ReadAsync<List<DependencyDto>>(cancellationToken);
    }

    public async Task<DependencyDto> AddAsync(Guid sheetId, CreateDependencyRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/dependencies", request, cancellationToken);
        return await response.ReadAsync<DependencyDto>(cancellationToken);
    }

    public async Task RemoveAsync(Guid sheetId, Guid dependencyId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/dependencies/{dependencyId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<ScheduleAnalysis> AnalyzeAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/schedule", cancellationToken);
        return await response.ReadAsync<ScheduleAnalysis>(cancellationToken);
    }
}
