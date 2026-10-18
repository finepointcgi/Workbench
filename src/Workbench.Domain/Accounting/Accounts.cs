namespace Workbench.Domain.Accounting;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

public enum NormalBalance
{
    Debit,
    Credit,
}

/// <summary>
/// How an expense account's cost is treated for indirect rates and DCAA reporting. Direct costs go to contracts,
/// indirect costs go to pools that are allocated, and unallowable costs (FAR 31.205) are segregated.
/// </summary>
public enum CostClassification
{
    None,
    DirectLabor,
    DirectOdc,
    IndirectLabor,
    Fringe,
    Overhead,
    GeneralAndAdministrative,
    Unallowable,
    Other,

    /// <summary>
    /// Indirect cost applied to projects by the allocation run (the "applied burden" debit and the per-pool "applied"
    /// contra credit). Never part of a pool or a base, so allocations don't feed back into the rates.
    /// </summary>
    AppliedBurden,
}

/// <summary>
/// A general-ledger account. Accounts with children are summary (roll-up) accounts and don't take postings.
/// Accounts are deactivated, never deleted, because journal lines reference them.
/// </summary>
public sealed class Account : Entity
{
    /// <summary>For example <c>5100</c>. Unique.</summary>
    public required string Number { get; set; }

    public required string Name { get; set; }
    public AccountType Type { get; set; }
    public NormalBalance NormalBalance { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>The summary account this one rolls up into.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Only meaningful for expense accounts; <see cref="CostClassification.None"/> otherwise.</summary>
    public CostClassification CostClass { get; set; }

    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public static NormalBalance DefaultNormalBalance(AccountType type) =>
        type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit;
}

/// <summary>Which kind of cost a <see cref="PostingRule"/> maps to an account.</summary>
public enum PostingRuleKind
{
    /// <summary>Labor charged to Direct WBS elements.</summary>
    DirectLabor,

    /// <summary>Labor charged to Indirect WBS elements; the key is the <see cref="IndirectPool"/> of the element.</summary>
    IndirectLabor,

    /// <summary>Labor charged to Leave WBS elements (paid leave and holidays go to the fringe pool).</summary>
    LeaveLabor,

    /// <summary>Allowable ODCs; the key is the ODC type.</summary>
    Odc,

    /// <summary>Unallowable ODCs and expenses; the key is the ODC type, or empty for the default.</summary>
    UnallowableOdc,

    /// <summary>Credit side of labor postings.</summary>
    AccruedPayroll,

    /// <summary>Credit side of vendor ODCs.</summary>
    AccountsPayable,

    /// <summary>Credit side of employee expenses.</summary>
    AccruedReimbursement,
}

/// <summary>Maps a cost source (kind plus an optional key) to the account it posts to.</summary>
public sealed class PostingRule : Entity
{
    public PostingRuleKind Kind { get; set; }

    /// <summary>Empty for the kind's default; otherwise the pool or ODC type name.</summary>
    public string Key { get; set; } = "";

    public Guid AccountId { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>The indirect cost pool a WBS element's labor belongs to.</summary>
public enum IndirectPool
{
    Fringe,
    Overhead,
    GeneralAndAdministrative,
}

/// <summary>
/// Assigns an indirect or leave WBS element (and, by inheritance, its descendants) to an indirect cost pool.
/// Kept beside the WBS rather than on it so the general ledger owns its own setup.
/// </summary>
public sealed class WbsCostPool : Entity
{
    public Guid WbsElementId { get; set; }
    public IndirectPool Pool { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
