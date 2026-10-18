using System.Globalization;
using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.IncurredCost;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.IncurredCost;

/// <summary>Display helpers for the incurred cost screens: cells by column kind, status labels and the file download.</summary>
public static class IceFormat
{
    public static string Cell(IceColumnDto column, IceRowDto row) => column.Kind switch
    {
        IceColumnKind.Amount => row.NumberOrNull(column.Key) is { } a ? AccountingFormat.Amount(a) : "",
        IceColumnKind.Rate => row.NumberOrNull(column.Key) is { } r ? Rate(r) : "",
        IceColumnKind.Hours => row.NumberOrNull(column.Key) is { } h ? h.ToString("#,##0.00", CultureInfo.InvariantCulture) : "",
        _ => row.TextOf(column.Key) ?? "",
    };

    public static string Rate(decimal value) => (value * 100).ToString("0.0000", CultureInfo.InvariantCulture) + "%";

    public static bool IsNumeric(IceColumnDto column) => column.Kind is IceColumnKind.Amount or IceColumnKind.Rate or IceColumnKind.Hours;

    public static string Tab(string code) => code switch
    {
        "Cover" => "Contractor",
        "U" => "Unallowable",
        _ => code,
    };

    public static string Label(IncurredCostState state) => state switch
    {
        IncurredCostState.Draft => "Draft",
        IncurredCostState.Reviewed => "Reviewed",
        IncurredCostState.Certified => "Certified",
        _ => "Submitted",
    };

    public static Color Color(IncurredCostState state) => state switch
    {
        IncurredCostState.Reviewed => MudBlazor.Color.Info,
        IncurredCostState.Certified => MudBlazor.Color.Primary,
        IncurredCostState.Submitted => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static Severity Severity(IceCheckSeverity severity) => severity switch
    {
        IceCheckSeverity.Error => MudBlazor.Severity.Error,
        IceCheckSeverity.Warning => MudBlazor.Severity.Warning,
        _ => MudBlazor.Severity.Info,
    };

    public static string When(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    public static async Task DownloadAsync(IJSRuntime js, IncurredCostExportFile file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/incurredCost.js");
        await module.InvokeVoidAsync("downloadBytes", file.FileName, file.ContentType, file.Content);
    }

    public static async Task PrintAsync(IJSRuntime js)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/billing.js");
        await module.InvokeVoidAsync("printPage");
    }
}
