using System.Globalization;
using Microsoft.JSInterop;
using Workbench.Contracts.Billing;
using Workbench.Ui.Setup;

namespace Workbench.Ui.Billing;

/// <summary>Display helpers for the billing screens. Amounts show to the cent with negatives in parentheses.</summary>
public static class BillingFormat
{
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    public static string Amount(decimal value) => value.ToString("#,##0.00;(#,##0.00);0.00", Usd);

    public static string Money(decimal value) => value.ToString("$#,##0.00;($#,##0.00);$0.00", Usd);

    public static string Blank(decimal value) => value == 0 ? "" : Amount(value);

    public static string Hours(decimal value) => value.ToString("#,##0.00", Usd);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? value) => value is { } d ? Date(d) : "";

    public static string Percent(decimal value) => value.ToString("0.##%", Usd);

    public static string Label(InvoiceState s) => s == InvoiceState.PartiallyPaid ? "Partially paid" : s.ToString();

    public static string Label(InvoiceKind k) => k switch
    {
        InvoiceKind.TimeAndMaterials => "T&M / labor hour",
        InvoiceKind.CostPlus => "Cost-plus",
        InvoiceKind.FixedPriceMilestone => "FFP milestone",
        InvoiceKind.TrueUp => "Year-end true-up",
        _ => "Credit memo",
    };

    public static string Label(InvoiceLineType k) => k switch
    {
        InvoiceLineType.MaterialHandling => "Material handling",
        InvoiceLineType.FeeWithhold => "Fee withhold",
        InvoiceLineType.WithholdRelease => "Withhold release",
        InvoiceLineType.Odc => "ODC",
        _ => k.ToString(),
    };

    public static string Label(CashReceiptMethod m) => m == CashReceiptMethod.Eft ? "EFT" : m.ToString();

    /// <summary>A rate for display: hourly bill rates as money, fee and withhold rates as percentages, nothing for cost lines.</summary>
    public static string LineRate(InvoiceLineDto l) => l.Kind switch
    {
        InvoiceLineType.Labor when l.Rate != 0 => Money(l.Rate),
        InvoiceLineType.Fee or InvoiceLineType.FeeWithhold or InvoiceLineType.MaterialHandling => Percent(l.Rate),
        _ => "",
    };

    public static string LineQuantity(InvoiceLineDto l) => l.Kind == InvoiceLineType.Labor && l.Quantity != 0 ? Hours(l.Quantity) + " h" : "";

    public static string Error(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ex.Message,
        _ => SetupFormat.Error(ex),
    };

    public static bool IsForbidden(Exception ex) => ex is UnauthorizedAccessException;

    public static async Task DownloadAsync(IJSRuntime js, BillingExportFile file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/financials.js");
        await module.InvokeVoidAsync("downloadText", file.FileName, file.ContentType, file.Content);
    }

    /// <summary>The first and last day of the month before <paramref name="today"/>.</summary>
    public static (DateOnly From, DateOnly To) PreviousMonth(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    public static (DateOnly From, DateOnly To) CurrentMonth(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }
}
