using Workbench.Contracts.Projects;

namespace Workbench.ApiClient;

public sealed class HttpProjectHubService(HttpClient http) : IProjectHubService
{
    public async Task<ProjectHubListDto> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/project-hub", cancellationToken)).ReadAsync<ProjectHubListDto>(cancellationToken);

    public async Task<ProjectHubDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/project-hub/{projectId}", cancellationToken)).ReadAsync<ProjectHubDto>(cancellationToken);

    public async Task<IReadOnlyList<ProjectTeamMemberDto>> GetTeamAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/project-hub/{projectId}/team", cancellationToken).ReadListAsync<ProjectTeamMemberDto>(cancellationToken);
}
