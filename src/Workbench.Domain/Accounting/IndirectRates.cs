namespace Workbench.Domain.Accounting;

/// <summary>What an indirect pool's cost is spread over.</summary>
public enum AllocationBase
{
    /// <summary>Direct labor dollars (the usual fringe base).</summary>
    DirectLabor,

    /// <summary>Direct labor plus the fringe applied to it (the usual overhead base).</summary>
    DirectLaborPlusFringe,

    /// <summary>Direct labor + fringe + overhead + direct ODCs: everything applied before this pool (the usual G&amp;A base).</summary>
    TotalCostInput,

    /// <summary>Total cost input without direct ODCs (materials and subcontracts carry no G&amp;A).</summary>
    ValueAdded,
}

/// <summary>
/// Which rate: provisional billing rates set at the start of the year, targets, actual rates from closing the year, or
/// forward pricing rates (the rates proposals are priced at, often agreed in an FPRA or proposed in an FPRP).
/// </summary>
public enum IndirectRateBasis
{
    Provisional,
    Target,
    Actual,
    ForwardPricing,
}

/// <summary>
/// How one indirect pool is built and allocated: the GL expense accounts whose balances form the pool, the allocation
/// base and the cascade order (fringe, then overhead, then G&amp;A). Unallowable and applied-burden accounts never
/// count toward a pool, whatever is selected.
/// </summary>
public sealed class IndirectPoolSetup : Entity
{
    public IndirectPool Pool { get; set; }
    public required string Name { get; set; }
    public AllocationBase Base { get; set; }

    /// <summary>Lower runs first; a pool's base can include the burden of every pool before it.</summary>
    public int CascadeOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Cost classifications whose expense accounts form the pool, comma separated (for example <c>Overhead</c>).</summary>
    public string CostClasses { get; set; } = "";

    /// <summary>
    /// Also include the indirect labor accounts that the posting rules map this pool to (labor on indirect WBS elements
    /// that <see cref="WbsCostPool"/> routes here) and, for the fringe pool, the leave labor account.
    /// </summary>
    public bool IncludeMappedIndirectLabor { get; set; } = true;

    /// <summary>Debited with project and WBS segments when burden is applied (for example 5920 Applied overhead).</summary>
    public Guid AppliedAccountId { get; set; }

    /// <summary>The contra account credited when burden is applied (for example 6190 Overhead applied).</summary>
    public Guid AbsorbedAccountId { get; set; }

    public List<IndirectPoolAccount> Accounts { get; } = [];

    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public IReadOnlyList<CostClassification> CostClassList =>
        CostClasses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => Enum.TryParse<CostClassification>(c, out var v) ? v : CostClassification.None)
            .Where(c => c != CostClassification.None)
            .Distinct()
            .ToList();
}

/// <summary>An account explicitly in a pool. A summary account brings in all of its descendants.</summary>
public sealed class IndirectPoolAccount : Entity
{
    public Guid PoolSetupId { get; set; }
    public Guid AccountId { get; set; }
}

/// <summary>
/// A pool's rate for one fiscal year and basis, as a fraction (0.32 = 32%). Append-only: a change supersedes the row
/// with a new one and a withdrawal marks it superseded, so the history of every rate stays.
/// </summary>
public sealed class IndirectRate : Entity
{
    public IndirectPool Pool { get; set; }
    public int FiscalYear { get; set; }
    public IndirectRateBasis Basis { get; set; }
    public decimal Rate { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string? Note { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when the rate was replaced or withdrawn; only rows without it are in force.</summary>
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>The replacing rate, or null when it was withdrawn.</summary>
    public Guid? SupersededById { get; set; }

    public bool IsCurrent => SupersededAtUtc is null;
}
