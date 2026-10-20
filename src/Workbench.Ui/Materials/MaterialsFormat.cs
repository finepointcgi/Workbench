using System.Globalization;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Ui.Purchasing;

namespace Workbench.Ui.Materials;

/// <summary>Display helpers for the materials screens.</summary>
public static class MaterialsFormat
{
    public static string Amount(decimal value) => PurchasingFormat.Amount(value);

    public static string Amount(decimal? value) => value is { } v ? PurchasingFormat.Amount(v) : "—";

    public static string Date(DateOnly value) => PurchasingFormat.Date(value);

    public static string Date(DateOnly? value) => PurchasingFormat.Date(value);

    /// <summary>Quantities without trailing zeros (12, 2.5, 0.125).</summary>
    public static string Qty(decimal value) => value.ToString("#,##0.####;-#,##0.####;0", CultureInfo.InvariantCulture);

    public static string Cost(decimal value) => value.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    public static string Error(Exception ex) => PurchasingFormat.Error(ex);

    public static bool IsExpected(Exception ex) => PurchasingFormat.IsExpected(ex);

    public static bool IsForbidden(Exception ex) => PurchasingFormat.IsForbidden(ex);

    /// <summary>Materials and administrators change things; Finance only reads.</summary>
    public static bool CanEdit(CurrentUserInfo? user) => user is null || user.Roles.IsMaterials();

    public static string Label(StockOwnership o) => o switch
    {
        StockOwnership.Contract => "Contract",
        StockOwnership.GovernmentFurnished => "Government-furnished",
        _ => "General",
    };

    public static string Label(InventoryMovement m) => m switch
    {
        InventoryMovement.PurchaseReceipt => "PO receipt",
        InventoryMovement.GovernmentReceipt => "GFM receipt",
        InventoryMovement.IssueToProject => "Issue to project",
        InventoryMovement.IssueToWorkOrder => "Issue to work order",
        InventoryMovement.ReturnFromWorkOrder => "Return from work order",
        InventoryMovement.TransferOut => "Transfer out",
        InventoryMovement.TransferIn => "Transfer in",
        InventoryMovement.CycleCount => "Cycle count",
        InventoryMovement.WorkOrderCompletion => "Completion to stock",
        InventoryMovement.WorkOrderShipment => "Delivered to project",
        InventoryMovement.StatusChange => "Status change",
        InventoryMovement.VendorReturn => "Return to vendor",
        InventoryMovement.InventoryScrap => "Scrapped from stock",
        _ => m.ToString(),
    };

    public static string Label(StockStatus s) => s switch
    {
        StockStatus.InspectionHold => "Inspection hold",
        StockStatus.Nonconforming => "Nonconforming",
        _ => "Available",
    };

    public static string Label(WorkOrderState s) => s switch
    {
        WorkOrderState.InProgress => "In progress",
        _ => s.ToString(),
    };

    public static string Label(PartCostMethod m) => m == PartCostMethod.Average ? "Moving average" : "Standard";

    public static string Label(PartTracking t) => t switch
    {
        PartTracking.Lot => "Lot",
        PartTracking.Serial => "Serial",
        _ => "None",
    };

    public static MudBlazor.Color Color(WorkOrderState s) => s switch
    {
        WorkOrderState.Released or WorkOrderState.InProgress => MudBlazor.Color.Info,
        WorkOrderState.Completed => MudBlazor.Color.Success,
        WorkOrderState.Cancelled => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Default,
    };

    public static DateTime ToDateTime(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}
