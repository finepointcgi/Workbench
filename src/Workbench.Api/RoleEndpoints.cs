using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Identity;

namespace Workbench.Api;

/// <summary>Admin › Roles: custom roles, their permissions and who holds them. The permission catalog itself is in the contracts.</summary>
internal static class RoleEndpoints
{
    public static void MapRoleEndpoints(this RouteGroupBuilder v1)
    {
        var roles = v1.MapGroup("/admin/roles").WithTags("Roles");
        roles.MapGet("/", (IRoleService s, CancellationToken ct) => s.ListAsync(ct));
        roles.MapGet("/names", (IRoleService s, CancellationToken ct) => s.ListNamesAsync(ct));
        roles.MapGet("/{id:guid}", (Guid id, IRoleService s, CancellationToken ct) => s.GetAsync(id, ct));
        roles.MapPost("/", (SaveRoleRequest r, IRoleService s, CancellationToken ct) => s.CreateAsync(r, ct));
        roles.MapPut("/{id:guid}", (Guid id, SaveRoleRequest r, IRoleService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        roles.MapPost("/{id:guid}/clone", (Guid id, CloneRoleBody r, IRoleService s, CancellationToken ct) => s.CloneAsync(id, r.Name, ct));
        roles.MapDelete("/{id:guid}", (Guid id, IRoleService s, CancellationToken ct) => s.DeleteAsync(id, ct));
        roles.MapGet("/{id:guid}/members", (Guid id, IRoleService s, CancellationToken ct) => s.ListMembersAsync(id, ct));

        var users = v1.MapGroup("/admin/user-roles").WithTags("Roles");
        users.MapGet("/{email}", (string email, IRoleService s, CancellationToken ct) => s.GetUserRolesAsync(email, ct));
        users.MapPut("/{email}", (string email, SetUserRolesBody r, IRoleService s, CancellationToken ct) => s.SetUserRolesAsync(email, r.RoleIds ?? [], ct));
        users.MapPut("/{email}/assignments", (string email, SetAssignmentsBody r, IRoleService s, CancellationToken ct) => s.SetUserRoleAssignmentsAsync(email, r.Assignments ?? [], ct));
        users.MapGet("/scope-options", (IRoleService s, CancellationToken ct) => s.GetScopeOptionsAsync(ct));

        // Read only: registering run-time permissions is for features' own services, not the API.
        var permissions = v1.MapGroup("/admin/permissions").WithTags("Roles");
        permissions.MapGet("/", (IPermissionCatalog c, CancellationToken ct) => c.ListAsync(ct));
    }

    internal sealed record CloneRoleBody(string Name);

    internal sealed record SetUserRolesBody(IReadOnlyList<Guid>? RoleIds);

    internal sealed record SetAssignmentsBody(IReadOnlyList<RoleAssignment>? Assignments);
}
