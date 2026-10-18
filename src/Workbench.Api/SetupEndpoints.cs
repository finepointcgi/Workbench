using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Api;

internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this RouteGroupBuilder v1)
    {
        var plan = v1.MapGroup("/projects/{id:guid}/plan").WithTags("Plan sync");
        plan.MapPost("", (Guid id, IPlanSyncService s, CancellationToken ct) => s.CreatePlanSheetAsync(id, ct));
        plan.MapGet("/diff", (Guid id, IPlanSyncService s, CancellationToken ct) => s.DiffAsync(id, ct));
        plan.MapPost("/apply", (Guid id, ApplyPlanSyncRequest r, IPlanSyncService s, CancellationToken ct) => s.ApplyAsync(id, r, ct));

        var setup = v1.MapGroup("/setup").WithTags("Setup");
        setup.MapPut("/org-units/{id:guid}", (Guid id, CreateOrgUnitRequest r, ISetupAdminService s, CancellationToken ct) => s.UpdateOrgUnitAsync(id, r, ct));
        setup.MapPut("/customers/{id:guid}", (Guid id, SaveCustomerRequest r, ISetupAdminService s, CancellationToken ct) => s.UpdateCustomerAsync(id, r, ct));
        setup.MapGet("/audit", (string? entityType, Guid? entityId, DateOnly? from, DateOnly? to, int? limit, ISetupAdminService s, CancellationToken ct) =>
            s.SearchAuditAsync(new AuditQuery(entityType, entityId, from, to, limit ?? 500), ct));
        setup.MapGet("/audit/entity-types", (ISetupAdminService s, CancellationToken ct) => s.ListAuditEntityTypesAsync(ct));
        setup.MapGet("/checklist", (ISetupAdminService s, CancellationToken ct) => s.GetChecklistAsync(ct));
    }
}
