namespace Workbench.Domain.Cash;

/// <summary>
/// A company bank account, linked to the GL cash account its activity posts to. Only the last four digits of the
/// account number are kept in clear; a full number, when entered, is stored encrypted (Data Protection) and revealed
/// only through an audited security event. Accounts are deactivated, never deleted.
/// </summary>
public sealed class BankAccount : Entity
{
    public required string Name { get; set; }
    public required string BankName { get; set; }

    /// <summary>The last four digits of the account number (the only part shown anywhere).</summary>
    public required string AccountNumberLast4 { get; set; }

    /// <summary>The full account number, encrypted; null when only the last four digits were entered.</summary>
    public string? ProtectedAccountNumber { get; set; }

    /// <summary>The nine-digit ABA routing number.</summary>
    public string? RoutingNumber { get; set; }

    /// <summary>The GL cash account (1010 Cash - operating or another cash account) this bank account reconciles to.</summary>
    public Guid GlAccountId { get; set; }

    public string Currency { get; set; } = "USD";
    public bool IsActive { get; set; } = true;

    /// <summary>The saved CSV column mapping for this bank's statement export (JSON), reused on the next import.</summary>
    public string? CsvMappingJson { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public enum StatementFormat
{
    Csv,
    Ofx,
    Bai2,
}

/// <summary>One imported statement file. The raw file is kept for audit; its transactions are <see cref="BankTransaction"/> rows.</summary>
public sealed class BankStatementImport : Entity
{
    public Guid BankAccountId { get; set; }
    public StatementFormat Format { get; set; }
    public required string FileName { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }

    /// <summary>The opening and closing ledger balances, when the file states them (OFX, BAI2).</summary>
    public decimal? OpeningBalance { get; set; }

    public decimal? ClosingBalance { get; set; }

    /// <summary>New transactions added by this import.</summary>
    public int TransactionCount { get; set; }

    /// <summary>Transactions skipped because an earlier (overlapping) statement already had them.</summary>
    public int DuplicateCount { get; set; }

    public required string Content { get; set; }
    public required string ImportedBy { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

public enum BankTransactionType
{
    Deposit,
    Check,
    Ach,
    Wire,
    Fee,
    Interest,
    Transfer,
    Other,
}

/// <summary>
/// A line from a bank statement. <see cref="Amount"/> is signed from the company's side: deposits and interest are
/// positive, checks, debits and fees negative. Immutable once imported; clearing happens through <see cref="BankMatch"/>.
/// </summary>
public sealed class BankTransaction : Entity
{
    public Guid BankAccountId { get; set; }
    public Guid ImportId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public BankTransactionType Type { get; set; }
    public required string Description { get; set; }
    public string? CheckNumber { get; set; }

    /// <summary>The bank's reference for the item (OFX FITID, BAI2 bank reference, a CSV reference column).</summary>
    public string? BankReference { get; set; }

    /// <summary>Date, amount and reference normalized; equal fingerprints on the same account are the same bank item.</summary>
    public required string Fingerprint { get; set; }

    /// <summary>The match that clears this item, if any.</summary>
    public Guid? MatchId { get; set; }

    /// <summary>The journal entry booked from this item on the reconciliation screen (fees, interest), if any.</summary>
    public Guid? JournalEntryId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>How a match was made.</summary>
public enum BankMatchRule
{
    Manual,
    ArPaymentReference,
    ApCheckNumber,
    ApAchReference,
    AmountAndDate,
    AmountAndDateGroup,
    ReversalPair,
    BookedFromBank,
}

/// <summary>
/// Clears bank items against GL cash lines: one to one, one to many (a deposit of several receipts) or many to one
/// (a batch ACH). The bank side and the GL side total the same. A match with no bank items pairs a GL entry with its
/// reversal (a voided check). Locked by the reconciliation that completes with it.
/// </summary>
public sealed class BankMatch : Entity
{
    public Guid BankAccountId { get; set; }
    public BankMatchRule Rule { get; set; }

    /// <summary>0–100: how sure the auto-matcher was (100 for references and manual matches).</summary>
    public int Confidence { get; set; }

    public string? Note { get; set; }
    public required string MatchedBy { get; set; }
    public DateTime MatchedAtUtc { get; set; }

    /// <summary>The completed reconciliation that locked this match; null while it can be undone.</summary>
    public Guid? ReconciliationId { get; set; }

    public List<BankMatchLine> GlLines { get; } = [];
}

/// <summary>A GL cash journal line cleared by a <see cref="BankMatch"/>. A line is in at most one match.</summary>
public sealed class BankMatchLine : Entity
{
    public Guid MatchId { get; set; }
    public Guid BankAccountId { get; set; }
    public Guid JournalLineId { get; set; }

    /// <summary>The line's debit-positive amount when matched.</summary>
    public decimal Amount { get; set; }
}

public enum BankReconciliationStatus
{
    InProgress,
    Completed,
}

/// <summary>
/// One bank account reconciled to one statement end date. While in progress the figures are computed live; completing
/// it snapshots them and locks the matches cleared by that date. Reopening (with a reason) unlocks them. Audited.
/// </summary>
public sealed class BankReconciliation : Entity
{
    public Guid BankAccountId { get; set; }
    public DateOnly StatementEndDate { get; set; }
    public decimal StatementEndingBalance { get; set; }
    public BankReconciliationStatus Status { get; set; } = BankReconciliationStatus.InProgress;

    // Snapshot at completion.
    public decimal GlBalance { get; set; }
    public decimal DepositsInTransit { get; set; }
    public decimal OutstandingPayments { get; set; }
    public decimal UnrecordedBankItems { get; set; }

    public string? Note { get; set; }
    public required string StartedBy { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }
    public string? ReopenReason { get; set; }
}

/// <summary>
/// Finance's waiver of the "bank accounts reconciled through period end" close check for one fiscal period, with a
/// reason. Append-only; the latest waiver for a period applies.
/// </summary>
public sealed class BankCloseWaiver : Entity
{
    public Guid FiscalPeriodId { get; set; }
    public required string Reason { get; set; }
    public required string WaivedBy { get; set; }
    public DateTime WaivedAtUtc { get; set; }
}

public enum ForecastFrequency
{
    Once,
    Weekly,
    Biweekly,
    Monthly,
}

/// <summary>A recurring (or one-off) manual cash forecast item such as rent; signed (receipts positive, payments negative).</summary>
public sealed class CashForecastItem : Entity
{
    public required string Name { get; set; }
    public decimal Amount { get; set; }
    public ForecastFrequency Frequency { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Cash management settings (one row): the payroll forecast and the auto-match date window.</summary>
public sealed class CashSettings : Entity
{
    /// <summary>Net cash per payroll; null to forecast from the average of recent payroll accruals.</summary>
    public decimal? PayrollAmount { get; set; }

    /// <summary>Days between paydays (7 weekly, 14 biweekly).</summary>
    public int PayCycleDays { get; set; } = 14;

    /// <summary>A known payday that anchors the schedule; null for the next Friday.</summary>
    public DateOnly? PayDateAnchor { get; set; }

    /// <summary>How far apart bank and GL dates may be for an amount match.</summary>
    public int MatchDateWindowDays { get; set; } = 5;

    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
