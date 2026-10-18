using Workbench.Application.Financials;

namespace Workbench.Application.Revenue;

/// <summary>
/// The revenue arithmetic, free of the database so each method can be tested on its own. Every amount is rounded to
/// the cent; percent complete keeps six decimals.
/// </summary>
public static class RevenueCalculator
{
    /// <summary>Cost to date ÷ estimated cost at completion, between 0 and 1 (complete once cost reaches the estimate).</summary>
    public static decimal PercentComplete(decimal costToDate, decimal estimatedCost)
    {
        if (estimatedCost <= 0)
        {
            return costToDate > 0 ? 1m : 0m;
        }

        return Math.Clamp(costToDate / estimatedCost, 0m, 1m);
    }

    /// <summary>ASC 606 cost-to-cost: percent complete × the total transaction price (fee included). Cumulative.</summary>
    public static (decimal Percent, decimal Revenue) CostToCost(decimal costToDate, decimal estimatedCost, decimal transactionPrice)
    {
        var pct = PercentComplete(costToDate, estimatedCost);
        return (Math.Round(pct, 6, MidpointRounding.AwayFromZero), Money.Round(pct * transactionPrice));
    }

    /// <summary>Cost-plus: allowable burdened cost plus the fee earned on it.</summary>
    public static decimal CostPlusFee(decimal allowableCost, decimal feeRate) => Money.Round(allowableCost) + Money.Round(allowableCost * feeRate);

    /// <summary>T&amp;M as incurred: hours at bill rate, plus ODCs with the markup (0 = at cost).</summary>
    public static decimal TimeAndMaterials(IEnumerable<(decimal Hours, decimal BillRate)> labor, decimal odcCost, decimal odcMarkup) =>
        labor.Sum(l => Money.Round(l.Hours * l.BillRate)) + Money.Round(odcCost) + Money.Round(odcCost * odcMarkup);

    /// <summary>FFP units: units delivered × unit price, capped at the transaction price when there is one.</summary>
    public static decimal UnitsDelivered(decimal units, decimal unitPrice, decimal? transactionPrice)
    {
        var revenue = Money.Round(units * unitPrice);
        return transactionPrice is { } cap ? Math.Min(revenue, cap) : revenue;
    }

    /// <summary>The loss expected at completion: estimated cost over estimated revenue (0 when profitable).</summary>
    public static decimal ExpectedLoss(decimal estimatedCost, decimal estimatedRevenue) => Math.Max(0, Money.Round(estimatedCost - estimatedRevenue));

    /// <summary>
    /// The provision a loss contract carries: the full expected loss less the part already in the books as cost over
    /// revenue. Recognizing it means the whole loss is in the income statement as soon as it is expected; it unwinds to 0
    /// as the remaining cost is incurred.
    /// </summary>
    public static decimal LossProvision(decimal expectedLoss, decimal costToDate, decimal recognizedToDate)
    {
        if (expectedLoss <= 0)
        {
            return 0;
        }

        var alreadyRecognized = Math.Max(0, costToDate - recognizedToDate);
        return Math.Clamp(Money.Round(expectedLoss - alreadyRecognized), 0, expectedLoss);
    }

    /// <summary>
    /// The contract position: recognized less billed. Earned but not billed is a contract asset (AR unbilled); billed in
    /// excess of revenue is a contract liability.
    /// </summary>
    public static (decimal Unbilled, decimal BillingsInExcess) Position(decimal recognizedToDate, decimal billedToDate)
    {
        var net = Money.Round(recognizedToDate - billedToDate);
        return net >= 0 ? (net, 0) : (0, -net);
    }
}
