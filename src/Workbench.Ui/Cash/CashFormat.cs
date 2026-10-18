using Microsoft.JSInterop;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Cash;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.Cash;

/// <summary>Display helpers for the cash screens (amounts as in accounting: to the cent, negatives in parentheses).</summary>
public static class CashFormat
{
    public static string Amount(decimal value) => AccountingFormat.Amount(value);

    public static string Amount(decimal? value) => value is { } v ? AccountingFormat.Amount(v) : "—";

    public static string Date(DateOnly value) => AccountingFormat.Date(value);

    public static string Date(DateOnly? value) => value is { } d ? AccountingFormat.Date(d) : "—";

    public static DateOnly? ToDateOnly(DateTime? value) => value is { } d ? DateOnly.FromDateTime(d) : null;

    public static DateTime ToDateTime(DateOnly value) => value.ToDateTime(TimeOnly.MinValue);

    public static string Error(Exception ex) => AccountingFormat.Error(ex);

    public static bool IsForbidden(Exception ex) => AccountingFormat.IsForbidden(ex);

    /// <summary>The exceptions a cash call can raise that pages show instead of crashing the circuit.</summary>
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
        || ex.GetType().Name.Contains("NotFound", StringComparison.Ordinal);

    public static string Label(MatchRule rule) => rule switch
    {
        MatchRule.ArPaymentReference => "AR reference",
        MatchRule.ApCheckNumber => "Check number",
        MatchRule.ApAchReference => "ACH reference",
        MatchRule.AmountAndDate => "Amount and date",
        MatchRule.AmountAndDateGroup => "Amount group",
        MatchRule.ReversalPair => "Reversal pair",
        MatchRule.BookedFromBank => "Booked from bank",
        _ => "Manual",
    };

    public static string Label(BankItemType type) => type switch
    {
        BankItemType.Ach => "ACH",
        _ => type.ToString(),
    };

    public static string Label(StatementFileFormat format) => format switch
    {
        StatementFileFormat.Ofx => "OFX/QFX",
        StatementFileFormat.Bai2 => "BAI2",
        _ => "CSV",
    };

    public static string Label(JournalEntrySource source) => AccountingFormat.Label(source);

    public static MudBlazor.Color ConfidenceColor(int confidence) => confidence switch
    {
        >= 90 => MudBlazor.Color.Success,
        >= 70 => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Warning,
    };

    public static async Task PrintAsync(IJSRuntime js)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/billing.js");
        await module.InvokeVoidAsync("printPage");
    }
}
