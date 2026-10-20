using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

/// <summary>
/// <see cref="PurchasingKit"/> plus procure-to-pay: analyst@ is in the OPS org with a charge assignment on 1001.01
/// (so they can requisition for project 1001), and helpers to take a requisition through to an issued PO.
/// </summary>
internal static class ProcurementKit
{
    public static async Task<PurchasingKit> CreateAsync(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null)
    {
        var kit = await PurchasingKit.CreateAsync(configure);
        await using var db = await kit.Factory.CreateAsync();
        var analyst = await db.Set<Employee>().FirstAsync(e => e.Id == kit.Fx.AnalystId);
        analyst.OrgUnitId = kit.OrgId;
        db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = kit.Fx.Wbs1, EmployeeId = kit.Fx.AnalystId });
        await db.SaveChangesAsync();
        return kit;
    }

    public static IRequisitionService Requisitions(this PurchasingKit kit) => kit.Get<IRequisitionService>();

    public static IPurchaseOrderService Orders(this PurchasingKit kit) => kit.Get<IPurchaseOrderService>();

    public static IThreeWayMatchService Match(this PurchasingKit kit) => kit.Get<IThreeWayMatchService>();

    public static ICommitmentService Commitments(this PurchasingKit kit) => kit.Get<ICommitmentService>();

    public static PurchasingOptions Options(this PurchasingKit kit) => kit.Get<IOptions<PurchasingOptions>>().Value;

    public static SaveRequisitionLineRequest ProjectLine(this PurchasingKit kit, decimal quantity, decimal unitCost, string description = "Rugged laptops") =>
        new(description, quantity, unitCost, WbsElementId: kit.Fx.Wbs1, OdcType: OdcCostType.Equipment);

    public static SaveRequisitionLineRequest AccountLine(this PurchasingKit kit, decimal quantity, decimal unitCost, string account = "1510", string description = "Engineering workstation") =>
        new(description, quantity, unitCost, AccountId: kit.Account(account));

    /// <summary>analyst@ requisitions and submits.</summary>
    public static async Task<RequisitionDto> SubmitRequisitionAsync(this PurchasingKit kit, params SaveRequisitionLineRequest[] lines)
    {
        kit.As(Editor);
        var req = await kit.Requisitions().CreateAsync(new SaveRequisitionRequest("Needed for the field test", lines));
        return await kit.Requisitions().SubmitAsync(req.Requisition.Id);
    }

    /// <summary>Approves every stage (pm@ for project lines, admin@ for Finance) and returns to finance@.</summary>
    public static async Task<RequisitionDto> ApproveAllAsync(this PurchasingKit kit, RequisitionDto req)
    {
        if (req.Requisition.Stage == RequisitionApprovalStage.ProjectManager)
        {
            kit.As(Owner);
            req = await kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest());
        }

        if (req.Requisition.Stage == RequisitionApprovalStage.Finance)
        {
            kit.As(Admin);
            req = await kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest());
        }

        kit.As(Finance);
        Assert.Equal(RequisitionState.Approved, req.Requisition.Status);
        return req;
    }

    /// <summary>A requisition (analyst@) approved and ordered from ACME as an issued PO (finance@).</summary>
    public static async Task<PurchaseOrderDto> IssuedOrderAsync(this PurchasingKit kit, params SaveRequisitionLineRequest[] lines)
    {
        var req = await kit.ApproveAllAsync(await kit.SubmitRequisitionAsync(lines));
        var po = await kit.Orders().CreateFromRequisitionsAsync(new CreatePurchaseOrderFromRequisitionsRequest([req.Requisition.Id], kit.Acme.Id, PurchasingKit.Day, ShipTo: "HQ"));
        return await kit.Orders().IssueAsync(po.Order.Id);
    }

    public static async Task<PurchaseOrderDto> ReceiveAsync(this PurchasingKit kit, PurchaseOrderDto po, params decimal[] quantities) =>
        await kit.Orders().ReceiveAsync(po.Order.Id, new ReceiveRequest(PurchasingKit.Day,
            po.Lines.Zip(quantities).Where(x => x.Second != 0).Select(x => new ReceiveLineRequest(x.First.Id, x.Second)).ToList()));

    /// <summary>finance@ enters and submits a bill for <paramref name="quantity"/> of the PO's first line.</summary>
    public static async Task<BillDto> BillPoLineAsync(this PurchasingKit kit, PurchaseOrderDto po, string invoice, decimal quantity, decimal? unitPrice = null, int line = 0)
    {
        kit.As(Finance);
        var poLine = po.Lines[line];
        var bill = await kit.Bills.CreateAsync(new SaveBillRequest(po.Order.VendorId, invoice, PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "", 0, PurchaseOrderLineId: poLine.Id, Quantity: quantity, UnitPrice: unitPrice)]));
        return await kit.Bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest());
    }
}
