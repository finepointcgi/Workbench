using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;

namespace Workbench.Api;

/// <summary>User administration, invites, access reviews, the security event log and retention (admins only).</summary>
internal static class SecurityAdminEndpoints
{
    public static void MapSecurityAdminEndpoints(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/admin").WithTags("Security administration");

        var users = admin.MapGroup("/users");
        users.MapGet("/", (string? query, bool? includeInactive, IUserAdminService s, CancellationToken ct) => s.ListUsersAsync(query, includeInactive ?? true, ct));
        users.MapPut("/{email}/roles", (string email, SetRolesRequest r, IUserAdminService s, CancellationToken ct) => s.SetRolesAsync(email, r.Roles, ct));
        users.MapPost("/{email}/deactivate", (string email, ReasonBody? r, IUserAdminService s, CancellationToken ct) => s.DeactivateAsync(email, r?.Reason, ct));
        users.MapPost("/{email}/reactivate", (string email, IUserAdminService s, CancellationToken ct) => s.ReactivateAsync(email, ct));
        users.MapPost("/{email}/sign-out-everywhere", (string email, IUserAdminService s, CancellationToken ct) => s.SignOutEverywhereAsync(email, ct));
        users.MapPost("/{email}/reset-mfa", (string email, IUserAdminService s, CancellationToken ct) => s.ResetMfaAsync(email, ct));
        users.MapPost("/{email}/password-reset", (string email, IUserAdminService s, CancellationToken ct) => s.SendPasswordResetAsync(email, ct));
        users.MapPut("/{email}/employee", (string email, EmployeeLinkBody r, IUserAdminService s, CancellationToken ct) => s.SetEmployeeLinkAsync(email, r.Mode, r.EmployeeId, ct));
        admin.MapGet("/linkable-employees", (IUserAdminService s, CancellationToken ct) => s.ListLinkableEmployeesAsync(ct));

        var invites = admin.MapGroup("/invites");
        invites.MapGet("/", (IInviteService s, CancellationToken ct) => s.ListAsync(ct));
        invites.MapPost("/", (CreateInviteRequest r, IInviteService s, CancellationToken ct) => s.CreateAsync(r, ct));
        invites.MapPost("/{id:guid}/revoke", (Guid id, IInviteService s, CancellationToken ct) => s.RevokeAsync(id, ct));

        var reviews = admin.MapGroup("/access-reviews");
        reviews.MapGet("/", (IAccessReviewService s, CancellationToken ct) => s.ListAsync(ct));
        reviews.MapGet("/{id:guid}", (Guid id, IAccessReviewService s, CancellationToken ct) => s.GetAsync(id, ct));
        reviews.MapPost("/", (StartReviewBody? r, IAccessReviewService s, CancellationToken ct) => s.StartAsync(r?.Name, ct));
        reviews.MapPut("/{id:guid}/items/{itemId:guid}", (Guid id, Guid itemId, AccessReviewDecisionRequest r, IAccessReviewService s, CancellationToken ct) =>
            s.DecideAsync(id, itemId, r, ct));
        reviews.MapPost("/{id:guid}/complete", (Guid id, CompleteReviewBody? r, IAccessReviewService s, CancellationToken ct) => s.CompleteAsync(id, r?.Notes, ct));

        var events = admin.MapGroup("/security-events");
        events.MapPost("/search", (SecurityEventQuery q, ISecurityEventService s, CancellationToken ct) => s.SearchAsync(q, ct));
        events.MapGet("/types", (ISecurityEventService s, CancellationToken ct) => s.ListEventTypesAsync(ct));
        events.MapPost("/export", (SecurityEventQuery q, ISecurityEventService s, CancellationToken ct) => s.ExportCsvAsync(q, ct));
        events.MapPost("/export.csv", async (SecurityEventQuery q, ISecurityEventService s, CancellationToken ct) =>
        {
            var file = await s.ExportCsvAsync(q, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
        });

        var retention = admin.MapGroup("/retention");
        retention.MapGet("/policies", (IRetentionService s, CancellationToken ct) => s.ListPoliciesAsync(ct));
        retention.MapPut("/policies/{recordType}", (string recordType, RetentionDaysBody r, IRetentionService s, CancellationToken ct) => s.SetPolicyAsync(recordType, r.RetentionDays, ct));
        retention.MapGet("/preview", (IRetentionService s, CancellationToken ct) => s.PreviewAsync(ct));
        retention.MapPost("/run", (IRetentionService s, CancellationToken ct) => s.RunNowAsync(ct));
        retention.MapGet("/holds", (bool? includeReleased, IRetentionService s, CancellationToken ct) => s.ListHoldsAsync(includeReleased ?? false, ct));
        retention.MapGet("/holdable", (IRetentionService s, CancellationToken ct) => s.ListHoldableAsync(ct));
        retention.MapPost("/holds", (PlaceLegalHoldRequest r, IRetentionService s, CancellationToken ct) => s.PlaceHoldAsync(r, ct));
        retention.MapPost("/holds/{id:guid}/release", (Guid id, ReasonBody r, IRetentionService s, CancellationToken ct) => s.ReleaseHoldAsync(id, r.Reason ?? "", ct));
        retention.MapGet("/holds/check", (string entityType, Guid entityId, IRetentionService s, CancellationToken ct) => s.IsOnHoldAsync(entityType, entityId, ct));
    }
}

public sealed record ReasonBody(string? Reason);

public sealed record EmployeeLinkBody(EmployeeLinkMode Mode, Guid? EmployeeId);

public sealed record StartReviewBody(string? Name);

public sealed record CompleteReviewBody(string? Notes);

public sealed record RetentionDaysBody(int RetentionDays);
