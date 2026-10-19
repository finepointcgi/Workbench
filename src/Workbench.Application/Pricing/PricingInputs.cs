using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.IndirectRates;
using Workbench.Application.PerDiem;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.Accounting;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Pricing;

/// <summary>Current employee cost rates (as of a date) and the labor-category statistics the pricing methods use.</summary>
internal sealed class LaborRateBook
{
    private readonly Dictionary<Guid, Employee> _employees;
    private readonly Dictionary<Guid, decimal> _rates;

    private LaborRateBook(DateOnly asOf, Dictionary<Guid, Employee> employees, Dictionary<Guid, decimal> rates)
    {
        AsOf = asOf;
        _employees = employees;
        _rates = rates;
    }

    public DateOnly AsOf { get; }

    public IReadOnlyCollection<Employee> Employees => _employees.Values;

    public static async Task<LaborRateBook> LoadAsync(IWorkbenchDbContext db, DateOnly asOf, CancellationToken ct)
    {
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var table = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().ToListAsync(ct))
        {
            table.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var rates = new Dictionary<Guid, decimal>();
        foreach (var id in employees.Keys)
        {
            if (table.Find(id, asOf) is { } rate)
            {
                rates[id] = rate;
            }
        }

        return new LaborRateBook(asOf, employees, rates);
    }

    public string NameOf(Guid employeeId) => _employees.TryGetValue(employeeId, out var e) ? e.DisplayName : "(unknown)";

    public decimal? RateOf(Guid employeeId) => _rates.TryGetValue(employeeId, out var r) ? r : null;

    /// <summary>Current rates of the active employees whose default labor category is <paramref name="laborCategoryId"/>.</summary>
    public IReadOnlyList<decimal> CategoryRates(Guid laborCategoryId) =>
        _employees.Values.Where(e => e.DefaultLaborCategoryId == laborCategoryId && e.IsActiveOn(AsOf) && _rates.ContainsKey(e.Id))
            .Select(e => _rates[e.Id]).OrderBy(r => r).ToList();

    public static decimal? Average(IReadOnlyList<decimal> rates) => rates.Count == 0 ? null : Money.RoundRate(rates.Average());

    public static decimal? Median(IReadOnlyList<decimal> rates)
    {
        if (rates.Count == 0)
        {
            return null;
        }

        var sorted = rates.OrderBy(r => r).ToList();
        var mid = sorted.Count / 2;
        return Money.RoundRate(sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2);
    }
}

/// <summary>Builds a <see cref="PricingInput"/> from a model and the books: cost rates, indirect rates, per diem.</summary>
internal static class PricingInputs
{
    public static async Task<PricingInput> BuildAsync(IWorkbenchDbContext db, Proposal proposal, PriceModel model, CancellationToken ct)
    {
        var notes = new List<string>();
        var book = await RateBook.LoadAsync(db, ct);
        var labor = await LaborRateBook.LoadAsync(db, model.RatesAsOf, ct);
        var perDiem = model.CostLines.Any(c => c.Kind == PriceCostKind.Travel) ? await PerDiemTables.LoadAsync(db, ct) : null;
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var escalations = model.Escalations.ToDictionary(e => e.LaborCategoryId, e => e.Rate);

        var periods = model.Periods.OrderBy(p => p.Order).ThenBy(p => p.Start)
            .Select(p => new PeriodInput(p.Id, p.Name, p.Order, p.Start, p.End, p.IsOption, RatesFor(book, model, p.Start, notes)))
            .ToList();
        if (book.Steps.Count == 0)
        {
            notes.Add("No indirect pools are set up, so the price carries no fringe, overhead or G&A.");
        }

        var laborInputs = new List<LaborInput>();
        foreach (var line in model.LaborLines.OrderBy(l => l.Order))
        {
            var category = categories.GetValueOrDefault(line.LaborCategoryId);
            var (rate, source) = DirectRate(line, labor, category, notes);
            var staff = line.EmployeeId is { } e ? labor.NameOf(e) : string.IsNullOrWhiteSpace(line.StaffName) ? "TBD" : line.StaffName!;
            laborInputs.Add(new LaborInput(line.Id, line.WbsId, line.ClinId, line.LaborCategoryId, category?.Code ?? "?", category?.Name ?? "(unknown category)",
                staff, source, rate, escalations.TryGetValue(line.LaborCategoryId, out var esc) ? esc : model.DefaultEscalation,
                line.Hours.GroupBy(h => h.PeriodId).ToDictionary(g => g.Key, g => g.Sum(h => h.Hours)), line.Order));
        }

        var periodById = model.Periods.ToDictionary(p => p.Id);
        var costs = model.CostLines.OrderBy(c => c.Order).Select(c =>
        {
            var (amount, detail) = c.Kind == PriceCostKind.Travel && periodById.TryGetValue(c.PeriodId, out var period)
                ? Travel(c, period, perDiem!)
                : (Money.Round(c.Quantity * c.UnitCost), Plain(c));
            return new CostInput(c.Id, c.WbsId, c.ClinId, c.PeriodId, c.Kind, c.Description, detail, amount, c.IsUnallowable, c.Order);
        }).ToList();

        var fee = new FeeTerms(model.FeeMethod, model.FeeRate, model.WgTechnicalRisk, model.WgManagementRisk, model.WgContractTypeRisk, model.WgCostEfficiency);
        return new PricingInput(model.Id, model.Name, model.IsLocked, model.Version, proposal.ContractType, model.RateBasis, model.RatesAsOf, proposal.TargetPrice, fee,
            book.Steps,
            periods,
            model.Clins.Select(c => new ClinInput(c.Id, c.Number, c.Description, c.Order)).ToList(),
            model.Wbs.Select(w => new WbsInput(w.Id, w.ParentId, w.Code, w.Name, w.BasisOfEstimate, w.Order)).ToList(),
            laborInputs,
            costs,
            notes.Distinct().ToList());
    }

    /// <summary>
    /// The rate set for a period: the fiscal year containing its start, on the model's basis; else the latest earlier year
    /// with that basis; else the same search on provisional rates. Overrides replace the book rate of their pool.
    /// </summary>
    internal static PeriodRateSet RatesFor(RateBook book, PriceModel model, DateOnly start, ICollection<string> notes)
    {
        var fiscalYear = FiscalYearOf(book, start);
        var found = Find(book, fiscalYear, model.RateBasis) ?? (model.RateBasis != IndirectRateBasis.Provisional ? Find(book, fiscalYear, IndirectRateBasis.Provisional) : null);
        var rates = new Dictionary<IndirectPool, decimal>();
        var ratesYear = fiscalYear;
        var basis = model.RateBasis;
        if (found is { } f)
        {
            (ratesYear, basis) = (f.Year, f.Basis);
            foreach (var step in book.Steps)
            {
                rates[step.Pool] = book.Rate(step.Pool, f.Year, f.Basis) ?? 0;
            }

            if (f.Basis != model.RateBasis)
            {
                notes.Add($"No {BasisText(model.RateBasis)} rates for FY{fiscalYear} or earlier; FY{f.Year} provisional rates are used.");
            }
            else if (f.Year != fiscalYear)
            {
                notes.Add($"FY{fiscalYear} has no {BasisText(model.RateBasis)} rates; FY{f.Year}'s are used.");
            }
        }
        else if (book.Steps.Count > 0)
        {
            notes.Add($"No indirect rates for FY{fiscalYear}; burden counts as 0. Set {BasisText(model.RateBasis)} rates under Indirect rates.");
        }

        var overridden = false;
        foreach (var (pool, value) in new[] { (IndirectPool.Fringe, model.FringeOverride), (IndirectPool.Overhead, model.OverheadOverride), (IndirectPool.GeneralAndAdministrative, model.GaOverride) })
        {
            if (value is { } v && book.Steps.Any(s => s.Pool == pool))
            {
                rates[pool] = v;
                overridden = true;
            }
        }

        return new PeriodRateSet(fiscalYear, ratesYear, basis, rates, overridden);
    }

    internal static string BasisText(IndirectRateBasis basis) => basis == IndirectRateBasis.ForwardPricing ? "forward pricing" : basis.ToString().ToLowerInvariant();

    /// <summary>The fiscal year containing <paramref name="date"/>, extending the calendar's pattern past its last year (Oct–Sep without one).</summary>
    internal static int FiscalYearOf(RateBook book, DateOnly date)
    {
        if (book.YearOf(date) is { } year)
        {
            return year.Year;
        }

        if (book.Years.Count == 0)
        {
            return PerDiemTables.FiscalYearOf(date);
        }

        var last = book.Years[^1];
        var first = book.Years[0];
        if (date > last.End)
        {
            for (var n = 1; n < 100; n++)
            {
                if (date <= last.End.AddYears(n))
                {
                    return last.Year + n;
                }
            }
        }
        else if (date < first.Start)
        {
            for (var n = 1; n < 100; n++)
            {
                if (date >= first.Start.AddYears(-n))
                {
                    return first.Year - n;
                }
            }
        }

        return PerDiemTables.FiscalYearOf(date);
    }

    private static (int Year, IndirectRateBasis Basis)? Find(RateBook book, int fiscalYear, IndirectRateBasis basis)
    {
        if (book.Steps.Count == 0)
        {
            return null;
        }

        for (var year = fiscalYear; year >= fiscalYear - 25; year--)
        {
            if (book.Steps.All(s => book.Rate(s.Pool, year, basis) is not null))
            {
                return (year, basis);
            }
        }

        return null;
    }

    private static (decimal Rate, string Source) DirectRate(PriceLaborLine line, LaborRateBook book, LaborCategory? category, ICollection<string> notes)
    {
        var code = category?.Code ?? "?";
        switch (line.RateMethod)
        {
            case LaborRateMethod.Manual:
                return (line.ManualRate ?? 0, "Entered rate");
            case LaborRateMethod.NamedEmployee when line.EmployeeId is { } employee:
                if (book.RateOf(employee) is { } own)
                {
                    return (own, $"{book.NameOf(employee)}'s cost rate");
                }

                notes.Add($"{book.NameOf(employee)} has no cost rate on {book.AsOf:yyyy-MM-dd}; their line is priced at $0.");
                return (0, $"{book.NameOf(employee)} (no cost rate)");
            case LaborRateMethod.SelectedPeople:
                var ids = PricingService.ParseIds(line.SelectedEmployeeIds);
                var rates = ids.Select(book.RateOf).OfType<decimal>().ToList();
                if (LaborRateBook.Average(rates) is { } avg)
                {
                    return (avg, $"Average of {rates.Count} selected {(rates.Count == 1 ? "person" : "people")}");
                }

                notes.Add($"None of the people selected on a {code} line has a cost rate; it is priced at $0.");
                return (0, "Selected people (no cost rates)");
            default:
                var pool = book.CategoryRates(line.LaborCategoryId);
                var median = line.RateMethod == LaborRateMethod.CategoryMedian;
                if ((median ? LaborRateBook.Median(pool) : LaborRateBook.Average(pool)) is { } rate)
                {
                    return (rate, $"{(median ? "Median" : "Average")} of {pool.Count} {code} {(pool.Count == 1 ? "person" : "people")}");
                }

                if (line.ManualRate is { } fallback)
                {
                    return (fallback, $"Entered rate (no {code} employees)");
                }

                notes.Add($"No active employees in {code} have a cost rate; enter a rate on its lines.");
                return (0, $"No {code} employees");
        }
    }

    private static string Plain(PriceCostLine c)
    {
        var parts = new List<string> { $"{c.Quantity.ToString("0.####", CultureInfo.InvariantCulture)} × {c.UnitCost.ToString("C", Usd)}" };
        if (!string.IsNullOrWhiteSpace(c.Vendor))
        {
            parts.Add(c.Vendor!);
        }

        if (!string.IsNullOrWhiteSpace(c.QuoteReference))
        {
            parts.Add($"quote {c.QuoteReference}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Trips × travelers × (airfare + other + nights × lodging + M&amp;IE), the per diem for the destination at the middle
    /// of the period (FTR: first and last travel days at 75% M&amp;IE; a day trip with no night gets the 75% day only).
    /// </summary>
    internal static (decimal Amount, string Detail) Travel(PriceCostLine c, PricePeriod period, PerDiemTables tables)
    {
        var mid = period.Start.AddDays((period.End.DayNumber - period.Start.DayNumber) / 2);
        var destination = string.IsNullOrWhiteSpace(c.Destination) ? "Standard CONUS" : c.Destination!.Trim();
        var rate = tables.Resolve(new TravelDestinationDto(PerDiemTable.Conus, "US", (c.State ?? "").Trim(), destination, mid, mid.AddDays(Math.Max(c.Nights, 0))), mid);
        var nights = Math.Max(c.Nights, 0);
        var mie = nights == 0 ? rate.Tier.FirstLastDay : rate.MieRate * (nights - 1) + 2 * rate.Tier.FirstLastDay;
        var perTrip = c.AirfarePerTrip + c.OtherPerTrip + nights * rate.LodgingMax + mie;
        var amount = Money.Round(Math.Max(c.Trips, 0) * Math.Max(c.Travelers, 0) * perTrip);
        var detail = $"{c.Trips} trip{(c.Trips == 1 ? "" : "s")} × {c.Travelers} traveler{(c.Travelers == 1 ? "" : "s")} × {nights} night{(nights == 1 ? "" : "s")}, "
                     + $"{rate.Description}: lodging {rate.LodgingMax.ToString("C", Usd)}, M&IE {rate.MieRate.ToString("C", Usd)} (first/last day {rate.Tier.FirstLastDay.ToString("C", Usd)}), "
                     + $"airfare {c.AirfarePerTrip.ToString("C", Usd)}, other {c.OtherPerTrip.ToString("C", Usd)} per trip";
        if (rate.Notes.Count > 0)
        {
            detail += ". " + string.Join(" ", rate.Notes);
        }

        return (amount, detail);
    }

    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");
}
