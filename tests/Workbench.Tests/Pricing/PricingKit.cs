using Workbench.Application.Pricing;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Pricing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.IndirectRates;

namespace Workbench.Tests.Pricing;

/// <summary>
/// Pricing on top of <see cref="IndirectRatesKit"/>'s books: FY2026/FY2027 calendars (Oct–Sep), the default pools, Dana
/// (ENG2, $100/h), contract W91-26-C-0001 with project 2001, and today = 2026-11-15.
/// </summary>
internal sealed class PricingKit
{
    private PricingKit(IndirectRatesKit books)
    {
        Books = books;
        Pricing = new PricingService(books.Factory, books.Audit, books.Clock);
    }

    public IndirectRatesKit Books { get; }
    public PricingService Pricing { get; }
    public LaborCategory Category => Books.Category;

    public static DateOnly FyStart => IndirectRatesKit.October;

    public static async Task<PricingKit> CreateAsync() => new(await IndirectRatesKit.CreateAsync());

    public async Task<Employee> AddEmployeeAsync(string name, decimal rate, LaborCategory? category = null, DateOnly? from = null)
    {
        await using var db = await Books.Factory.CreateAsync();
        var employee = new Employee
        {
            EmployeeNumber = "E" + Guid.NewGuid().ToString("N")[..6], Email = $"{name.ToLowerInvariant()}@example.com", DisplayName = name,
            DefaultLaborCategoryId = (category ?? Category).Id, HireDate = new DateOnly(2020, 1, 1),
        };
        db.Set<Employee>().Add(employee);
        db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = employee.Id, EffectiveFrom = from ?? new DateOnly(2020, 1, 1), HourlyCost = rate });
        await db.SaveChangesAsync();
        return employee;
    }

    public async Task<LaborCategory> AddCategoryAsync(string code)
    {
        await using var db = await Books.Factory.CreateAsync();
        var category = new LaborCategory { Code = code, Name = code };
        db.Set<LaborCategory>().Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    /// <summary>A proposal whose working price starts on 2026-10-01 (FY2027), with <paramref name="options"/> option years.</summary>
    public async Task<(ProposalDto Proposal, PriceModelDto Model)> NewAsync(PricingContractKind type = PricingContractKind.CostPlusFixedFee, int options = 0,
        decimal? target = null, string number = "PRP-1")
    {
        var proposal = await Pricing.CreateProposalAsync(new SaveProposalRequest(number, "Modernization recompete", type, Customer: "U.S. Army", TargetPrice: target,
            FirstPeriodStart: FyStart, OptionYears: options));
        var model = await Pricing.GetModelAsync(proposal.Models.Single().Id);
        return (proposal, model);
    }

    /// <summary>Sets the basis, fee and escalation (no escalation and no fee unless given).</summary>
    public Task<PriceModelDto> SettingsAsync(PriceModelDto model, RateBasis basis = RateBasis.Provisional, decimal fee = 0m, decimal escalation = 0m,
        FeeMethodKind method = FeeMethodKind.FlatPercent, decimal? fringe = null, decimal? overhead = null, decimal? ga = null) =>
        Pricing.SaveSettingsAsync(model.Id, new SavePriceSettingsRequest(model.Settings with
        {
            RateBasis = basis, FeeRate = fee, DefaultEscalation = escalation, FeeMethod = method, FringeOverride = fringe, OverheadOverride = overhead, GaOverride = ga,
        }));

    public Task<PriceModelDto> LaborAsync(PriceModelDto model, params SavePriceLaborLineRequest[] lines) => Pricing.SaveLaborLinesAsync(model.Id, lines);

    public SavePriceLaborLineRequest Line(PriceModelDto model, LaborRateSource method, decimal[] hours, decimal? manualRate = null, Guid? employeeId = null,
        IReadOnlyList<Guid>? selected = null, LaborCategory? category = null) =>
        new(null, model.Wbs[0].Id, model.Clins[0].Id, (category ?? Category).Id, method,
            model.Periods.Zip(hours).ToDictionary(x => x.First.Id, x => x.Second), employeeId, null, selected, manualRate);

    public Task SetRatesAsync(int year, RateBasis basis, decimal fringe, decimal overhead, decimal ga) => Books.SetRatesAsync(year, basis, fringe, overhead, ga);
}
