using System.Globalization;
using Microsoft.JSInterop;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Pricing;

namespace Workbench.Ui.Pricing;

public static class PricingFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C0", Culture);

    public static string Cents(decimal value) => value.ToString("C2", Culture);

    public static string Rate(decimal value) => value.ToString("$#,##0.00##", Culture);

    public static string Hours(decimal value) => value.ToString("#,##0.##", Culture);

    /// <summary>A fraction as a percentage (0.0725 → 7.25%).</summary>
    public static string Percent(decimal? value) => value is { } v ? (v * 100).ToString("0.##", Culture) + "%" : "—";

    public static string Multiplier(decimal value) => value.ToString("0.0000", Culture);

    public static string Label(PricingContractKind type) => type switch
    {
        PricingContractKind.FirmFixedPrice => "Firm-fixed-price (FFP)",
        PricingContractKind.TimeAndMaterials => "Time-and-materials (T&M)",
        PricingContractKind.LaborHour => "Labor-hour (LH)",
        PricingContractKind.CostPlusFixedFee => "Cost-plus-fixed-fee (CPFF)",
        PricingContractKind.CostPlusAwardFee => "Cost-plus-award-fee (CPAF)",
        PricingContractKind.CostPlusIncentiveFee => "Cost-plus-incentive-fee (CPIF)",
        _ => type.ToString(),
    };

    public static string Short(PricingContractKind type) => type switch
    {
        PricingContractKind.FirmFixedPrice => "FFP",
        PricingContractKind.TimeAndMaterials => "T&M",
        PricingContractKind.LaborHour => "LH",
        PricingContractKind.CostPlusFixedFee => "CPFF",
        PricingContractKind.CostPlusAwardFee => "CPAF",
        PricingContractKind.CostPlusIncentiveFee => "CPIF",
        _ => type.ToString(),
    };

    public static bool IsTimeAndMaterials(PricingContractKind type) => type is PricingContractKind.TimeAndMaterials or PricingContractKind.LaborHour;

    /// <summary>"Fee" on cost-type contracts, "profit" on fixed-price and T&amp;M.</summary>
    public static string FeeWord(PricingContractKind type) =>
        type is PricingContractKind.CostPlusFixedFee or PricingContractKind.CostPlusAwardFee or PricingContractKind.CostPlusIncentiveFee ? "Fee" : "Profit";

    public static string Label(RateBasis basis) => basis == RateBasis.ForwardPricing ? "Forward pricing" : basis.ToString();

    public static string Label(LaborRateSource source) => source switch
    {
        LaborRateSource.CategoryAverage => "Category average",
        LaborRateSource.CategoryMedian => "Category median",
        LaborRateSource.SelectedPeople => "Selected people",
        LaborRateSource.NamedEmployee => "Named employee",
        LaborRateSource.Manual => "Entered rate",
        _ => source.ToString(),
    };

    public static string Label(PriceCostType kind) => kind switch
    {
        PriceCostType.Odc => "ODC",
        PriceCostType.Subcontract => "Subcontract",
        _ => kind.ToString(),
    };

    public static string Label(ProposalState state) => state.ToString();

    public static MudBlazor.Color Color(ProposalState state) => state switch
    {
        ProposalState.Won => MudBlazor.Color.Success,
        ProposalState.Submitted => MudBlazor.Color.Info,
        ProposalState.Lost or ProposalState.Cancelled => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Primary,
    };

    public static string ModelLabel(PriceModelRefDto m) =>
        m.IsLocked ? $"{m.Name} (locked {m.LockedAtUtc?.ToLocalTime():MMM d})" : m.IsBase ? $"{m.Name}" : $"Scenario: {m.Name}";

    public static string Error(Exception ex) => ex switch
    {
        ArgumentException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException => ex.Message,
        _ when ex.GetType().Name.Contains("NotFound", StringComparison.Ordinal) => ex.Message,
        _ => "Something went wrong. " + ex.Message,
    };

    public static async Task DownloadAsync(IJSRuntime js, PricingExportFile file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/evm.js");
        await module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }

    public static async Task PrintAsync(IJSRuntime js)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/billing.js");
        await module.InvokeVoidAsync("printPage");
    }

    /// <summary>Parses a percentage typed by a person ("7.5" → 0.075), or null for blank.</summary>
    public static decimal? FromPercent(decimal? percent) => percent is { } p ? Math.Round(p / 100m, 6, MidpointRounding.AwayFromZero) : null;

    public static decimal? ToPercent(decimal? fraction) => fraction is { } f ? Math.Round(f * 100m, 4, MidpointRounding.AwayFromZero) : null;
}

/// <summary>What <see cref="PricingTextDialog"/> returns.</summary>
public sealed record PricingTextResult(string Text, string? Note, bool Flag);
