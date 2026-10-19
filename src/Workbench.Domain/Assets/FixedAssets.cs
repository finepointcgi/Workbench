namespace Workbench.Domain.Assets;

/// <summary>Book depreciation methods. MACRS is tax-book only and never posted (see <see cref="FixedAsset.MacrsRecoveryYears"/>).</summary>
public enum DepreciationMethod
{
    StraightLine,

    /// <summary>Double-declining balance (200%), switching to straight line when that's larger, never below salvage.</summary>
    DecliningBalance,
    SumOfYearsDigits,
}

/// <summary>How much depreciation the in-service month gets.</summary>
public enum DepreciationConvention
{
    /// <summary>A whole month in the in-service month.</summary>
    FullMonth,

    /// <summary>Half a month in the in-service month; the other half after the last full month of life.</summary>
    MidMonth,

    /// <summary>Nothing in the in-service month; depreciation starts the month after.</summary>
    FollowingMonth,
}

public enum AssetStatus
{
    InService,
    Disposed,

    /// <summary>Transferred out (for example government property returned or sent to another contract).</summary>
    Transferred,
}

/// <summary>A kind of asset with its default accounts, method and useful life.</summary>
public sealed class AssetCategory : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public DepreciationMethod DefaultMethod { get; set; }
    public int DefaultLifeYears { get; set; } = 5;
    public DepreciationConvention Convention { get; set; }

    /// <summary>Bill lines in this category are capitalized at or above this amount; null uses the organization's threshold.</summary>
    public decimal? CapitalizationThreshold { get; set; }

    /// <summary>MACRS recovery period for tax reporting (3, 5, 7, 10, 15 or 20 years).</summary>
    public int? MacrsRecoveryYears { get; set; }

    public Guid AssetAccountId { get; set; }
    public Guid AccumulatedDepreciationAccountId { get; set; }
    public Guid ExpenseAccountId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A fixed asset. Contractor-owned assets are depreciated monthly into the ledger; government-furnished and
/// contractor-acquired property (FAR 52.245-1) is tracked for the property records but not depreciated by us.
/// </summary>
public sealed class FixedAsset : Entity, Companies.ICompanyOwned
{
    /// <summary>The company that owns the asset and books its depreciation.</summary>
    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;

    /// <summary>Our asset tag, for example "FA-00012". Unique.</summary>
    public required string TagNumber { get; set; }

    public required string Description { get; set; }
    public Guid CategoryId { get; set; }
    public DateOnly AcquisitionDate { get; set; }

    /// <summary>Depreciation starts in this month (full-month convention). Defaults to the acquisition date.</summary>
    public DateOnly InServiceDate { get; set; }

    public decimal Cost { get; set; }
    public decimal SalvageValue { get; set; }
    public int UsefulLifeYears { get; set; }
    public DepreciationMethod Method { get; set; }
    public DepreciationConvention Convention { get; set; }
    public int? MacrsRecoveryYears { get; set; }

    /// <summary>
    /// For assets brought over from another system: the date their opening balances are as of (a month end). Workbench
    /// depreciates from the month after it, spreading what's left over the rest of the life.
    /// </summary>
    public DateOnly? OpeningBalanceDate { get; set; }

    /// <summary>Accumulated depreciation through <see cref="OpeningBalanceDate"/>, already on the books (never posted by Workbench).</summary>
    public decimal OpeningAccumulatedDepreciation { get; set; }

    public string? Location { get; set; }
    public Guid? CustodianEmployeeId { get; set; }
    public string? SerialNumber { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.InService;

    public Guid AssetAccountId { get; set; }
    public Guid AccumulatedDepreciationAccountId { get; set; }
    public Guid ExpenseAccountId { get; set; }

    // Segments the depreciation expense is charged to.
    public Guid? OrgUnitId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }

    // Government property (FAR 52.245-1).
    public bool IsGovernmentFurnished { get; set; }
    public bool IsContractorAcquired { get; set; }
    public Guid? ContractId { get; set; }

    /// <summary>The government's property (tag) number.</summary>
    public string? GovernmentPropertyNumber { get; set; }

    /// <summary>Depreciation to date: the opening balance plus what the depreciation runs posted (a running total).</summary>
    public decimal AccumulatedDepreciation { get; set; }

    public Guid? VendorId { get; set; }

    /// <summary>The AP bill line it was capitalized from.</summary>
    public Guid? SourceBillLineId { get; set; }

    /// <summary>The reclass entry that moved the cost from the bill's account to the asset account, when one was needed.</summary>
    public Guid? CapitalizationEntryId { get; set; }

    public DateOnly? DisposedOn { get; set; }
    public decimal? DisposalProceeds { get; set; }
    public string? DisposalReason { get; set; }
    public Guid? DisposalEntryId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public bool IsGovernmentProperty => IsGovernmentFurnished || IsContractorAcquired;

    public decimal NetBookValue => Cost - AccumulatedDepreciation;
}

/// <summary>One monthly depreciation run for a fiscal period. A period can have several runs (later ones pick up assets added since).</summary>
public sealed class AssetDepreciationRun : Entity
{
    public Guid FiscalPeriodId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public decimal Total { get; set; }
    public int AssetCount { get; set; }
    public required string RunBy { get; set; }
    public DateTime RunAtUtc { get; set; }
}

/// <summary>One asset's depreciation for one fiscal period. Unique per asset and period, which makes runs idempotent.</summary>
public sealed class AssetDepreciation : Entity
{
    public Guid AssetId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public Guid RunId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>A move of an asset between locations, custodians, organizations or projects (append-only history).</summary>
public sealed class AssetTransfer : Entity
{
    public Guid AssetId { get; set; }
    public DateOnly TransferredOn { get; set; }
    public string? FromLocation { get; set; }
    public string? ToLocation { get; set; }
    public Guid? FromCustodianEmployeeId { get; set; }
    public Guid? ToCustodianEmployeeId { get; set; }
    public Guid? FromOrgUnitId { get; set; }
    public Guid? ToOrgUnitId { get; set; }
    public Guid? FromProjectId { get; set; }
    public Guid? ToProjectId { get; set; }
    public string? Reason { get; set; }
    public required string TransferredBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
