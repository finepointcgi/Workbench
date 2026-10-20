using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Compliance;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.Compliance;

/// <summary>Display helpers for the CPSR and CAS disclosure pages.</summary>
public static class ComplianceFormat
{
    public const string AccessNote = "Purchasing system review files and the CAS disclosure statement are for Finance and administrators.";

    public static string Amount(decimal value) => AccountingFormat.Amount(value);

    public static string Date(DateOnly? value) => value is { } d ? AccountingFormat.Date(d) : "";

    public static string Error(Exception ex) => AccountingFormat.Error(ex);

    public static bool IsForbidden(Exception ex) => AccountingFormat.IsForbidden(ex);

    public static bool IsExpected(Exception ex) => Workbench.Ui.Purchasing.PurchasingFormat.IsExpected(ex);

    public static Task DownloadAsync(IJSRuntime js, ComplianceExportFile file) =>
        AccountingFormat.DownloadAsync(js, new AccountingExportFile(file.FileName, file.ContentType, file.Content));

    public static string Label(CpsrElementStatus s) => s switch
    {
        CpsrElementStatus.Met => "Met",
        CpsrElementStatus.Missing => "Missing",
        _ => "Doesn't apply",
    };

    public static Color Color(CpsrElementStatus s) => s switch
    {
        CpsrElementStatus.Met => MudBlazor.Color.Success,
        CpsrElementStatus.Missing => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static Color ScoreColor(int score) => score >= 90 ? MudBlazor.Color.Success : score >= 75 ? MudBlazor.Color.Warning : MudBlazor.Color.Error;

    public static string Label(CompetitionType c) => c switch
    {
        CompetitionType.Competitive => "Competitive quotes",
        CompetitionType.SoleSource => "Sole source",
        CompetitionType.MicroPurchase => "Micro-purchase",
        _ => "Not recorded",
    };

    public static string Label(PriceAnalysisType p) => p switch
    {
        PriceAnalysisType.PriceAnalysis => "Price analysis",
        PriceAnalysisType.CostAnalysis => "Cost analysis",
        PriceAnalysisType.Both => "Price and cost analysis",
        _ => "None",
    };

    public static string Label(ProcurementDocumentKind k) => k == ProcurementDocumentKind.PurchaseOrder ? "Purchase order" : "Subcontract";

    public static string Label(DisclosureState s) => s switch
    {
        DisclosureState.Effective => "In force",
        DisclosureState.Superseded => "Superseded",
        _ => "Draft",
    };

    public static Color Color(DisclosureState s) => s switch
    {
        DisclosureState.Effective => MudBlazor.Color.Success,
        DisclosureState.Superseded => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Info,
    };

    public static Severity Severity(ConsistencySeverity s) => s switch
    {
        ConsistencySeverity.Problem => MudBlazor.Severity.Error,
        ConsistencySeverity.Warning => MudBlazor.Severity.Warning,
        _ => MudBlazor.Severity.Info,
    };
}
