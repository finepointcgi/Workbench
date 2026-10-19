using System.Globalization;
using Workbench.Application.Financials;
using Workbench.Application.IndirectRates;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Pricing;

namespace Workbench.Application.Pricing;

/// <summary>The indirect rates for one period: the fiscal year they belong to and the year/basis they were taken from.</summary>
public sealed record PeriodRateSet(int FiscalYear, int RatesFiscalYear, IndirectRateBasis Basis, IReadOnlyDictionary<IndirectPool, decimal> Rates, bool Overridden)
{
    public decimal Of(IndirectPool pool) => Rates.GetValueOrDefault(pool);
}

public sealed record PeriodInput(Guid Id, string Name, int Order, DateOnly Start, DateOnly End, bool IsOption, PeriodRateSet Rates);

public sealed record ClinInput(Guid Id, string Number, string Description, int Order);

public sealed record WbsInput(Guid Id, Guid? ParentId, string Code, string Name, string? BasisOfEstimate, int Order);

/// <summary>A labor line with its direct rate already resolved from the books (as of the model's rate date).</summary>
public sealed record LaborInput(
    Guid Id, Guid WbsId, Guid? ClinId, Guid LaborCategoryId, string LaborCategoryCode, string LaborCategoryName, string Staff, string RateSource,
    decimal BaseRate, decimal Escalation, IReadOnlyDictionary<Guid, decimal> HoursByPeriod, int Order);

/// <summary>A non-labor cost with its amount resolved (travel from the per diem tables).</summary>
public sealed record CostInput(Guid Id, Guid WbsId, Guid? ClinId, Guid PeriodId, PriceCostKind Kind, string Description, string Detail, decimal Amount, bool IsUnallowable, int Order);

/// <summary>How fee or profit is set; <see cref="Rate"/> is the effective rate on total cost.</summary>
public sealed record FeeTerms(FeeMethod Method, decimal FlatRate, decimal Technical, decimal Management, decimal ContractTypeRisk, decimal CostEfficiency)
{
    /// <summary>DFARS 215.404-71: performance risk is technical (60%) and management/cost control (40%).</summary>
    public const decimal TechnicalWeight = 0.6m;

    public const decimal ManagementWeight = 0.4m;

    public decimal Rate => Method == FeeMethod.FlatPercent
        ? FlatRate
        : Math.Round(TechnicalWeight * Technical + ManagementWeight * Management + ContractTypeRisk + CostEfficiency, 6, MidpointRounding.AwayFromZero);

    public string Explain() => Method == FeeMethod.FlatPercent
        ? $"Flat {Pct(FlatRate)} of total cost"
        : $"Weighted guidelines: performance risk {Pct(TechnicalWeight * Technical + ManagementWeight * Management)} (technical {Pct(Technical)} × 60%, "
          + $"management {Pct(Management)} × 40%) + contract type risk {Pct(ContractTypeRisk)} + cost efficiency {Pct(CostEfficiency)} = {Pct(Rate)} of total cost";

    /// <summary>DFARS 215.404-71-3 normal contract type risk values.</summary>
    public static decimal NormalContractTypeRisk(PricingContractType type) => type switch
    {
        PricingContractType.FirmFixedPrice => 0.05m,
        PricingContractType.CostPlusIncentiveFee => 0.01m,
        _ => 0.005m, // CPFF, CPAF, T&M and labor-hour
    };

    internal static string Pct(decimal rate) => (rate * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

public sealed record PricingInput(
    Guid ModelId,
    string ModelName,
    bool IsLocked,
    int? Version,
    PricingContractType ContractType,
    IndirectRateBasis Basis,
    DateOnly RatesAsOf,
    decimal? TargetPrice,
    FeeTerms Fee,
    IReadOnlyList<PoolStep> Steps,
    IReadOnlyList<PeriodInput> Periods,
    IReadOnlyList<ClinInput> Clins,
    IReadOnlyList<WbsInput> Wbs,
    IReadOnlyList<LaborInput> Labor,
    IReadOnlyList<CostInput> Costs,
    IReadOnlyList<string> Notes);

/// <summary>
/// Prices a model. Pure: every rate is already resolved into <see cref="PricingInput"/>.
/// <list type="number">
/// <item>Direct rates escalate once per full year between the rate date and the period's start: rate × (1 + e)^years, to 4 places.</item>
/// <item>Direct labor = hours × escalated rate, to the cent, per line and period.</item>
/// <item>Costs are summed per CLIN, period and WBS element, then burdened with <see cref="BurdenCascade.Apply"/> (the same
/// cascade, rounding and pool order as <c>IBurdenedCostService</c>) at the period's rates. Unallowable costs are left out.</item>
/// <item>Fee = total cost × the fee rate, to the cent. Time-and-materials and labor-hour labor is priced at the bill rates
/// (direct rate × the labor multiplier × (1 + fee), to 4 places); their ODCs, travel and materials at cost with G&amp;A, no fee.</item>
/// </list>
/// </summary>
public static class PriceCalculator
{
    public static PriceResultDto Calculate(PricingInput input, DateTime computedAtUtc)
    {
        var feeRate = input.Fee.Rate;
        var isTm = input.ContractType is PricingContractType.TimeAndMaterials or PricingContractType.LaborHour;
        var periods = input.Periods.OrderBy(p => p.Order).ThenBy(p => p.Start).ToList();
        var periodById = periods.ToDictionary(p => p.Id);
        var clinById = input.Clins.ToDictionary(c => c.Id);
        var wbsById = input.Wbs.ToDictionary(w => w.Id);
        var notes = new List<string>(input.Notes);

        // 1–2. Labor build-up.
        var labor = new List<(LaborInput Line, PeriodInput Period, decimal Hours, decimal Rate, decimal DirectLabor)>();
        foreach (var line in input.Labor.OrderBy(l => l.Order))
        {
            foreach (var period in periods)
            {
                var hours = line.HoursByPeriod.GetValueOrDefault(period.Id);
                if (hours == 0)
                {
                    continue;
                }

                var rate = EscalatedRate(line.BaseRate, line.Escalation, input.RatesAsOf, period.Start);
                labor.Add((line, period, hours, rate, Money.Round(hours * rate)));
            }
        }

        // 4. Bill rates per labor category and period (the hours-weighted direct rate, fully burdened, with fee).
        var billRates = new List<BillRateLineDto>();
        var billRateLookup = new Dictionary<(Guid Category, Guid Period), decimal>();
        foreach (var group in labor.GroupBy(l => (l.Line.LaborCategoryId, l.Period.Id)).OrderBy(g => g.First().Line.LaborCategoryCode, StringComparer.Ordinal).ThenBy(g => g.First().Period.Order))
        {
            var first = group.First();
            var hours = group.Sum(l => l.Hours);
            var direct = Money.RoundRate(group.Sum(l => l.Hours * l.Rate) / hours);
            var perDollar = BurdenPerDollar(input.Steps, first.Period.Rates);
            var fringe = Money.RoundRate(direct * perDollar.GetValueOrDefault(IndirectPool.Fringe));
            var overhead = Money.RoundRate(direct * perDollar.GetValueOrDefault(IndirectPool.Overhead));
            var ga = Money.RoundRate(direct * perDollar.GetValueOrDefault(IndirectPool.GeneralAndAdministrative));
            var multiplier = 1m + perDollar.Values.Sum();
            var bill = Money.RoundRate(direct * multiplier * (1 + feeRate));
            billRateLookup[group.Key] = bill;
            billRates.Add(new BillRateLineDto(first.Line.LaborCategoryId, first.Line.LaborCategoryCode, first.Line.LaborCategoryName, first.Period.Id, first.Period.Name,
                first.Period.Start, hours, direct, fringe, overhead, ga, bill - direct - fringe - overhead - ga, bill));
        }

        // 3. Burden and fee per CLIN × period × WBS.
        var unallowable = input.Costs.Where(c => c.IsUnallowable).Sum(c => c.Amount);
        var keys = labor.Select(l => (l.Line.ClinId, PeriodId: l.Period.Id, l.Line.WbsId))
            .Concat(input.Costs.Where(c => !c.IsUnallowable && periodById.ContainsKey(c.PeriodId)).Select(c => (c.ClinId, c.PeriodId, c.WbsId)))
            .Distinct()
            .ToList();
        var groups = new List<(Guid? ClinId, Guid PeriodId, Guid WbsId, PriceFiguresDto Figures, decimal LaborPrice, decimal LaborCost)>();
        foreach (var key in keys)
        {
            var period = periodById[key.PeriodId];
            var rows = labor.Where(l => l.Line.ClinId == key.ClinId && l.Period.Id == key.PeriodId && l.Line.WbsId == key.WbsId).ToList();
            var costs = input.Costs.Where(c => !c.IsUnallowable && c.ClinId == key.ClinId && c.PeriodId == key.PeriodId && c.WbsId == key.WbsId).ToList();
            decimal Kind(PriceCostKind kind) => costs.Where(c => c.Kind == kind).Sum(c => c.Amount);
            var (odc, travel, materials, subs) = (Kind(PriceCostKind.Odc), Kind(PriceCostKind.Travel), Kind(PriceCostKind.Materials), Kind(PriceCostKind.Subcontract));
            var directLabor = rows.Sum(r => r.DirectLabor);
            var directOther = odc + travel + materials + subs;
            var burden = BurdenCascade.Apply(input.Steps, directLabor, directOther, period.Rates.Of);
            var cost = directLabor + burden.Total + directOther;
            var multiplier = BurdenCascade.LaborMultiplier(input.Steps, period.Rates.Of);
            decimal fee, laborPrice;
            if (isTm)
            {
                laborPrice = rows.Sum(r => Money.Round(r.Hours * billRateLookup[(r.Line.LaborCategoryId, r.Period.Id)]));
                var otherBurden = BurdenCascade.Apply(input.Steps, 0, directOther, period.Rates.Of).Total;
                fee = laborPrice + directOther + otherBurden - cost;
            }
            else
            {
                fee = Money.Round(cost * feeRate);
                laborPrice = directLabor * multiplier * (1 + feeRate);
            }

            var figures = new PriceFiguresDto(rows.Sum(r => r.Hours), directLabor, burden.Fringe, burden.Overhead, burden.GeneralAndAdministrative,
                odc, travel, materials, subs, cost, fee, cost + fee);
            groups.Add((key.ClinId, key.PeriodId, key.WbsId, figures, laborPrice, directLabor * multiplier));
        }

        static PriceFiguresDto Sum(IEnumerable<PriceFiguresDto> figures) => figures.Aggregate(PriceFiguresDto.Zero, (a, f) => a.Add(f));

        var totals = Sum(groups.Select(g => g.Figures));

        // 5. Roll-ups: CLIN × period cells, periods, WBS (with parents).
        var cells = groups.GroupBy(g => (g.ClinId, g.PeriodId))
            .Select(g =>
            {
                var clin = g.Key.ClinId is { } id && clinById.TryGetValue(id, out var c) ? c : null;
                var period = periodById[g.Key.PeriodId];
                return (Order: clin?.Order ?? int.MaxValue, Number: clin?.Number ?? "", Period: period,
                    Cell: new PriceCellDto(clin?.Id, clin?.Number ?? "—", clin?.Description ?? "Not on a CLIN", period.Id, period.Name, Sum(g.Select(x => x.Figures))));
            })
            .OrderBy(c => c.Order).ThenBy(c => c.Number, StringComparer.Ordinal).ThenBy(c => c.Period.Order)
            .Select(c => c.Cell)
            .ToList();
        var byPeriod = periods.Select(p => new PriceFiguresByPeriodDto(p.Id, p.Name, p.Start, p.End, p.IsOption, Sum(groups.Where(g => g.PeriodId == p.Id).Select(g => g.Figures)))).ToList();

        var own = groups.GroupBy(g => g.WbsId).ToDictionary(g => g.Key, g => Sum(g.Select(x => x.Figures)));
        var children = input.Wbs.Where(w => w.ParentId is not null).ToLookup(w => w.ParentId!.Value);
        PriceFiguresDto Rolled(Guid id) => children[id].Aggregate(own.GetValueOrDefault(id) ?? PriceFiguresDto.Zero, (acc, child) => acc.Add(Rolled(child.Id)));
        var wbs = new List<WbsPriceDto>();
        void Walk(IEnumerable<WbsInput> level, int depth)
        {
            foreach (var w in level.OrderBy(w => w.Order).ThenBy(w => w.Code, StringComparer.Ordinal))
            {
                wbs.Add(new WbsPriceDto(w.Id, w.Code, w.Name, w.BasisOfEstimate, depth, Rolled(w.Id)));
                Walk(children[w.Id], depth + 1);
            }
        }

        Walk(input.Wbs.Where(w => w.ParentId is null || !wbsById.ContainsKey(w.ParentId.Value)), 0);

        // 6. Wrap rate, cost multiplier, margin, price-to-win.
        var wrap = totals.DirectLabor == 0 ? 0 : Math.Round(groups.Sum(g => g.LaborPrice) / totals.DirectLabor, 4, MidpointRounding.AwayFromZero);
        var costMultiplier = totals.DirectLabor == 0 ? 0 : Math.Round(groups.Sum(g => g.LaborCost) / totals.DirectLabor, 4, MidpointRounding.AwayFromZero);
        var margin = totals.Price == 0 ? 0 : Math.Round(totals.Fee / totals.Price, 4, MidpointRounding.AwayFromZero);
        var ptw = input.TargetPrice is { } target && target > 0 ? PriceToWin(target, totals) : null;

        if (input.ContractType is PricingContractType.CostPlusFixedFee or PricingContractType.CostPlusAwardFee or PricingContractType.CostPlusIncentiveFee && feeRate > 0.10m)
        {
            notes.Add($"The fee ({FeeTerms.Pct(feeRate)}) is above 10% of estimated cost, the FAR 15.404-4(c)(4)(i) limit for cost-plus-fixed-fee work other than research and development.");
        }

        if (unallowable != 0)
        {
            notes.Add($"{unallowable.ToString("C", CultureInfo.GetCultureInfo("en-US"))} of unallowable cost is excluded from the price and never burdened.");
        }

        var laborDtos = labor.Select(l => new LaborBuildUpDto(l.Line.Id, Code(wbsById, l.Line.WbsId), ClinNumber(clinById, l.Line.ClinId), l.Line.LaborCategoryId,
            l.Line.LaborCategoryCode, l.Line.LaborCategoryName, l.Line.Staff, l.Line.RateSource, l.Line.BaseRate, l.Line.Escalation, l.Period.Id, l.Period.Name,
            l.Hours, l.Rate, l.DirectLabor)).ToList();
        var costDtos = input.Costs.OrderBy(c => periodById.TryGetValue(c.PeriodId, out var p) ? p.Order : int.MaxValue).ThenBy(c => c.Order)
            .Select(c => new CostBuildUpDto(c.Id, Code(wbsById, c.WbsId), ClinNumber(clinById, c.ClinId), periodById.TryGetValue(c.PeriodId, out var p) ? p.Name : "?",
                (PriceCostType)(int)c.Kind, c.Description, c.Detail, c.Amount, c.IsUnallowable))
            .ToList();
        var periodRates = periods.Select(p => new PeriodRatesDto(p.Id, p.Name, p.Rates.FiscalYear, p.Rates.RatesFiscalYear, (RateBasis)(int)p.Rates.Basis,
            p.Rates.Of(IndirectPool.Fringe), p.Rates.Of(IndirectPool.Overhead), p.Rates.Of(IndirectPool.GeneralAndAdministrative),
            Math.Round(BurdenCascade.LaborMultiplier(input.Steps, p.Rates.Of), 6, MidpointRounding.AwayFromZero), p.Rates.Overridden)).ToList();
        var cascade = input.Steps.Select(s => new CascadeStepDto(RateBook.Label(s.Pool), BaseLabel(s.Base))).ToList();

        return new PriceResultDto(input.ModelId, input.ModelName, input.IsLocked, input.Version, computedAtUtc, (PricingContractKind)(int)input.ContractType,
            (RateBasis)(int)input.Basis, feeRate, input.Fee.Explain(), totals, unallowable, wrap, costMultiplier, margin, ptw, cascade, periodRates, cells, byPeriod, wbs,
            laborDtos, costDtos, billRates, notes);
    }

    /// <summary>The direct rate escalated once per full year from <paramref name="asOf"/> to <paramref name="periodStart"/>, to 4 places.</summary>
    public static decimal EscalatedRate(decimal baseRate, decimal escalation, DateOnly asOf, DateOnly periodStart)
    {
        var factor = 1m;
        for (var i = 0; i < EscalationYears(asOf, periodStart); i++)
        {
            factor *= 1 + escalation;
        }

        return Money.RoundRate(baseRate * factor);
    }

    /// <summary>Whole years from the rate date to the period's start (0 when the period starts within a year).</summary>
    public static int EscalationYears(DateOnly asOf, DateOnly periodStart)
    {
        if (periodStart <= asOf)
        {
            return 0;
        }

        var years = periodStart.Year - asOf.Year;
        if (periodStart.Month < asOf.Month || (periodStart.Month == asOf.Month && periodStart.Day < asOf.Day))
        {
            years--;
        }

        return Math.Max(0, years);
    }

    public static PriceToWinDto PriceToWin(decimal target, PriceFiguresDto totals)
    {
        var difference = totals.Price - target;
        return new PriceToWinDto(target, difference, Math.Round(difference / target, 4, MidpointRounding.AwayFromZero),
            totals.TotalCost > 0 ? Math.Round((target - totals.TotalCost) / totals.TotalCost, 4, MidpointRounding.AwayFromZero) : null,
            totals.Price <= target);
    }

    /// <summary>Each pool's burden on one dollar of direct labor (unrounded), in cascade order.</summary>
    public static IReadOnlyDictionary<IndirectPool, decimal> BurdenPerDollar(IReadOnlyList<PoolStep> steps, PeriodRateSet rates)
    {
        var applied = new Dictionary<IndirectPool, decimal>();
        foreach (var step in steps)
        {
            applied[step.Pool] = BurdenCascade.BaseOf(step.Base, 1m, 0m, applied) * rates.Of(step.Pool);
        }

        return applied;
    }

    public static string BaseLabel(AllocationBase basis) => basis switch
    {
        AllocationBase.DirectLabor => "Direct labor",
        AllocationBase.DirectLaborPlusFringe => "Direct labor + fringe",
        AllocationBase.TotalCostInput => "Total cost input",
        AllocationBase.ValueAdded => "Value added",
        _ => basis.ToString(),
    };

    private static string Code(Dictionary<Guid, WbsInput> wbs, Guid id) => wbs.TryGetValue(id, out var w) ? w.Code : "?";

    private static string? ClinNumber(Dictionary<Guid, ClinInput> clins, Guid? id) => id is { } c && clins.TryGetValue(c, out var clin) ? clin.Number : null;
}
