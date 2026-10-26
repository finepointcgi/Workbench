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

        // Starting a project in one step (project, WBS and plan sheet).
        var start = v1.MapGroup("/project-start").WithTags("Plan sync");
        start.MapGet("/templates", (IProjectStartService s, CancellationToken ct) => s.ListTemplatesAsync(ct));
        start.MapGet("/suggested-code", async (IProjectStartService s, CancellationToken ct) => new SuggestedProjectCode(await s.SuggestCodeAsync(ct)));
        start.MapPost("", (StartProjectRequest r, IProjectStartService s, CancellationToken ct) => s.StartAsync(r, ct));
        start.MapPost("/{projectId:guid}/finish", (Guid projectId, IProjectStartService s, CancellationToken ct) => s.FinishSetupAsync(projectId, ct));
        start.MapPost("/check-import", (Workbench.Contracts.Scheduling.PlanImportRequest r, IProjectStartService s, CancellationToken ct) => s.CheckImportAsync(r, ct));

        // The project's plan with Finance: ceiling-first terms, submit, approve or return.
        var plans = v1.MapGroup("/project-plans").WithTags("Project plans");
        plans.MapGet("/{projectId:guid}", (Guid projectId, IProjectPlanService s, CancellationToken ct) => s.GetAsync(projectId, ct));
        plans.MapGet("/from-contract/{contractId:guid}", (Guid contractId, IProjectPlanService s, CancellationToken ct) => s.TermsFromContractAsync(contractId, ct));
        plans.MapPut("/{projectId:guid}/terms", (Guid projectId, SaveProjectTermsRequest r, IProjectPlanService s, CancellationToken ct) => s.SaveTermsAsync(projectId, r, ct));
        plans.MapPut("/{projectId:guid}/categories", (Guid projectId, SaveProjectCategoriesRequest r, IProjectPlanService s, CancellationToken ct) => s.SaveCategoriesAsync(projectId, r.LaborCategoryIds, ct));
        plans.MapPost("/{projectId:guid}/submit", (Guid projectId, PlanReviewRequest r, IProjectPlanService s, CancellationToken ct) => s.SubmitAsync(projectId, r.Comment, ct));
        plans.MapPost("/{projectId:guid}/approve", (Guid projectId, PlanReviewRequest r, IProjectPlanService s, CancellationToken ct) => s.ApproveAsync(projectId, r.Comment, ct));
        plans.MapPost("/{projectId:guid}/return", (Guid projectId, PlanReviewRequest r, IProjectPlanService s, CancellationToken ct) => s.ReturnAsync(projectId, r.Comment ?? "", ct));

        var setup = v1.MapGroup("/setup").WithTags("Setup");
        setup.MapPut("/org-units/{id:guid}", (Guid id, CreateOrgUnitRequest r, ISetupAdminService s, CancellationToken ct) => s.UpdateOrgUnitAsync(id, r, ct));
        setup.MapPut("/customers/{id:guid}", (Guid id, SaveCustomerRequest r, ISetupAdminService s, CancellationToken ct) => s.UpdateCustomerAsync(id, r, ct));
        setup.MapGet("/audit", (string? entityType, Guid? entityId, DateOnly? from, DateOnly? to, int? limit, ISetupAdminService s, CancellationToken ct) =>
            s.SearchAuditAsync(new AuditQuery(entityType, entityId, from, to, limit ?? 500), ct));
        setup.MapGet("/audit/entity-types", (ISetupAdminService s, CancellationToken ct) => s.ListAuditEntityTypesAsync(ct));
        setup.MapGet("/checklist", (ISetupAdminService s, CancellationToken ct) => s.GetChecklistAsync(ct));
    }
}

/// <summary>Response of <c>GET /project-start/suggested-code</c>.</summary>
public sealed record SuggestedProjectCode(string Code);
