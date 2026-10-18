using System.Globalization;
using Workbench.Application.Financials;
using Workbench.Application.Scheduling;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;

namespace Workbench.Application.Evm;

/// <summary>What is known about a work package's progress at a status date.</summary>
/// <param name="Started">Work has begun (status past "Not started", a % complete above 0, or a milestone met).</param>
/// <param name="Complete">The work is done.</param>
/// <param name="PercentComplete">0–100, for the percent-complete technique.</param>
/// <param name="MilestoneWeightDone">Sum of the weights (percent) of the milestones met.</param>
/// <param name="ApportionedBasePercent">The % complete of the work package an apportioned one follows.</param>
public sealed record EvProgress(
    bool Started = false,
    bool Complete = false,
    decimal PercentComplete = 0,
    decimal MilestoneWeightDone = 0,
    decimal ApportionedBasePercent = 0);

/// <summary>The earned value arithmetic: planned value phasing, earned % per technique, indices and EAC methods.</summary>
public static class EvmCalculator
{
    /// <summary>
    /// Cumulative planned value (BCWS) through <paramref name="asOf"/> (inclusive): the budget spread evenly over the
    /// Mon–Fri working days from <paramref name="start"/> to <paramref name="finish"/> (calendar days if there are none).
    /// </summary>
    public static decimal PlannedValue(decimal budget, DateOnly start, DateOnly finish, DateOnly asOf)
    {
        if (finish < start)
        {
            (start, finish) = (finish, start);
        }

        if (asOf < start)
        {
            return 0;
        }

        if (asOf >= finish)
        {
            return budget;
        }

        var total = WorkingDays.Index(finish.AddDays(1)) - WorkingDays.Index(start);
        var done = WorkingDays.Index(asOf.AddDays(1)) - WorkingDays.Index(start);
        if (total <= 0)
        {
            total = finish.DayNumber - start.DayNumber + 1;
            done = asOf.DayNumber - start.DayNumber + 1;
        }

        return Money.Round(budget * Math.Clamp(done, 0, total) / total);
    }

    /// <summary>The % (0–100) of the budget earned by <paramref name="technique"/>.</summary>
    /// <param name="plannedPercent">BCWS as a % of the budget, for level of effort.</param>
    public static decimal EarnedPercent(EarnedValueTechnique technique, EvProgress progress, decimal plannedPercent) =>
        Math.Clamp(technique switch
        {
            EarnedValueTechnique.ZeroHundred => progress.Complete ? 100m : 0m,
            EarnedValueTechnique.FiftyFifty => progress.Complete ? 100m : progress.Started ? 50m : 0m,
            EarnedValueTechnique.PercentComplete => progress.Complete ? 100m : progress.PercentComplete,
            EarnedValueTechnique.Milestones => progress.MilestoneWeightDone,
            EarnedValueTechnique.LevelOfEffort => plannedPercent,
            EarnedValueTechnique.Apportioned => progress.ApportionedBasePercent,
            _ => 0m,
        }, 0m, 100m);

    /// <summary>BCWP for a work package: the budget × the earned %.</summary>
    public static decimal Earned(decimal budget, decimal earnedPercent) => Money.Round(budget * earnedPercent / 100m);

    public static decimal PercentOf(decimal part, decimal whole) => whole == 0 ? 0 : Math.Round(part / whole * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Cumulative metrics. <paramref name="managerEac"/> null uses EAC (CPI).</summary>
    public static EvmMetrics Metrics(decimal bac, decimal bcws, decimal bcwp, decimal acwp, decimal? managerEac = null)
    {
        var sv = bcwp - bcws;
        var cv = bcwp - acwp;
        var spi = Ratio(bcwp, bcws);
        var cpi = Ratio(bcwp, acwp);
        var remaining = bac - bcwp;

        // With no cost index yet (nothing spent), the remaining work is assumed to cost its budget.
        var eacCpi = Money.Round(acwp + (cpi is > 0 ? remaining / cpi.Value : remaining));
        var compound = cpi * spi;
        var eacCpiSpi = Money.Round(acwp + (compound is > 0 ? remaining / compound.Value : remaining));
        var eacManager = Money.Round(managerEac ?? eacCpi);
        var eac = eacManager;
        return new EvmMetrics(
            bac, bcws, bcwp, acwp, sv, cv,
            Percent(sv, bcws), Percent(cv, bcwp),
            spi, cpi,
            eacCpi, eacCpiSpi, eacManager,
            eac, eac - acwp, bac - eac,
            bac - acwp > 0 ? Round4(remaining / (bac - acwp)) : null,
            eac - acwp > 0 ? Round4(remaining / (eac - acwp)) : null,
            PercentOf(bcwp, bac), PercentOf(acwp, bac));
    }

    /// <summary>
    /// Why a control account needs a variance analysis: |SV| or |CV| (or VAC) beyond both <paramref name="thresholdPercent"/>
    /// (of BCWS, BCWP or BAC) and <paramref name="thresholdAmount"/> dollars.
    /// </summary>
    public static IReadOnlyList<string> VarianceReasons(EvmMetrics m, decimal thresholdPercent, decimal thresholdAmount)
    {
        var reasons = new List<string>();
        void Check(string label, decimal amount, decimal basis)
        {
            if (basis == 0 && amount == 0)
            {
                return;
            }

            var pct = basis == 0 ? (decimal?)null : amount / basis * 100m;
            if (Math.Abs(amount) > thresholdAmount && (pct is null || Math.Abs(pct.Value) > thresholdPercent))
            {
                reasons.Add(pct is { } p
                    ? string.Create(CultureInfo.InvariantCulture, $"{label} {Signed(amount)} ({p:+0.0;-0.0}%) beyond ±{thresholdPercent:0.##}% and ±${thresholdAmount:N0}")
                    : string.Create(CultureInfo.InvariantCulture, $"{label} {Signed(amount)} beyond ±${thresholdAmount:N0}"));
            }
        }

        Check("SV", m.Sv, m.Bcws);
        Check("CV", m.Cv, m.Bcwp);
        Check("VAC", m.Vac, m.Bac);
        return reasons;
    }

    private static string Signed(decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"{(amount < 0 ? "-" : "+")}${Math.Abs(amount):N0}");

    private static decimal? Ratio(decimal numerator, decimal denominator) => denominator == 0 ? null : Round4(numerator / denominator);

    private static decimal? Percent(decimal numerator, decimal denominator) =>
        denominator == 0 ? null : Math.Round(numerator / denominator * 100m, 2, MidpointRounding.AwayFromZero);

    private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
