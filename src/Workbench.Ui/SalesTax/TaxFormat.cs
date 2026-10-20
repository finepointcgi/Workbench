using System.Globalization;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.SalesTax;

/// <summary>Formatting and error helpers for the sales tax screens.</summary>
public static class TaxFormat
{
    public static string Money(decimal value) => value.ToString("C", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>A fraction as a percent with up to three decimals: 0.0575 → "5.75%".</summary>
    public static string Rate(decimal fraction) => (fraction * 100m).ToString("0.###", CultureInfo.InvariantCulture) + "%";

    public static string Error(Exception ex) => AccountingFormat.Error(ex);

    public static bool IsForbidden(Exception ex) => AccountingFormat.IsForbidden(ex);

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
        || ex.GetType().Name.Contains("NotFound", StringComparison.Ordinal);
}
