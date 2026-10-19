namespace Workbench.Domain.Currencies;

/// <summary>
/// A currency Workbench can record documents in, by its ISO 4217 code (upper case). Currencies are deactivated, never
/// deleted, so documents and rates that name one keep their meaning.
/// </summary>
public sealed class Currency : Entity
{
    /// <summary>The ISO 4217 code, for example <c>EUR</c>.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>Minor-unit digits (2 for EUR and GBP, 0 for JPY). Transaction amounts round to this many places.</summary>
    public int Decimals { get; set; } = 2;

    public bool IsActive { get; set; } = true;
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>What kind of rate a row is. Documents use <see cref="Spot"/>; the period-end revaluation uses <see cref="MonthEnd"/>.</summary>
public enum ExchangeRateType
{
    Spot,
    Average,
    MonthEnd,
}

/// <summary>
/// How many units of <see cref="ToCurrency"/> one unit of <see cref="FromCurrency"/> buys, from <see cref="EffectiveDate"/>
/// until the next rate of the same pair and type. One row per pair, type and date; entering the same date again replaces
/// the rate (audited). Documents keep the rate they used, so changing a rate never moves a posted amount.
/// </summary>
public sealed class ExchangeRate : Entity
{
    public required string FromCurrency { get; set; }
    public required string ToCurrency { get; set; }
    public ExchangeRateType RateType { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public decimal Rate { get; set; }

    /// <summary>Where it came from: "Manual", "CSV import" or a rate provider's name.</summary>
    public required string Source { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// The tenant's currency setup (one row). The functional currency is a tenant setting for now; with multiple companies
/// it moves to the company.
/// </summary>
public sealed class CurrencySettings : Entity
{
    /// <summary>The currency the books are kept in. Every journal line's debit and credit is in it.</summary>
    public string FunctionalCurrency { get; set; } = "USD";

    /// <summary>Realized gain or loss on payments of foreign documents (default 4920).</summary>
    public Guid? RealizedGainLossAccountId { get; set; }

    /// <summary>The period-end revaluation's gain or loss (default 4930).</summary>
    public Guid? UnrealizedGainLossAccountId { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public enum RevaluationItemKind
{
    Receivable,
    Payable,
    Bank,
}

/// <summary>
/// One period-end revaluation of open foreign receivables, payables and bank balances: an entry on the period's last day
/// to the unrealized gain/loss account, reversed on the first day of the next period. One per fiscal period.
/// </summary>
public sealed class CurrencyRevaluation : Entity
{
    public Guid FiscalPeriodId { get; set; }

    /// <summary>The period's last day: the rate date and the entry date.</summary>
    public DateOnly RateDate { get; set; }

    /// <summary>The net gain (positive) or loss (negative).</summary>
    public decimal NetGainLoss { get; set; }

    public Guid JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public required string PostedBy { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public List<CurrencyRevaluationLine> Lines { get; } = [];
}

/// <summary>One revalued document or bank account: its open balance in its currency, carried and revalued in the functional currency.</summary>
public sealed class CurrencyRevaluationLine : Entity
{
    public Guid RevaluationId { get; set; }
    public RevaluationItemKind Kind { get; set; }

    /// <summary>The invoice, bill or bank account.</summary>
    public Guid DocumentId { get; set; }

    public required string DocumentNumber { get; set; }
    public required string CurrencyCode { get; set; }

    /// <summary>The open balance in <see cref="CurrencyCode"/>.</summary>
    public decimal TransactionBalance { get; set; }

    /// <summary>What the books carry it at (the document's rate) in the functional currency.</summary>
    public decimal CarryingAmount { get; set; }

    public decimal Rate { get; set; }
    public decimal RevaluedAmount { get; set; }

    /// <summary>Gain (positive) or loss (negative).</summary>
    public decimal GainLoss { get; set; }

    /// <summary>The GL account adjusted (AR billed, AP, or the bank's cash account).</summary>
    public Guid AccountId { get; set; }
}
