namespace Workbench.Domain.Travel;

/// <summary>Who pays the card issuer.</summary>
public enum CardPaymentResponsibility
{
    /// <summary>Central bill: the company pays the issuer, so charges post to the card liability, not to the employee.</summary>
    CompanyPaid,

    /// <summary>Individual bill: the employee pays the issuer and is reimbursed like any expense.</summary>
    EmployeePaid,
}

/// <summary>A corporate card program (issuer, who pays, and the GL liability company-paid charges post to).</summary>
public sealed class CardProgram : Entity
{
    public required string Name { get; set; }
    public required string Issuer { get; set; }

    /// <summary>The issuer as an AP vendor (named on the liability postings).</summary>
    public Guid? VendorId { get; set; }

    public CardPaymentResponsibility PaymentResponsibility { get; set; } = CardPaymentResponsibility.CompanyPaid;

    /// <summary>Liability account company-paid charges credit (default 2030 Corporate card payable).</summary>
    public Guid? LiabilityAccountId { get; set; }

    /// <summary>Charges not on a submitted expense report after this many days show as overdue.</summary>
    public int UnsubmittedAlertDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A card issued to an employee. Only the last four digits are stored.</summary>
public sealed class CorporateCard : Entity
{
    public Guid ProgramId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string Last4 { get; set; }
    public string? NameOnCard { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One statement file imported for a program.</summary>
public sealed class CardStatementImport : Entity
{
    public Guid ProgramId { get; set; }
    public required string FileName { get; set; }

    /// <summary>"CSV" or "OFX" (the feed that read it).</summary>
    public required string Format { get; set; }

    public int RowCount { get; set; }
    public int ImportedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int UnmatchedCount { get; set; }
    public decimal ImportedTotal { get; set; }
    public required string ImportedBy { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>
/// A card charge (or credit) from a statement. It waits in the cardholder's unassigned charges until it is attached to an
/// expense report line (<see cref="OdcEntryId"/>; the line's amount is then locked to the charge), or Finance excludes it.
/// </summary>
public sealed class CardTransaction : Entity
{
    public Guid ProgramId { get; set; }
    public Guid ImportId { get; set; }

    /// <summary>The matched card; null when no card in the program has these last four digits.</summary>
    public Guid? CardId { get; set; }

    public Guid? EmployeeId { get; set; }
    public required string CardLast4 { get; set; }

    /// <summary>The bank's transaction id (OFX FITID or a CSV reference) or, without one, a hash of the row. Unique per program.</summary>
    public required string DedupeKey { get; set; }

    public string? ExternalId { get; set; }
    public DateOnly TransactionDate { get; set; }
    public DateOnly? PostedDate { get; set; }

    /// <summary>Positive for a charge, negative for a credit or refund.</summary>
    public decimal Amount { get; set; }

    public required string Merchant { get; set; }
    public string? MerchantCategory { get; set; }

    public Guid? OdcEntryId { get; set; }
    public DateTime? AttachedAtUtc { get; set; }
    public string? AttachedBy { get; set; }

    public bool IsExcluded { get; set; }
    public string? ExcludedReason { get; set; }
    public string? ExcludedBy { get; set; }
    public DateTime? ExcludedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
