using Microsoft.JSInterop;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Purchasing;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.Purchasing;

/// <summary>Display helpers for the purchasing screens (amounts as in accounting: to the cent, negatives in parentheses).</summary>
public static class PurchasingFormat
{
    public static string Amount(decimal value) => AccountingFormat.Amount(value);

    public static string Date(DateOnly value) => AccountingFormat.Date(value);

    public static string Date(DateOnly? value) => value is { } d ? AccountingFormat.Date(d) : "";

    public static DateOnly? ToDateOnly(DateTime? value) => value is { } d ? DateOnly.FromDateTime(d) : null;

    public static DateTime ToDateTime(DateOnly value) => value.ToDateTime(TimeOnly.MinValue);

    public static string Error(Exception ex) => AccountingFormat.Error(ex);

    public static bool IsForbidden(Exception ex) => AccountingFormat.IsForbidden(ex);

    /// <summary>The exceptions a purchasing call can raise that pages show instead of crashing the circuit.</summary>
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
        || ex.GetType().Name.Contains("NotFound", StringComparison.Ordinal);

    public static string Label(BillState s) => s switch
    {
        BillState.PendingApproval => "Pending approval",
        _ => s.ToString(),
    };

    public static string Label(BillLineKind k) => k switch
    {
        BillLineKind.ProjectCost => "Project cost",
        BillLineKind.Account => "GL account (overhead)",
        BillLineKind.Retainage => "Retainage released",
        _ => "Subcontract",
    };

    /// <summary>The line kinds a person can enter (retainage lines come from a retainage release).</summary>
    public static IReadOnlyList<BillLineKind> EnterableLineKinds { get; } = [BillLineKind.ProjectCost, BillLineKind.Account, BillLineKind.Subcontract];

    public static string Label(BillKind k) => k switch
    {
        BillKind.CreditMemo => "Credit memo",
        BillKind.DebitMemo => "Debit memo",
        BillKind.RetainageRelease => "Retainage release",
        _ => "Bill",
    };

    /// <summary>A memo's amounts read negative (what the vendor owes back), a bill's positive.</summary>
    public static decimal Signed(BillSummaryDto b, decimal amount) => b.IsCredit ? -amount : amount;

    public static string Label(SubcontractPricing p) => p switch
    {
        SubcontractPricing.FirmFixedPrice => "FFP",
        SubcontractPricing.TimeAndMaterials => "T&M",
        SubcontractPricing.LaborHour => "LH",
        SubcontractPricing.CostPlusFixedFee => "CPFF",
        SubcontractPricing.CostPlusAwardFee => "CPAF",
        SubcontractPricing.CostPlusIncentiveFee => "CPIF",
        _ => "Cost, no fee",
    };

    public static MudBlazor.Color Color(BillState s) => s switch
    {
        BillState.Posted => MudBlazor.Color.Success,
        BillState.Approved => MudBlazor.Color.Info,
        BillState.PendingApproval => MudBlazor.Color.Warning,
        BillState.Rejected => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static MudBlazor.Color Color(VendorState s) => s switch
    {
        VendorState.Active => MudBlazor.Color.Success,
        VendorState.OnHold => MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Default,
    };

    public static Task DownloadAsync(IJSRuntime js, PurchasingExportFile file) =>
        AccountingFormat.DownloadAsync(js, new AccountingExportFile(file.FileName, file.ContentType, file.Content));
}
