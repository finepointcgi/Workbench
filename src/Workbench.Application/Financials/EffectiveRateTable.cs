namespace Workbench.Application.Financials;

/// <summary>
/// Effective-dated rates per key (an employee's cost rate, a labor category's bill rate). A rate applies from its
/// EffectiveFrom date until the next rate for the same key starts.
/// </summary>
public sealed class EffectiveRateTable<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, List<(DateOnly From, decimal Rate)>> _rates = [];

    public EffectiveRateTable<TKey> Add(TKey key, DateOnly effectiveFrom, decimal rate)
    {
        if (!_rates.TryGetValue(key, out var list))
        {
            _rates[key] = list = [];
        }

        // Keep the list ordered by date; a rate with the same start date goes after the earlier one.
        var index = list.FindLastIndex(r => r.From <= effectiveFrom) + 1;
        list.Insert(index, (effectiveFrom, rate));
        return this;
    }

    /// <summary>The rate in effect for <paramref name="key"/> on <paramref name="date"/>, or null before its first rate.</summary>
    public decimal? Find(TKey key, DateOnly date)
    {
        if (!_rates.TryGetValue(key, out var list))
        {
            return null;
        }

        decimal? found = null;
        foreach (var (from, rate) in list)
        {
            if (from > date)
            {
                break;
            }

            // The same start date twice is a correction: the later-added one wins.
            found = rate;
        }

        return found;
    }
}

public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundRate(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
