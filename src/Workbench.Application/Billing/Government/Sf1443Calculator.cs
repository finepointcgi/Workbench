using Workbench.Application.Financials;
using Workbench.Contracts.Billing;

namespace Workbench.Application.Billing;

/// <summary>The figures an SF 1443 is computed from: section I, the costs, and the lines Finance can change.</summary>
public sealed record Sf1443Inputs(
    string To,
    string From,
    bool IsSmallBusiness,
    string ContractNumber,
    decimal ContractPrice,
    decimal ProgressPaymentRate,
    decimal LiquidationRate,
    DateOnly InitialAwardDate,
    int RequestNumber,
    DateOnly RequestDate,
    DateOnly CostsThrough,
    ProgressPaymentBasis CostBasis,
    decimal PaidCosts,
    decimal UnpaidCosts,
    decimal IncurredToDate,
    decimal EstimateToComplete,
    decimal SubcontractorPaid,
    decimal SubcontractorLiquidated,
    decimal SubcontractorApproved,
    decimal PreviousRequested,
    decimal CostsOfDelivered,
    decimal PriceOfDelivered,
    decimal UnliquidatedAdvances,
    decimal LiquidatedToDate);

/// <summary>
/// The arithmetic of the Contractor's Request for Progress Payment (SF 1443, REV. 10/2023) under FAR 52.232-16.
/// <list type="bullet">
/// <item>Line 11 is the eligible cost: paid costs only, or paid and unpaid, by the contract's cost basis (lines 9 and 10 are
/// reserved on the current form).</item>
/// <item>Line 13 = 11 × 6a; when the estimate at completion (12a + 12b) is above the contract price, it is reduced by the
/// loss ratio, contract price ÷ estimate at completion (FAR 32.503-6(g)).</item>
/// <item>Line 26, the amount requested, is the lesser of line 19 (the cost limit) and line 25 (the limit on outstanding
/// progress payments), never below zero.</item>
/// </list>
/// Amounts round to the cent at each multiplied line, as on the printed form.
/// </summary>
public static class Sf1443Calculator
{
    public static Sf1443Dto Compute(Sf1443Inputs i)
    {
        var notes = new List<string>();
        var line11 = i.CostBasis == ProgressPaymentBasis.PaidCosts ? i.PaidCosts : i.PaidCosts + i.UnpaidCosts;

        var estimateAtCompletion = i.IncurredToDate + i.EstimateToComplete;
        var lossRatio = i.ContractPrice > 0 && estimateAtCompletion > i.ContractPrice;
        var line13 = Money.Round(line11 * i.ProgressPaymentRate);
        if (lossRatio)
        {
            line13 = Money.Round(line11 * i.ProgressPaymentRate * i.ContractPrice / estimateAtCompletion);
            notes.Add($"Loss ratio applied to line 13: the estimate at completion ({estimateAtCompletion:N2}) is above the contract price ({i.ContractPrice:N2}).");
        }

        var line14c = i.SubcontractorPaid - i.SubcontractorLiquidated;
        var line14e = line14c + i.SubcontractorApproved;
        var line15 = line13 + line14e;
        var line16 = Money.Round(i.ContractPrice * i.LiquidationRate);
        var line17 = Math.Min(line15, line16);
        var line19 = line17 - i.PreviousRequested;

        var line20b = line11 - i.CostsOfDelivered;
        var line20c = Money.Round(line20b * i.ProgressPaymentRate);
        var line20d = line14e;
        var line20e = line20c + line20d;
        var line21b = i.ContractPrice - i.PriceOfDelivered;
        var line21c = Money.Round(line21b * i.LiquidationRate);
        var line21e = line21c - i.UnliquidatedAdvances;
        var line22 = Math.Min(line20e, line21e);
        var line24 = i.PreviousRequested - i.LiquidatedToDate;
        var line25 = line22 - line24;
        var line26 = Math.Max(0, Math.Min(line19, line25));

        if (i.CostBasis == ProgressPaymentBasis.PaidCosts && i.UnpaidCosts > 0)
        {
            notes.Add($"Paid costs only (large business): {i.UnpaidCosts:N2} of costs not yet paid are left out of line 11.");
        }

        if (line26 == 0)
        {
            notes.Add(line19 <= 0
                ? "Nothing can be requested: earlier requests already reach the limit on line 17."
                : "Nothing can be requested: unliquidated progress payments already reach the limit on line 22.");
        }
        else if (line26 < 2_500)
        {
            notes.Add("FAR 52.232-16 says not to request less than $2,500 at a time; consider waiting for the next request.");
        }

        return new Sf1443Dto(
            i.To, i.From, i.IsSmallBusiness, i.ContractNumber, i.ContractPrice, i.ProgressPaymentRate, i.LiquidationRate, i.InitialAwardDate,
            i.RequestNumber, i.RequestDate,
            i.CostsThrough, i.CostBasis, i.PaidCosts, i.UnpaidCosts, line11, i.IncurredToDate, i.EstimateToComplete, line13, lossRatio,
            i.SubcontractorPaid, i.SubcontractorLiquidated, line14c, i.SubcontractorApproved, line14e, line15, line16, line17, i.PreviousRequested, line19,
            i.CostsOfDelivered, line20b, line20c, line20d, line20e, i.PriceOfDelivered, line21b, line21c, i.UnliquidatedAdvances, line21e, line22,
            i.LiquidatedToDate, line24, line25,
            line26,
            notes);
    }
}
