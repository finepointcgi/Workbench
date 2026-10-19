using System.Globalization;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.IndirectRates;

public static class IndirectRatesFormat
{
    public static readonly CostPool[] Pools = [CostPool.Fringe, CostPool.Overhead, CostPool.GeneralAndAdministrative];

    public static readonly RateBasis[] Bases = [RateBasis.Provisional, RateBasis.Target, RateBasis.Actual, RateBasis.ForwardPricing];

    public static string Label(RateBasis basis) => basis == RateBasis.ForwardPricing ? "Forward pricing" : basis.ToString();

    /// <summary>A fraction as a percentage (0.32 → 32.00%); a dash for null.</summary>
    public static string Percent(decimal? rate) => rate is { } r ? (r * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%" : "—";

    public static string Label(AllocationBaseKind value) => value switch
    {
        AllocationBaseKind.DirectLabor => "Direct labor",
        AllocationBaseKind.DirectLaborPlusFringe => "Direct labor + fringe",
        AllocationBaseKind.TotalCostInput => "Total cost input",
        AllocationBaseKind.ValueAdded => "Value added",
        _ => value.ToString(),
    };

    public static string Rates(AppliedRates r) =>
        $"FY{r.FiscalYear} {r.Basis.ToString().ToLowerInvariant()}: fringe {Percent(r.Fringe)}, overhead {Percent(r.Overhead)}, G&A {Percent(r.GeneralAndAdministrative)}";

    public static IReadOnlyList<int> FiscalYears(IEnumerable<Workbench.Contracts.ProjectAccounting.FiscalPeriodDto> periods) =>
        periods.Select(p => p.FiscalYear).Distinct().OrderByDescending(y => y).ToList();

    public static int DefaultYear(IReadOnlyList<Workbench.Contracts.ProjectAccounting.FiscalPeriodDto> periods) =>
        AccountingFormat.DefaultPeriod(periods)?.FiscalYear ?? periods.Select(p => p.FiscalYear).DefaultIfEmpty(DateTime.Today.Year).Max();
}
