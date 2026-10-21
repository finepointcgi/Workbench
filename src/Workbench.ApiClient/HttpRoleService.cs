using System.Net.Http.Json;
using Workbench.Contracts.Identity;

namespace Workbench.ApiClient;

public sealed class HttpRoleService(HttpClient http) : IRoleService
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/admin/roles", cancellationToken)).ReadAsync<List<RoleDto>>(cancellationToken);

    public async Task<IReadOnlyList<RoleRef>> ListNamesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/admin/roles/names", cancellationToken)).ReadAsync<List<RoleRef>>(cancellationToken);

    public async Task<RoleDto> GetAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/admin/roles/{roleId}", cancellationToken)).ReadAsync<RoleDto>(cancellationToken);

    public async Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/admin/roles", request, cancellationToken)).ReadAsync<RoleDto>(cancellationToken);

    public async Task<RoleDto> UpdateAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/admin/roles/{roleId}", request, cancellationToken)).ReadAsync<RoleDto>(cancellationToken);

    public async Task<RoleDto> CloneAsync(Guid roleId, string name, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/admin/roles/{roleId}/clone", new { Name = name }, cancellationToken)).ReadAsync<RoleDto>(cancellationToken);

    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/admin/roles/{roleId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<IReadOnlyList<UserRolesDto>> ListMembersAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/admin/roles/{roleId}/members", cancellationToken)).ReadAsync<List<UserRolesDto>>(cancellationToken);

    public async Task<UserRolesDto> GetUserRolesAsync(string email, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/admin/user-roles/{Uri.EscapeDataString(email)}", cancellationToken)).ReadAsync<UserRolesDto>(cancellationToken);

    public async Task<UserRolesDto> SetUserRolesAsync(string email, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/admin/user-roles/{Uri.EscapeDataString(email)}", new { RoleIds = roleIds }, cancellationToken)).ReadAsync<UserRolesDto>(cancellationToken);
}
