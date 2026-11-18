namespace Workbench.Domain.Planning;

// Stays in core: the corporate budgets and recurring journals (the accounting module) and landed cost (the materials module) both spread money with it.
/// <summary>Splitting money in whole cents.</summary>
public static class Allocation
{
    /// <summary>
    /// Splits <paramref name="total"/> by <paramref name="weights"/> (any positive scale) into whole cents that add up to
    /// exactly the total: each share is rounded down, then the leftover cents go to the largest remainders (earliest first on ties).
    /// All-zero weights split evenly.
    /// </summary>
    public static IReadOnlyList<decimal> Split(decimal total, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        var w = weights.Sum() == 0 ? weights.Select(_ => 1m).ToList() : weights.ToList();
        var sum = w.Sum();
        var cents = decimal.Round(total * 100m, 0, MidpointRounding.AwayFromZero);
        var sign = cents < 0 ? -1m : 1m;
        cents = Math.Abs(cents);
        var raw = w.Select(x => cents * x / sum).ToList();
        var floors = raw.Select(decimal.Floor).ToList();
        var left = cents - floors.Sum();
        foreach (var i in raw.Select((r, i) => (Remainder: r - floors[i], i)).OrderByDescending(x => x.Remainder).ThenBy(x => x.i).Take((int)left).Select(x => x.i).ToList())
        {
            floors[i] += 1;
        }

        return floors.Select(f => sign * f / 100m).ToList();
    }
}
