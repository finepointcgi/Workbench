using System.Globalization;
using Workbench.Contracts.Currencies;

namespace Workbench.Ui.Currencies;

/// <summary>Display helpers for amounts in other currencies.</summary>
public static class CurrencyFormat
{
    /// <summary>"12,000.00 GBP" (no symbol, so it reads the same for every currency).</summary>
    public static string Amount(decimal amount, string? code, int decimals = 2) =>
        $"{amount.ToString("N" + decimals, CultureInfo.InvariantCulture)} {code}".TrimEnd();

    public static string Rate(decimal rate) => rate.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Kind(RateKind kind) => kind switch
    {
        RateKind.MonthEnd => "month-end",
        RateKind.Average => "average",
        _ => "spot",
    };

    public static string Label(RateKind kind) => kind switch
    {
        RateKind.MonthEnd => "Month-end",
        RateKind.Average => "Average",
        _ => "Spot",
    };

    public static string Label(RevaluationKind kind) => kind switch
    {
        RevaluationKind.Receivable => "Receivable",
        RevaluationKind.Payable => "Payable",
        _ => "Bank",
    };

    /// <summary>"Gain 12.34" / "Loss 5.00" (so the sign isn't the only signal).</summary>
    public static string GainLoss(decimal value) =>
        value == 0 ? "None" : $"{(value > 0 ? "Gain" : "Loss")} {Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture)}";
}
