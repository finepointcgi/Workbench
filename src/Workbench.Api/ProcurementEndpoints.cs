using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Purchasing;

namespace Workbench.Api;

internal static class ProcurementEndpoints
{
    public static void MapProcurementEndpoints(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/purchasing").WithTags("Procurement").AddEndpointFilter(MapConflicts);

        // Requisitions
        p.MapGet("/requisitions", (RequisitionState? status, bool? mine, string? search, IRequisitionService s, CancellationToken ct) =>
            s.ListAsync(new RequisitionQuery(status, mine ?? false, search), ct));
        p.MapGet("/requisitions/options", (IRequisitionService s, CancellationToken ct) => s.GetOptionsAsync(ct));
        p.MapGet("/requisitions/approvals", (IRequisitionService s, CancellationToken ct) => s.ListApprovalQueueAsync(ct));
        p.MapGet("/requisitions/{id:guid}", (Guid id, IRequisitionService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/requisitions", (SaveRequisitionRequest r, IRequisitionService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPut("/requisitions/{id:guid}", (Guid id, SaveRequisitionRequest r, IRequisitionService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapPost("/requisitions/{id:guid}/submit", (Guid id, IRequisitionService s, CancellationToken ct) => s.SubmitAsync(id, ct));
        p.MapPost("/requisitions/{id:guid}/approve", (Guid id, RequisitionDecisionRequest r, IRequisitionService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        p.MapPost("/requisitions/{id:guid}/reject", (Guid id, RequisitionDecisionRequest r, IRequisitionService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        p.MapPost("/requisitions/{id:guid}/cancel", (Guid id, RequisitionDecisionRequest r, IRequisitionService s, CancellationToken ct) => s.CancelAsync(id, r, ct));

        // Purchase orders and receiving
        p.MapGet("/orders", (PurchaseOrderState? status, Guid? vendorId, string? search, bool? openOnly, IPurchaseOrderService s, CancellationToken ct) =>
            s.ListAsync(new PurchaseOrderQuery(status, vendorId, search, openOnly ?? false), ct));
        p.MapGet("/orders/receivable", (IPurchaseOrderService s, CancellationToken ct) => s.ListReceivableAsync(ct));
        p.MapGet("/orders/orderable-lines", (IPurchaseOrderService s, CancellationToken ct) => s.ListOrderableRequisitionLinesAsync(ct));
        p.MapGet("/orders/{id:guid}", (Guid id, IPurchaseOrderService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/orders", (SavePurchaseOrderRequest r, IPurchaseOrderService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPost("/orders/from-requisitions", (CreatePurchaseOrderFromRequisitionsRequest r, IPurchaseOrderService s, CancellationToken ct) => s.CreateFromRequisitionsAsync(r, ct));
        p.MapPut("/orders/{id:guid}", (Guid id, SavePurchaseOrderRequest r, IPurchaseOrderService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapPost("/orders/{id:guid}/issue", (Guid id, IPurchaseOrderService s, CancellationToken ct) => s.IssueAsync(id, ct));
        p.MapPost("/orders/{id:guid}/change-orders", (Guid id, ChangeOrderRequest r, IPurchaseOrderService s, CancellationToken ct) => s.ChangeOrderAsync(id, r, ct));
        p.MapPost("/orders/{id:guid}/receipts", (Guid id, ReceiveRequest r, IPurchaseOrderService s, CancellationToken ct) => s.ReceiveAsync(id, r, ct));
        p.MapPost("/orders/{id:guid}/close", (Guid id, IPurchaseOrderService s, CancellationToken ct) => s.CloseAsync(id, ct));
        p.MapPost("/orders/{id:guid}/cancel", (Guid id, PurchaseOrderReasonRequest r, IPurchaseOrderService s, CancellationToken ct) => s.CancelAsync(id, r, ct));

        // Three-way match
        p.MapGet("/match-exceptions", (bool? includeOverridden, IThreeWayMatchService s, CancellationToken ct) => s.ListExceptionsAsync(includeOverridden ?? false, ct));
        p.MapPost("/match-exceptions/{billLineId:guid}/override", (Guid billLineId, MatchOverrideRequest r, IThreeWayMatchService s, CancellationToken ct) => s.OverrideAsync(billLineId, r, ct));
        p.MapPost("/bills/from-order", (CreateBillFromPurchaseOrderRequest r, IThreeWayMatchService s, CancellationToken ct) => s.BuildBillAsync(r, ct));

        // Commitments
        p.MapGet("/commitments/projects/{projectId:guid}", (Guid projectId, ICommitmentService s, CancellationToken ct) => s.GetProjectCommitmentsAsync(projectId, ct));
    }

    /// <summary>Wrong state (already issued, received, closed) → 409.</summary>
    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
