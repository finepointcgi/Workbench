using Workbench.Application.Currencies;

namespace Workbench.Tests.Currencies;

public sealed class CurrencyMathTests
{
    [Fact]
    public void Conversion_rounds_half_away_from_zero_to_the_cent()
    {
        Assert.Equal(1.01m, CurrencyMath.ToFunctional(201m, 0.005m)); // 1.005 → 1.01 (banker's rounding would give 1.00)
        Assert.Equal(-1.01m, CurrencyMath.ToFunctional(-201m, 0.005m));
        Assert.Equal(8_680m, CurrencyMath.ToFunctional(8_000m, 1.085m));
        Assert.Equal(366.25m, CurrencyMath.ToFunctional(333.33m, 1.0987654m)); // 366.2514...
    }

    [Fact]
    public void Transaction_amounts_round_to_the_currency_decimals()
    {
        Assert.Equal(14_925m, CurrencyMath.ToTransaction(100m, 0.0067m, 0)); // JPY has no minor unit
        Assert.Equal(12_000m, CurrencyMath.ToTransaction(15_180m, 1.265m, 2));
        Assert.Equal(909.09m, CurrencyMath.ToTransaction(1_000m, 1.1m, 2));
        Assert.Throws<ArgumentException>(() => CurrencyMath.ToTransaction(1m, 0m, 2));
    }

    [Fact]
    public void Rates_keep_eight_decimals_and_invert()
    {
        Assert.Equal(0.92165899m, CurrencyMath.Invert(1.085m));
        Assert.Equal(1.23456789m, CurrencyMath.RoundRate(1.234567891m));
    }

    [Fact]
    public void A_settling_payment_relieves_the_whole_functional_balance()
    {
        // Three payments of a third: the last one takes the rounding cent, so the document closes at zero.
        var (open, functional) = (100m, 110m);
        var first = CurrencyMath.Relieved(33.33m, open, functional, 1.1m);
        Assert.Equal(36.66m, first);
        (open, functional) = (open - 33.33m, functional - first);
        var second = CurrencyMath.Relieved(33.33m, open, functional, 1.1m);
        (open, functional) = (open - 33.33m, functional - second);
        var last = CurrencyMath.Relieved(open, open, functional, 1.1m);
        Assert.Equal(36.68m, last);
        Assert.Equal(110m, first + second + last);
    }
}
