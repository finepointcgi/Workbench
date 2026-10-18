using Workbench.Application.Financials;
using Workbench.Domain.Accounting;

namespace Workbench.Application.IndirectRates;

/// <summary>One pool in the cascade: what it is spread over. Steps are applied in list order.</summary>
public sealed record PoolStep(IndirectPool Pool, AllocationBase Base);

/// <summary>The burden on one cost: each pool's applied amount, rounded to the cent.</summary>
public sealed record Burden(decimal Fringe, decimal Overhead, decimal GeneralAndAdministrative)
{
    public static readonly Burden Zero = new(0, 0, 0);

    public decimal Total => Fringe + Overhead + GeneralAndAdministrative;

    public decimal this[IndirectPool pool] => pool switch
    {
        IndirectPool.Fringe => Fringe,
        IndirectPool.Overhead => Overhead,
        _ => GeneralAndAdministrative,
    };

    public static Burden From(IReadOnlyDictionary<IndirectPool, decimal> applied) => new(
        applied.GetValueOrDefault(IndirectPool.Fringe),
        applied.GetValueOrDefault(IndirectPool.Overhead),
        applied.GetValueOrDefault(IndirectPool.GeneralAndAdministrative));

    public Burden Add(Burden o) => new(Fringe + o.Fringe, Overhead + o.Overhead, GeneralAndAdministrative + o.GeneralAndAdministrative);

    public Burden Minus(Burden o) => new(Fringe - o.Fringe, Overhead - o.Overhead, GeneralAndAdministrative - o.GeneralAndAdministrative);
}

/// <summary>
/// The cascade: each pool's base is built from direct labor, direct ODCs and the burden of the pools before it, so with
/// the usual setup fringe = DL × f, overhead = (DL + fringe) × o and G&amp;A = (DL + fringe + overhead + ODC) × g.
/// </summary>
public static class BurdenCascade
{
    public static decimal BaseOf(AllocationBase basis, decimal directLabor, decimal directOdc, IReadOnlyDictionary<IndirectPool, decimal> earlier) => basis switch
    {
        AllocationBase.DirectLabor => directLabor,
        AllocationBase.DirectLaborPlusFringe => directLabor + earlier.GetValueOrDefault(IndirectPool.Fringe),
        AllocationBase.TotalCostInput => directLabor + directOdc + earlier.Values.Sum(),
        AllocationBase.ValueAdded => directLabor + earlier.Values.Sum(),
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };

    /// <summary>Burdens one cost: each pool's base × its rate, rounded to the cent before the next pool uses it.</summary>
    public static Burden Apply(IReadOnlyList<PoolStep> steps, decimal directLabor, decimal directOdc, Func<IndirectPool, decimal> rate)
    {
        var applied = new Dictionary<IndirectPool, decimal>();
        foreach (var step in steps)
        {
            applied[step.Pool] = Money.Round(BaseOf(step.Base, directLabor, directOdc, applied) * rate(step.Pool));
        }

        return Burden.From(applied);
    }

    /// <summary>Unrounded burden per dollar of direct labor (1 + all pools), for estimating burden on future labor.</summary>
    public static decimal LaborMultiplier(IReadOnlyList<PoolStep> steps, Func<IndirectPool, decimal> rate)
    {
        var applied = new Dictionary<IndirectPool, decimal>();
        foreach (var step in steps)
        {
            applied[step.Pool] = BaseOf(step.Base, 1m, 0m, applied) * rate(step.Pool);
        }

        return 1m + applied.Values.Sum();
    }

    /// <summary>
    /// Actual rates from pool cost and the direct base: each pool's rate is its cost over its base, where the burden of
    /// earlier pools in the base is their full pool cost (which is what their actual rates apply). Null when the base is 0.
    /// </summary>
    public static IReadOnlyList<(IndirectPool Pool, decimal Base, decimal? Rate)> ActualRates(
        IReadOnlyList<PoolStep> steps, decimal directLabor, decimal directOdc, Func<IndirectPool, decimal> poolCost)
    {
        var applied = new Dictionary<IndirectPool, decimal>();
        var result = new List<(IndirectPool, decimal, decimal?)>();
        foreach (var step in steps)
        {
            var b = BaseOf(step.Base, directLabor, directOdc, applied);
            var cost = poolCost(step.Pool);
            result.Add((step.Pool, b, b == 0 ? null : Math.Round(cost / b, 6, MidpointRounding.AwayFromZero)));
            applied[step.Pool] = cost;
        }

        return result;
    }
}
