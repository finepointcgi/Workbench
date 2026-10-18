using System.Globalization;
using Workbench.Contracts.Purchasing;

namespace Workbench.Ui.Purchasing.Procurement;

/// <summary>Display helpers for requisitions, purchase orders and receiving.</summary>
public static class ProcurementFormat
{
    public static string Quantity(decimal value) => value.ToString("#,##0.####", CultureInfo.InvariantCulture);

    public static string Price(decimal value) => value.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    public static string Label(RequisitionState s) => s switch
    {
        RequisitionState.PendingApproval => "Pending approval",
        _ => s.ToString(),
    };

    public static string Label(RequisitionApprovalStage s) => s == RequisitionApprovalStage.ProjectManager ? "Project manager" : "Finance";

    public static string Label(PurchaseOrderState s) => s switch
    {
        PurchaseOrderState.PartiallyReceived => "Partially received",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(RequisitionState s) => s switch
    {
        RequisitionState.Approved or RequisitionState.Ordered => MudBlazor.Color.Success,
        RequisitionState.PendingApproval => MudBlazor.Color.Warning,
        RequisitionState.Rejected => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static MudBlazor.Color Color(PurchaseOrderState s) => s switch
    {
        PurchaseOrderState.Issued => MudBlazor.Color.Info,
        PurchaseOrderState.PartiallyReceived => MudBlazor.Color.Warning,
        PurchaseOrderState.Received => MudBlazor.Color.Success,
        PurchaseOrderState.Cancelled => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static string Charge(string? projectCode, string? wbsCode, string? accountNumber, string? orgCode) =>
        accountNumber is not null ? $"{accountNumber} · {orgCode}" : $"{projectCode} {wbsCode}";
}
