using Workbench.Contracts;
using Workbench.Contracts.Revenue;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.Revenue;

/// <summary>Display helpers for the revenue screens.</summary>
public static class RevenueFormat
{
    public static string Amount(decimal value) => AccountingFormat.Amount(value);

    public static string Percent(decimal? fraction) => fraction is { } f ? f.ToString("P2", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : "";

    public static string Label(RevenueMethodKind method) => method switch
    {
        RevenueMethodKind.Billings => "Revenue = billings",
        RevenueMethodKind.CostToCost => "Cost-to-cost % complete",
        RevenueMethodKind.TimeAndMaterialsBilled => "T&M as billed",
        RevenueMethodKind.TimeAndMaterialsIncurred => "T&M as incurred",
        RevenueMethodKind.CostPlusFee => "Cost plus fee",
        RevenueMethodKind.FixedPriceMilestones => "FFP milestones",
        RevenueMethodKind.FixedPriceUnits => "FFP units delivered",
        _ => method.ToString(),
    };

    public static string Unit(string? clinNumber) => clinNumber is null ? "Project" : $"CLIN {clinNumber}";

    public static bool IsExpected(Exception ex) =>
        ex is HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or WorkbenchNotFoundException or TaskCanceledException;

    public static string Error(Exception ex) => AccountingFormat.Error(ex);

    public static bool IsForbidden(Exception ex) => ex is UnauthorizedAccessException;
}
