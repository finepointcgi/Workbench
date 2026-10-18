using System.Globalization;
using Microsoft.JSInterop;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Ui.Setup;

namespace Workbench.Ui.Accounting;

/// <summary>Display helpers for the accounting screens. Amounts show to the cent with negatives in parentheses.</summary>
public static class AccountingFormat
{
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    public static string Amount(decimal value) => value.ToString("#,##0.00;(#,##0.00);0.00", Usd);

    /// <summary>Blank for zero, for debit/credit columns.</summary>
    public static string Blank(decimal value) => value == 0 ? "" : Amount(value);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Label(CostClass value) => value switch
    {
        CostClass.None => "",
        CostClass.DirectLabor => "Direct labor",
        CostClass.DirectOdc => "Direct ODC",
        CostClass.IndirectLabor => "Indirect labor",
        CostClass.GeneralAndAdministrative => "G&A",
        _ => value.ToString(),
    };

    public static string Label(CostPool value) => value == CostPool.GeneralAndAdministrative ? "G&A" : value.ToString();

    public static string Label(JournalEntrySource value) => value switch
    {
        JournalEntrySource.Odc => "ODC",
        _ => value.ToString(),
    };

    public static string AccountLabel(AccountDto a) => $"{a.Number} {a.Name}";

    /// <summary>The current open period (or the latest open one, or the latest of all).</summary>
    public static FiscalPeriodDto? DefaultPeriod(IReadOnlyList<FiscalPeriodDto> periods)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return periods.FirstOrDefault(p => p.Start <= today && today <= p.End)
            ?? periods.Where(p => p.Status == FiscalPeriodState.Open).MinBy(p => p.Start)
            ?? periods.MaxBy(p => p.Start);
    }

    public static string Error(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ex.Message,
        _ => SetupFormat.Error(ex),
    };

    public static bool IsForbidden(Exception ex) => ex is UnauthorizedAccessException;

    public static async Task DownloadAsync(IJSRuntime js, AccountingExportFile file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/financials.js");
        await module.InvokeVoidAsync("downloadText", file.FileName, file.ContentType, file.Content);
    }
}
