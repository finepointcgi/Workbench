using System.Net.Http.Json;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;

namespace Workbench.ApiClient;

public sealed class HttpTaskAssignmentService(HttpClient http) : ITaskAssignmentService
{
    public async Task<SheetTasksDto> GetSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/tasks", cancellationToken);
        return await response.ReadAsync<SheetTasksDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskAssigneeDto>> SetAssigneesAsync(Guid sheetId, Guid itemId, SetAssigneesRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/tasks/{itemId}/assignees", request, cancellationToken);
        return await response.ReadAsync<List<TaskAssigneeDto>>(cancellationToken);
    }

    public async Task<ItemDto> UpdateStatusAsync(Guid sheetId, Guid itemId, UpdateTaskStatusRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/tasks/{itemId}/status", request, cancellationToken);
        return await response.ReadAsync<ItemDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<MyTaskDto>> ListMineAsync(bool includeDone = false, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/my-tasks?includeDone={(includeDone ? "true" : "false")}", cancellationToken);
        return await response.ReadAsync<List<MyTaskDto>>(cancellationToken);
    }

    public async Task<ProjectWorkloadDto> GetProjectWorkloadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/projects/{projectId}/workload", cancellationToken);
        return await response.ReadAsync<ProjectWorkloadDto>(cancellationToken);
    }
}
