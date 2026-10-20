using System.Globalization;
using Microsoft.JSInterop;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Assets;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.Assets;

/// <summary>Display helpers for the fixed asset screens.</summary>
public static class AssetFormat
{
    public static string Amount(decimal value) => AccountingFormat.Amount(value);

    public static string Date(DateOnly? value) => value is { } d ? AccountingFormat.Date(d) : "";

    public static string Label(AssetDepreciationMethod m) => m switch
    {
        AssetDepreciationMethod.StraightLine => "Straight line",
        AssetDepreciationMethod.DecliningBalance => "Declining balance (200%)",
        _ => "Sum-of-years digits",
    };

    public static string Label(AssetDepreciationConvention c) => c switch
    {
        AssetDepreciationConvention.MidMonth => "Half month (mid-month)",
        AssetDepreciationConvention.FollowingMonth => "Following month",
        _ => "Full month",
    };

    public static string Help(AssetDepreciationConvention c) => c switch
    {
        AssetDepreciationConvention.MidMonth => "Half a month in the in-service month, the other half at the end of the life.",
        AssetDepreciationConvention.FollowingMonth => "Depreciation starts the month after the asset goes into service.",
        _ => "A whole month of depreciation in the in-service month.",
    };

    public static MudBlazor.Color Color(AssetState s) => s switch
    {
        AssetState.InService => MudBlazor.Color.Success,
        AssetState.Disposed => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Info,
    };

    public static string Label(AssetState s) => s == AssetState.InService ? "In service" : s.ToString();

    public static string Label(AssetBookKind b) => b switch
    {
        AssetBookKind.Book => "Book (GAAP)",
        AssetBookKind.Federal => "Federal tax",
        AssetBookKind.State => "State tax",
        _ => "AMT",
    };

    public static string Label(TaxMethod m) => m switch
    {
        TaxMethod.MacrsGds => "MACRS GDS",
        TaxMethod.Macrs150Db => "MACRS 150% declining balance",
        TaxMethod.StraightLine => "MACRS straight line",
        _ => "MACRS ADS (straight line)",
    };

    public static string Label(TaxConventionKind c) => c switch
    {
        TaxConventionKind.HalfYear => "Half-year",
        TaxConventionKind.MidQuarter => "Mid-quarter",
        _ => "Mid-month",
    };

    public static string Label(AssetDisposalType k) => k switch
    {
        AssetDisposalType.Scrap => "Scrapped",
        AssetDisposalType.TradeIn => "Traded in",
        _ => "Sold",
    };

    /// <summary>"Oct 2026 – Sep 2027" for a tax year that doesn't follow the calendar, otherwise "2026".</summary>
    public static string TaxYear(int year, int startMonth) => startMonth == 1
        ? year.ToString(CultureInfo.InvariantCulture)
        : $"{new DateOnly(year, startMonth, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture)} – {new DateOnly(year, startMonth, 1).AddYears(1).AddDays(-1).ToString("MMM yyyy", CultureInfo.InvariantCulture)}";

    public static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public static Task DownloadAsync(IJSRuntime js, AssetExportFile file) =>
        AccountingFormat.DownloadAsync(js, new AccountingExportFile(file.FileName, file.ContentType, file.Content));
}
