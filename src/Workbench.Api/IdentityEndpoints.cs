using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Identity;

namespace Workbench.Api;

internal static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this RouteGroupBuilder v1)
    {
        var users = v1.MapGroup("/users").WithTags("Users");
        users.MapGet("/me", (IUserDirectory directory, CancellationToken ct) => directory.GetMeAsync(ct));
        users.MapGet("/", (string? query, int? take, IUserDirectory directory, CancellationToken ct) => directory.SearchAsync(query, take ?? 20, ct));
        users.MapPut("/{email}/admin", (string email, SetAdminRequest request, IUserDirectory directory, CancellationToken ct) =>
            directory.SetAdminAsync(email, request.IsAdmin, ct));

        v1.MapGet("/sheets/{sheetId:guid}/my-role", async (Guid sheetId, ISheetPermissions permissions, CancellationToken ct) =>
            await permissions.GetMyRoleAsync(sheetId, ct) is { } role ? Results.Ok(new MyRoleResponse(role)) : Results.NotFound()).WithTags("Sheets");
        v1.MapGet("/sheets/{sheetId:guid}/workspace", (Guid sheetId, IWorkspaceMembershipService service, CancellationToken ct) =>
            service.GetForSheetAsync(sheetId, ct)).WithTags("Workspaces");

        var workspaces = v1.MapGroup("/workspaces").WithTags("Workspaces");
        workspaces.MapGet("/", (IWorkspaceMembershipService service, CancellationToken ct) => service.ListMyWorkspacesAsync(ct));
        workspaces.MapGet("/{workspaceId:guid}/members", (Guid workspaceId, IWorkspaceMembershipService service, CancellationToken ct) =>
            service.ListMembersAsync(workspaceId, ct));
        workspaces.MapPut("/{workspaceId:guid}/members", (Guid workspaceId, SetWorkspaceMemberRequest request, IWorkspaceMembershipService service, CancellationToken ct) =>
            service.SetMemberAsync(workspaceId, request, ct));
        workspaces.MapDelete("/{workspaceId:guid}/members/{email}", async (Guid workspaceId, string email, IWorkspaceMembershipService service, CancellationToken ct) =>
        {
            await service.RemoveMemberAsync(workspaceId, email, ct);
            return Results.NoContent();
        });
    }
}

/// <summary>Body of <c>PUT /api/v1/users/{email}/admin</c>.</summary>
public sealed record SetAdminRequest(bool IsAdmin);

/// <summary>Response of <c>GET /api/v1/sheets/{id}/my-role</c>; 404 when the user can't see the sheet.</summary>
public sealed record MyRoleResponse(WorkspaceRole Role);
