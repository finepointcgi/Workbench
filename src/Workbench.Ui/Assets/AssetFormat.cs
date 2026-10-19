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

    public static Task DownloadAsync(IJSRuntime js, AssetExportFile file) =>
        AccountingFormat.DownloadAsync(js, new AccountingExportFile(file.FileName, file.ContentType, file.Content));
}
