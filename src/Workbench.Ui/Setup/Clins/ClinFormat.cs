using System.Globalization;
using MudBlazor;
using Workbench.Contracts.Contracts;

namespace Workbench.Ui.Setup.Clins;

public static class ClinFormat
{
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C0", Usd);

    public static string Percent(decimal? fraction) => fraction is { } f ? f.ToString("P1", Usd) : "—";

    public static string Label(ClinPricingKind pricing) => pricing switch
    {
        ClinPricingKind.FirmFixedPrice => "FFP",
        ClinPricingKind.TimeAndMaterials => "T&M",
        ClinPricingKind.LaborHour => "LH",
        ClinPricingKind.CostPlusFixedFee => "CPFF",
        ClinPricingKind.CostPlusAwardFee => "CPAF",
        ClinPricingKind.CostPlusIncentiveFee => "CPIF",
        _ => "Cost",
    };

    public static string Label(ModificationChangeKind kind) => kind switch
    {
        ModificationChangeKind.AddClin => "Add CLIN",
        ModificationChangeKind.ExerciseOption => "Exercise option",
        ModificationChangeKind.ChangeClinValue => "Change CLIN value",
        ModificationChangeKind.ExtendPeriod => "Extend period of performance",
        _ => "Funding",
    };

    public static string Label(FundingLevel level) => level switch
    {
        FundingLevel.NotActive => "Unexercised option",
        FundingLevel.Exhausted => "Funding exhausted",
        FundingLevel.Critical => "Over 90%",
        FundingLevel.Warning => "Over 75%",
        _ => "Funded",
    };

    public static Color Color(FundingLevel level) => level switch
    {
        FundingLevel.Exhausted or FundingLevel.Critical => MudBlazor.Color.Error,
        FundingLevel.Warning => MudBlazor.Color.Warning,
        FundingLevel.NotActive => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Success,
    };

    /// <summary>One line describing a modification change, for lists and the execute confirmation.</summary>
    public static string Describe(ModificationChangeDto c) => c.Kind switch
    {
        ModificationChangeKind.AddClin => $"Add {(c.IsOption ? "option " : "")}CLIN {c.ClinNumber} · {c.ClinDescription} · {Money(c.Amount ?? 0)}",
        ModificationChangeKind.ExerciseOption => $"Exercise option CLIN {c.ClinNumber}",
        ModificationChangeKind.ChangeClinValue => $"CLIN {c.ClinNumber} value {(c.Amount >= 0 ? "+" : "")}{Money(c.Amount ?? 0)}",
        ModificationChangeKind.ExtendPeriod => $"Extend {(string.IsNullOrEmpty(c.ClinNumber) ? "contract" : $"CLIN {c.ClinNumber}")} to {c.NewPeriodEnd:yyyy-MM-dd}",
        _ => $"{(c.Amount >= 0 ? "Fund" : "De-obligate")} CLIN {c.ClinNumber} {Money(Math.Abs(c.Amount ?? 0))}",
    };
}
