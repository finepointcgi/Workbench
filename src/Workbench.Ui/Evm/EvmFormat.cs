using Workbench.Contracts.Evm;
using Workbench.Ui.Financials;

namespace Workbench.Ui.Evm;

/// <summary>Formatting for the earned value screens.</summary>
public static class EvmFormat
{
    public static string Money(decimal value) => FinancialFormat.Money(value);

    public static string Money(decimal? value) => value is { } v ? FinancialFormat.Money(v) : "—";

    public static string Index(decimal? value) => value is { } v ? v.ToString("0.00", FinancialFormat.Culture) : "—";

    public static string Percent(decimal? value) => value is { } v ? $"{v.ToString("0.#", FinancialFormat.Culture)}%" : "—";

    /// <summary>Red below 0.95, amber below 1, green otherwise.</summary>
    public static string IndexClass(decimal? value) => value switch
    {
        null => "",
        < 0.95m => "mud-error-text",
        < 1m => "mud-warning-text",
        _ => "mud-success-text",
    };

    public static string VarianceClass(decimal value) => FinancialFormat.VarianceClass(value);

    public static string Technique(EvTechnique technique) => technique switch
    {
        EvTechnique.ZeroHundred => "0/100",
        EvTechnique.FiftyFifty => "50/50",
        EvTechnique.PercentComplete => "% complete",
        EvTechnique.Milestones => "Weighted milestones",
        EvTechnique.LevelOfEffort => "Level of effort",
        EvTechnique.Apportioned => "Apportioned",
        _ => technique.ToString(),
    };

    public static string State(EvmPeriodState state) => state switch
    {
        EvmPeriodState.Closed => "Closed",
        EvmPeriodState.Reporting => "Reporting",
        EvmPeriodState.Unstatused => "Not closed",
        _ => "Future",
    };
}
