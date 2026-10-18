using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Projects;

namespace Workbench.Api;

internal static class ProjectHubEndpoints
{
    public static void MapProjectHubEndpoints(this RouteGroupBuilder v1)
    {
        var hub = v1.MapGroup("/project-hub").WithTags("Projects hub");
        hub.MapGet("", (IProjectHubService s, CancellationToken ct) => s.ListProjectsAsync(ct));
        hub.MapGet("/{id:guid}", (Guid id, IProjectHubService s, CancellationToken ct) => s.GetProjectAsync(id, ct));
        hub.MapGet("/{id:guid}/team", (Guid id, IProjectHubService s, CancellationToken ct) => s.GetTeamAsync(id, ct));
    }
}
