using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.Identity;

namespace Workbench.ApiClient.Identity;

public sealed class HttpUserDirectory(HttpClient http) : IUserDirectory
{
    public async Task<UserDto> GetMeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/users/me", cancellationToken);
        return await response.ReadAsync<UserDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<UserDto>> SearchAsync(string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/users?query={Uri.EscapeDataString(query ?? string.Empty)}&take={take}", cancellationToken);
        return await response.ReadAsync<List<UserDto>>(cancellationToken);
    }

    public async Task<UserDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/users/{Uri.EscapeDataString(email)}/roles", new SetRolesBody(roles), cancellationToken);
        return await response.ReadAsync<UserDto>(cancellationToken);
    }

    private sealed record SetRolesBody(OrgRoles Roles);
}

public sealed class HttpCurrentUserContext(HttpClient http) : ICurrentUserContext
{
    public async Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/users/me/context", cancellationToken);
        return await response.ReadAsync<CurrentUserInfo>(cancellationToken);
    }
}

public sealed class HttpSheetPermissions(HttpClient http) : ISheetPermissions
{
    public async Task<WorkspaceRole?> GetMyRoleAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/my-role", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await response.ReadAsync<MyRole>(cancellationToken)).Role;
    }

    private sealed record MyRole(WorkspaceRole Role);
}

public sealed class HttpWorkspaceMembershipService(HttpClient http) : IWorkspaceMembershipService
{
    public async Task<IReadOnlyList<WorkspaceSummary>> ListMyWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/workspaces", cancellationToken);
        return await response.ReadAsync<List<WorkspaceSummary>>(cancellationToken);
    }

    public async Task<WorkspaceSummary> GetForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/workspace", cancellationToken);
        return await response.ReadAsync<WorkspaceSummary>(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceMemberDto>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/workspaces/{workspaceId}/members", cancellationToken);
        return await response.ReadAsync<List<WorkspaceMemberDto>>(cancellationToken);
    }

    public async Task<WorkspaceMemberDto> SetMemberAsync(Guid workspaceId, SetWorkspaceMemberRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/workspaces/{workspaceId}/members", request, cancellationToken);
        return await response.ReadAsync<WorkspaceMemberDto>(cancellationToken);
    }

    public async Task RemoveMemberAsync(Guid workspaceId, string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/workspaces/{workspaceId}/members/{Uri.EscapeDataString(email)}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }
}
