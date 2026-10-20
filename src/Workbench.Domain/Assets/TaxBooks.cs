namespace Workbench.Domain.Assets;

/// <summary>
/// A depreciation book other than the asset's own book (the GAAP book on <see cref="FixedAsset"/>, the only one posted to
/// the ledger). Tax books are reporting books: federal, state and alternative minimum tax.
/// </summary>
public enum TaxBookKind
{
    Federal,
    State,
    Amt,
}

/// <summary>How a tax book depreciates.</summary>
public enum TaxDepreciationMethod
{
    /// <summary>MACRS general depreciation system: 200% declining balance for 3–10 year property, 150% for 15 and 20 year, straight line for 27.5 and 39 year real property.</summary>
    MacrsGds,

    /// <summary>MACRS 150% declining balance over the GDS recovery period (the AMT method, or an election).</summary>
    Macrs150Db,

    /// <summary>MACRS straight line over the recovery period (an election).</summary>
    StraightLine,

    /// <summary>MACRS alternative depreciation system: straight line over the ADS recovery period.</summary>
    MacrsAds,
}

/// <summary>The MACRS averaging convention for the year the asset is placed in service (and the year it's disposed of).</summary>
public enum TaxConvention
{
    HalfYear,

    /// <summary>Required for the year's personal property when more than 40% of it was placed in service in the last quarter.</summary>
    MidQuarter,

    /// <summary>Residential rental (27.5 year) and nonresidential real (39 year) property.</summary>
    MidMonth,
}

/// <summary>How an asset left: sold for cash, scrapped, or traded in toward a replacement.</summary>
public enum AssetDisposalKind
{
    Sale,
    Scrap,
    TradeIn,
}

/// <summary>
/// One tax book of an asset: its method, recovery period and convention, plus the first-year Section 179 expense and bonus
/// (special) depreciation. Depreciation is computed on demand, never posted. When the asset is disposed of, the book's
/// adjusted basis and gain or loss at that date are kept here.
/// </summary>
public sealed class AssetTaxBook : Entity
{
    public Guid AssetId { get; set; }
    public TaxBookKind Book { get; set; }
    public TaxDepreciationMethod Method { get; set; }

    /// <summary>Recovery period in years (for example 5, 7, 27.5 or an ADS life like 9.5).</summary>
    public decimal RecoveryYears { get; set; }

    public TaxConvention Convention { get; set; }

    /// <summary>Tax basis before Section 179 and bonus; null uses the asset's cost.</summary>
    public decimal? Basis { get; set; }

    /// <summary>Null uses the asset's in-service date.</summary>
    public DateOnly? PlacedInServiceDate { get; set; }

    /// <summary>Section 179 expense elected in the first year (federal and states that conform).</summary>
    public decimal Section179Amount { get; set; }

    /// <summary>Bonus (special) depreciation percentage of the basis left after Section 179, taken in the first year (0–100).</summary>
    public decimal BonusPercent { get; set; }

    public string? Notes { get; set; }

    // Kept when the asset is disposed of.
    public decimal? DisposalDepreciation { get; set; }
    public decimal? DisposalAdjustedBasis { get; set; }
    public decimal? DisposalGainLoss { get; set; }

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
