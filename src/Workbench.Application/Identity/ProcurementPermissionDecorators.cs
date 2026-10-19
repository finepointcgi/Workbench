using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Identity;

/// <summary>
/// Procure-to-pay permissions. Requisitions: any signed-in employee, for projects they manage or are assigned to and their
/// own organization (Finance and Admin for anything); a requisition is visible to its requester, its routed project
/// managers and Finance. Purchase orders, the three-way match: Finance or Admin, except that the receivers named on an
/// order can see and receive it. Commitments: Finance, Admin or the project's manager.
/// </summary>
public static class ProcurementPermissionRegistration
{
    public static IServiceCollection AddWorkbenchProcurementPermissions(this IServiceCollection services)
    {
        services.Decorate<IRequisitionService, PermissionCheckingRequisitionService>();
        services.Decorate<IPurchaseOrderService, PermissionCheckingPurchaseOrderService>();
        services.Decorate<IThreeWayMatchService, PermissionCheckingThreeWayMatchService>();
        services.Decorate<ICommitmentService, PermissionCheckingCommitmentService>();
        return services;
    }
}

public sealed class PermissionCheckingRequisitionService(IRequisitionService inner, AccessControl access, RequisitionAccess requisitions, IWorkbenchDbContextFactory dbFactory)
    : IRequisitionService
{
    /// <summary>Finance, the requester and the project managers it was routed to see it; to anyone else it doesn't exist.</summary>
    private async Task RequireVisibleAsync(Guid requisitionId, CancellationToken ct)
    {
        access.RequireSignedIn();
        if ((await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            return;
        }

        var me = access.Email;
        await using var db = await dbFactory.CreateAsync(ct);
        var visible = await db.Set<PurchaseRequisition>().AsNoTracking()
            .AnyAsync(r => r.Id == requisitionId && (r.RequesterEmail == me || r.Lines.Any(l => l.ApproverEmail == me)), ct);
        if (!visible)
        {
            throw WorkbenchNotFoundException.For("Requisition", requisitionId);
        }
    }

    public Task<IReadOnlyList<RequisitionSummaryDto>> ListAsync(RequisitionQuery query, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListAsync(query, cancellationToken);
    }

    public async Task<RequisitionDto> GetAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        await RequireVisibleAsync(requisitionId, cancellationToken);
        return await inner.GetAsync(requisitionId, cancellationToken);
    }

    public Task<RequisitionOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetOptionsAsync(cancellationToken);
    }

    public async Task<RequisitionDto> CreateAsync(SaveRequisitionRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await requisitions.RequireChargesAsync(request, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<RequisitionDto> UpdateAsync(Guid requisitionId, SaveRequisitionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireVisibleAsync(requisitionId, cancellationToken);
        await requisitions.RequireChargesAsync(request, cancellationToken);
        return await inner.UpdateAsync(requisitionId, request, cancellationToken);
    }

    public async Task<RequisitionDto> SubmitAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        await RequireVisibleAsync(requisitionId, cancellationToken);
        return await inner.SubmitAsync(requisitionId, cancellationToken);
    }

    public Task<IReadOnlyList<RequisitionSummaryDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListApprovalQueueAsync(cancellationToken);
    }

    public async Task<RequisitionDto> ApproveAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireVisibleAsync(requisitionId, cancellationToken);
        return await inner.ApproveAsync(requisitionId, request, cancellationToken);
    }

    public async Task<RequisitionDto> RejectAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireVisibleAsync(requisitionId, cancellationToken);
        return await inner.RejectAsync(requisitionId, request, cancellationToken);
    }

    public async Task<RequisitionDto> CancelAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireVisibleAsync(requisitionId, cancellationToken);
        return await inner.CancelAsync(requisitionId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingPurchaseOrderService(IPurchaseOrderService inner, AccessControl access, IWorkbenchDbContextFactory dbFactory) : IPurchaseOrderService
{
    private const string What = "work with purchase orders";

    private Task RequireFinanceAsync(CancellationToken ct) => access.RequireFinanceAsync(What, ct);

    /// <summary>Finance, Materials (receives into inventory), or a receiver named on the order; to anyone else it doesn't exist.</summary>
    private async Task RequireReceiverAsync(Guid purchaseOrderId, CancellationToken ct)
    {
        access.RequireSignedIn();
        if ((await access.GetOrgRolesAsync(ct)) is var roles && (roles.IsFinance() || roles.IsMaterials()))
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var receivers = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.Id == purchaseOrderId).Select(o => o.ReceiverEmails).FirstOrDefaultAsync(ct);
        if (!ProcurementSupport.SplitEmails(receivers).Contains(access.Email))
        {
            throw WorkbenchNotFoundException.For("Purchase order", purchaseOrderId);
        }
    }

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(PurchaseOrderQuery query, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<PurchaseOrderDto> GetAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await RequireReceiverAsync(purchaseOrderId, cancellationToken);
        return await inner.GetAsync(purchaseOrderId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderableRequisitionLineDto>> ListOrderableRequisitionLinesAsync(CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListOrderableRequisitionLinesAsync(cancellationToken);
    }

    public async Task<PurchaseOrderDto> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<PurchaseOrderDto> CreateFromRequisitionsAsync(CreatePurchaseOrderFromRequisitionsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.CreateFromRequisitionsAsync(request, cancellationToken);
    }

    public async Task<PurchaseOrderDto> UpdateAsync(Guid purchaseOrderId, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.UpdateAsync(purchaseOrderId, request, cancellationToken);
    }

    public async Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.IssueAsync(purchaseOrderId, cancellationToken);
    }

    public async Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ChangeOrderAsync(purchaseOrderId, request, cancellationToken);
    }

    public async Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default)
    {
        await RequireReceiverAsync(purchaseOrderId, cancellationToken);
        return await inner.ReceiveAsync(purchaseOrderId, request, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListReceivableAsync(cancellationToken);
    }

    public async Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.CloseAsync(purchaseOrderId, cancellationToken);
    }

    public async Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.CancelAsync(purchaseOrderId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingThreeWayMatchService(IThreeWayMatchService inner, AccessControl access) : IThreeWayMatchService
{
    private const string What = "work with the three-way match";

    public async Task<IReadOnlyList<MatchExceptionDto>> ListExceptionsAsync(bool includeOverridden = false, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListExceptionsAsync(includeOverridden, cancellationToken);
    }

    public async Task<MatchExceptionDto> OverrideAsync(Guid billLineId, MatchOverrideRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("override a three-way match", cancellationToken);
        return await inner.OverrideAsync(billLineId, request, cancellationToken);
    }

    public async Task<SaveBillRequest> BuildBillAsync(CreateBillFromPurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("bill purchase orders", cancellationToken);
        return await inner.BuildBillAsync(request, cancellationToken);
    }
}

public sealed class PermissionCheckingCommitmentService(ICommitmentService inner, ProjectAccess projects) : ICommitmentService
{
    public async Task<ProjectCommitmentsDto> GetProjectCommitmentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see a project's commitments", cancellationToken);
        return await inner.GetProjectCommitmentsAsync(projectId, cancellationToken);
    }
}
