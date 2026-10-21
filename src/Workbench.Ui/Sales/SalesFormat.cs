using Microsoft.JSInterop;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;

namespace Workbench.Ui.Sales;

/// <summary>Display helpers for the sales order and shipping screens.</summary>
public static class SalesFormat
{
    /// <summary>Materials, Finance and administrators enter orders and ship them; Business development only reads orders.</summary>
    public static bool CanEdit(CurrentUserInfo? user) => user is null || user.Can(Workbench.Contracts.Identity.Permission.SalesOrdersManage);

    public static bool CanCreditHold(CurrentUserInfo? user) => user is null || user.Can(Workbench.Contracts.Identity.Permission.SalesCreditHold);

    public static bool CanInvoice(CurrentUserInfo? user) => user is null || user.Can(Workbench.Contracts.Identity.Permission.BillingInvoicesManage);

    public static string Label(SalesOrderState s) => s switch
    {
        SalesOrderState.PartlyShipped => "Partly shipped",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(SalesOrderState s) => s switch
    {
        SalesOrderState.Released => MudBlazor.Color.Info,
        SalesOrderState.PartlyShipped => MudBlazor.Color.Warning,
        SalesOrderState.Shipped or SalesOrderState.Closed => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(ShipmentState s) => s switch
    {
        ShipmentState.Packing => "Being packed",
        ShipmentState.Confirmed => "Shipped",
        _ => "Cancelled",
    };

    public static MudBlazor.Color Color(ShipmentState s) => s switch
    {
        ShipmentState.Packing => MudBlazor.Color.Info,
        ShipmentState.Confirmed => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(PickListState s) => s switch
    {
        PickListState.Open => "To pick",
        PickListState.Packed => "Packed",
        _ => "Cancelled",
    };

    public static string Label(WawfDocument d) => d switch
    {
        WawfDocument.Combo => "Combo (invoice and receiving report)",
        WawfDocument.TwoInOne => "2-in-1 (services)",
        _ => "Receiving report",
    };

    public static string Label(WawfReportState s) => s.ToString();

    public static string Point(GovernmentInspectionPoint p) => p == GovernmentInspectionPoint.Origin ? "Origin" : "Destination";

    public static string Tracking(string lot, string serial) =>
        string.Join(" · ", new[] { lot.Length > 0 ? $"Lot {lot}" : null, serial.Length > 0 ? $"S/N {serial}" : null }.Where(x => x is not null));

    public static async Task DownloadAsync(IJSRuntime js, ShippingFile file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/evm.js");
        await module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }

    public static async Task PrintAsync(IJSRuntime js)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/billing.js");
        await module.InvokeVoidAsync("printPage");
    }
}
