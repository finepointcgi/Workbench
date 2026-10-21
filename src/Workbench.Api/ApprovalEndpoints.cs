using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Approvals;

namespace Workbench.Api;

/// <summary>Approval workflows: the inbox (everyone), delegations (everyone, admins for others) and setup (Finance reads, Admin changes).</summary>
internal static class ApprovalEndpoints
{
    internal static void MapApprovalEndpoints(this RouteGroupBuilder v1)
    {
        var inbox = v1.MapGroup("/approvals");
        inbox.MapGet("/inbox", (IApprovalInboxService s, CancellationToken ct) => s.ListMineAsync(ct));
        inbox.MapGet("/inbox/count", (IApprovalInboxService s, CancellationToken ct) => s.CountMineAsync(ct));
        inbox.MapPost("/{documentType}/{documentId:guid}/decision", async (string documentType, Guid documentId, ApprovalDecisionRequest request, IApprovalInboxService s, CancellationToken ct) =>
        {
            await s.DecideAsync(documentType, documentId, request, ct);
            return Results.NoContent();
        });
        inbox.MapGet("/{documentType}/{documentId:guid}/history", (string documentType, Guid documentId, IApprovalInboxService s, CancellationToken ct) =>
            s.GetHistoryAsync(documentType, documentId, ct));

        var delegations = v1.MapGroup("/approval-delegations");
        delegations.MapGet("", (IApprovalDelegationService s, CancellationToken ct) => s.ListAsync(ct));
        delegations.MapPost("", (SaveApprovalDelegationRequest request, IApprovalDelegationService s, CancellationToken ct) => s.CreateAsync(request, ct));
        delegations.MapPost("/{id:guid}/revoke", (Guid id, IApprovalDelegationService s, CancellationToken ct) => s.RevokeAsync(id, ct));

        var workflows = v1.MapGroup("/approval-workflows");
        workflows.MapGet("", (IApprovalWorkflowService s, CancellationToken ct) => s.ListAsync(ct));
        workflows.MapPost("/record", (CreateRecordApprovalWorkflowRequest request, IApprovalWorkflowService s, CancellationToken ct) => s.CreateRecordWorkflowAsync(request, ct));
        workflows.MapGet("/org-unit-approvers", (IApprovalWorkflowService s, CancellationToken ct) => s.ListOrgUnitApproversAsync(ct));
        workflows.MapPut("/org-unit-approvers/{orgUnitId:guid}", (Guid orgUnitId, SetOrgUnitApproverRequest request, IApprovalWorkflowService s, CancellationToken ct) =>
            s.SetOrgUnitApproverAsync(orgUnitId, request.EmployeeId, ct));
        workflows.MapGet("/{documentType}", (string documentType, IApprovalWorkflowService s, CancellationToken ct) => s.GetAsync(documentType, ct));
        workflows.MapGet("/{documentType}/versions/{version:int}", (string documentType, int version, IApprovalWorkflowService s, CancellationToken ct) =>
            s.GetVersionAsync(documentType, version, ct));
        workflows.MapPut("/{documentType}", (string documentType, SaveApprovalWorkflowRequest request, IApprovalWorkflowService s, CancellationToken ct) =>
            s.SaveAsync(documentType, request, ct));
        workflows.MapPost("/{documentType}/reset", (string documentType, IApprovalWorkflowService s, CancellationToken ct) => s.ResetToDefaultAsync(documentType, ct));
    }
}
