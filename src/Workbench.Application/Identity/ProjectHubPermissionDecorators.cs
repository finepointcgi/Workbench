using Workbench.Application.Projects;
using Workbench.Contracts;
using Workbench.Contracts.Projects;

namespace Workbench.Application.Identity;

/// <summary>
/// The Projects hub: the list is scoped inside the service (signed in only here); a project page opens for Finance,
/// administrators, its manager, the people charging to it and its plan sheet's members (others get not found); its
/// team is for Finance, administrators and the project's manager.
/// </summary>
public sealed class PermissionCheckingProjectHubService(IProjectHubService inner, ProjectHubAccess hub, ProjectAccess projects) : IProjectHubService
{
    public Task<ProjectHubListDto> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        hub.Access.RequireSignedIn();
        return inner.ListProjectsAsync(cancellationToken);
    }

    public async Task<ProjectHubDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        hub.Access.RequireSignedIn();
        if (!await hub.CanSeeProjectAsync(projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        return await inner.GetProjectAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectTeamMemberDto>> GetTeamAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see who works on this project", cancellationToken);
        return await inner.GetTeamAsync(projectId, cancellationToken);
    }
}
