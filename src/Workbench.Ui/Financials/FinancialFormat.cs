using System.Globalization;

namespace Workbench.Ui.Financials;

/// <summary>US-dollar formatting for the financials screens.</summary>
public static class FinancialFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C0", Culture);

    public static string MoneyCents(decimal value) => value.ToString("C2", Culture);

    public static string Hours(decimal value) => value.ToString("N1", Culture);

    public static string Percent(decimal? value) => value is { } v ? v.ToString("P0", Culture) : "—";

    /// <summary>Red for an overrun (negative variance).</summary>
    public static string VarianceClass(decimal variance) => variance < 0 ? "mud-error-text" : "mud-success-text";

    public static string SpentClass(decimal? percent) => percent > 1 ? "mud-error-text" : "";
}
