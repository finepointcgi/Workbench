using Microsoft.EntityFrameworkCore;
using Workbench.Domain.SalesTax;

namespace Workbench.Application.SalesTax;

/// <summary>Where tax is charged, as an engine sees it (an external engine would use the address parts or its own code).</summary>
public sealed record TaxJurisdictionRef(Guid Id, string Code, string Name, string State, string? County, string? City);

/// <summary>One amount to tax and how its tax code treats it.</summary>
public sealed record TaxLine(string Key, decimal Amount, TaxTreatment Treatment);

/// <summary>A sale (invoice) or a purchase (use tax) to calculate.</summary>
public sealed record TaxCalculationRequest(TaxJurisdictionRef Jurisdiction, DateOnly Date, IReadOnlyList<TaxLine> Lines, bool IsPurchase);

/// <summary>What the engine worked out: the taxable base, the combined rate and the tax by level (they add up to <see cref="Tax"/>).</summary>
public sealed record TaxCalculationResult(
    decimal TaxableAmount, decimal ExemptAmount, decimal Rate, decimal StateTax, decimal CountyTax, decimal CityTax, decimal SpecialTax, string Engine, string? Note = null)
{
    public decimal Tax => StateTax + CountyTax + CityTax + SpecialTax;
}

/// <summary>
/// Calculates sales and use tax. The built-in engine reads the rate tables (<see cref="TableSalesTaxEngine"/>). A host can
/// register another implementation (an Avalara or Vertex adapter, for example) in place of it; Workbench itself never calls
/// out to one.
/// </summary>
public interface ISalesTaxEngine
{
    string Name { get; }

    Task<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken ct);
}

/// <summary>
/// Sales tax arithmetic. The total is the taxable base times the combined rate, rounded half away from zero to the cent;
/// the state, county and city shares are rounded the same way and special districts take what's left, so the levels always
/// add up to the total.
/// </summary>
public static class SalesTaxMath
{
    public static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>On a sale, taxable and resale items are taxed; on a purchase, only taxable ones (resale and exempt items aren't).</summary>
    public static bool IsTaxed(TaxTreatment treatment, bool isPurchase) =>
        treatment == TaxTreatment.Taxable || (!isPurchase && treatment == TaxTreatment.Resale);

    public static TaxCalculationResult Calculate(TaxCalculationRequest request, TaxRate? rate, string engine)
    {
        var taxable = request.Lines.Where(l => IsTaxed(l.Treatment, request.IsPurchase)).Sum(l => l.Amount);
        var exempt = request.Lines.Where(l => !IsTaxed(l.Treatment, request.IsPurchase)).Sum(l => l.Amount);
        if (taxable < 0)
        {
            exempt += taxable;
            taxable = 0;
        }

        if (rate is null)
        {
            return new TaxCalculationResult(taxable, exempt, 0, 0, 0, 0, 0, engine,
                $"No rate for {request.Jurisdiction.Name} on {request.Date:yyyy-MM-dd}; add one under Setup › Sales tax.");
        }

        var total = Round(taxable * rate.CombinedRate);
        var state = Round(taxable * rate.StateRate);
        var county = Round(taxable * rate.CountyRate);
        var city = Round(taxable * rate.CityRate);
        return new TaxCalculationResult(taxable, exempt, rate.CombinedRate, state, county, city, total - state - county - city, engine);
    }
}

/// <summary>The built-in engine: the jurisdiction's rate in effect on the date, from the rate tables (Setup › Sales tax).</summary>
public sealed class TableSalesTaxEngine(IWorkbenchDbContextFactory dbFactory) : ISalesTaxEngine
{
    public const string EngineName = "Rate tables";

    public string Name => EngineName;

    public async Task<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var rate = await RateOnAsync(db, request.Jurisdiction.Id, request.Date, ct);
        return SalesTaxMath.Calculate(request, rate, Name);
    }

    /// <summary>The latest rate effective on or before the date.</summary>
    public static async Task<TaxRate?> RateOnAsync(IWorkbenchDbContext db, Guid jurisdictionId, DateOnly date, CancellationToken ct) =>
        (await db.Set<TaxRate>().AsNoTracking().Where(r => r.JurisdictionId == jurisdictionId && r.EffectiveDate <= date).ToListAsync(ct))
            .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.CreatedAtUtc).FirstOrDefault();
}
