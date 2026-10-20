using Workbench.Contracts.Quality;

namespace Workbench.Ui.Quality;

/// <summary>Plain labels and colors for the quality and engineering change screens.</summary>
public static class QualityFormat
{
    public static string Label(InspectionType t) => t switch
    {
        InspectionType.InProcess => "In-process",
        InspectionType.Final => "Final",
        _ => "Receiving",
    };

    public static string Label(Sampling s) => s switch
    {
        Sampling.FixedSample => "Fixed sample",
        Sampling.Aql => "AQL sample (Z1.4 level II)",
        _ => "Every unit (100%)",
    };

    public static string Label(NcrState s) => s switch
    {
        NcrState.PendingApproval => "Waiting for approval",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(NcrState s) => s switch
    {
        NcrState.Open => MudBlazor.Color.Warning,
        NcrState.PendingApproval => MudBlazor.Color.Info,
        NcrState.Closed => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(NcrDisposition d) => d switch
    {
        NcrDisposition.UseAsIs => "Use as is",
        NcrDisposition.ReturnToVendor => "Return to vendor",
        _ => d.ToString(),
    };

    public static string Explain(NcrDisposition d) => d switch
    {
        NcrDisposition.UseAsIs => "Accept it as it is: it goes back to available stock.",
        NcrDisposition.Rework => "Fix it on a rework work order; the material is kitted to the order and comes back to stock when it's completed.",
        NcrDisposition.ReturnToVendor => "Ship it back: it leaves stock against the receiving clearing account and a debit memo is written for accounts payable.",
        _ => "Throw it away: its cost goes to inventory adjustments and scrap, another expense account, or a project.",
    };

    public static string Label(NcrSource s) => s switch
    {
        NcrSource.ReceivingInspection => "Receiving inspection",
        NcrSource.InProcessInspection => "In-process inspection",
        NcrSource.FinalInspection => "Final inspection",
        _ => "Found in stock",
    };

    public static MudBlazor.Color Color(CarState s) => s switch
    {
        CarState.Open => MudBlazor.Color.Warning,
        CarState.Implemented => MudBlazor.Color.Info,
        CarState.Closed => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(CarState s) => s == CarState.Implemented ? "Waiting for verification" : s.ToString();

    public static string Label(RootCause r) => r switch
    {
        RootCause.NotDetermined => "Not determined yet",
        _ => r.ToString(),
    };

    public static string Label(EcoState s) => s switch
    {
        EcoState.PendingApproval => "Waiting for approval",
        EcoState.Approved => "Approved (not yet effective)",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(EcoState s) => s switch
    {
        EcoState.Draft => MudBlazor.Color.Default,
        EcoState.PendingApproval => MudBlazor.Color.Info,
        EcoState.Approved => MudBlazor.Color.Warning,
        EcoState.Effective => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(EcoReason r) => r switch
    {
        EcoReason.DefectCorrection => "Defect correction",
        EcoReason.DesignImprovement => "Design improvement",
        EcoReason.CostReduction => "Cost reduction",
        EcoReason.CustomerRequest => "Customer request",
        _ => r.ToString(),
    };

    public static string Label(EcoStockDisposition d) => d switch
    {
        EcoStockDisposition.UseUp => "Use up existing stock",
        EcoStockDisposition.ReturnToVendor => "Return to vendor",
        _ => d.ToString(),
    };

    public static string Label(EcoWorkOrderDisposition d) => d switch
    {
        EcoWorkOrderDisposition.FinishOnOldRevision => "Finish on the old revision",
        EcoWorkOrderDisposition.UpdateToNewRevision => "Update to the new revision",
        _ => "Put on hold",
    };

    public static string Effectivity(EcoSummaryDto s) => s.EffectivityKind == Contracts.Quality.Effectivity.Date
        ? $"From {Materials.MaterialsFormat.Date(s.EffectiveDate)}"
        : $"From serial or lot {s.EffectiveSerialOrLot}";
}
