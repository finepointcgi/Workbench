namespace Workbench.Domain.Cash;

/// <summary>
/// How a company bank account originates NACHA ACH files: the ODFI (our bank) and the company identification it assigned.
/// One per bank account. Field lengths follow the NACHA record layouts.
/// </summary>
public sealed class BankAchSettings : Entity
{
    public Guid BankAccountId { get; set; }

    /// <summary>File header "immediate destination": the ODFI's nine-digit routing number (written with a leading space).</summary>
    public required string ImmediateDestination { get; set; }

    /// <summary>File header destination name, up to 23 characters (the bank's name).</summary>
    public required string ImmediateDestinationName { get; set; }

    /// <summary>File header "immediate origin", ten characters: usually "1" plus the EIN, or what the bank assigned.</summary>
    public required string ImmediateOrigin { get; set; }

    /// <summary>File header origin name, up to 23 characters (our company's name).</summary>
    public required string ImmediateOriginName { get; set; }

    /// <summary>Batch header company identification, ten characters (often the same as the immediate origin).</summary>
    public required string CompanyIdentification { get; set; }

    /// <summary>Batch header company name, up to 16 characters.</summary>
    public required string CompanyName { get; set; }

    /// <summary>Batch header entry description, up to 10 characters (shown on the vendor's statement), for example "VENDOR PAY".</summary>
    public string EntryDescription { get; set; } = "VENDOR PAY";

    /// <summary>Batch header originating DFI: the first eight digits of our bank's routing number (also the trace number prefix).</summary>
    public required string OriginatingDfi { get; set; }

    /// <summary>Adds an offsetting debit to our own account so the file balances (some banks require it, most don't).</summary>
    public bool BalancedFile { get; set; }

    /// <summary>The positive pay layout this bank expects for check files; null uses the standard CSV preset.</summary>
    public Guid? PositivePayLayoutId { get; set; }

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum PositivePayFormat
{
    Csv,
    FixedWidth,
}

/// <summary>
/// A positive pay file layout: the fields a bank expects, in order. <see cref="FieldsJson"/> is a JSON array of
/// <c>{ "field": "CheckNumber", "width": 10, "pad": "0", "align": "Right", "format": "MMddyyyy", "value": "..." }</c>.
/// Built-in presets aren't stored; these are an organization's own layouts.
/// </summary>
public sealed class PositivePayLayout : Entity
{
    public required string Name { get; set; }
    public PositivePayFormat Format { get; set; }
    public required string FieldsJson { get; set; }
    public bool IncludeHeader { get; set; }

    /// <summary>The issue code for an issued check and for a void, for example "I" and "V".</summary>
    public string IssueCode { get; set; } = "I";

    public string VoidCode { get; set; } = "V";
    public bool IsActive { get; set; } = true;
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum PaymentFileKind
{
    Nacha,
    PositivePay,
    PositivePayVoids,
}

/// <summary>
/// One payment file downloaded from a payment run. Workbench never sends payment files anywhere; Finance uploads them to
/// the bank. Append-only: what was in the file (counts, totals, a hash) and who took it.
/// </summary>
public sealed class PaymentFileDownload : Entity
{
    public Guid RunId { get; set; }
    public Guid BankAccountId { get; set; }
    public PaymentFileKind Kind { get; set; }
    public required string FileName { get; set; }

    /// <summary>The NACHA file ID modifier (A–Z, 0–9) used, so two files on one day differ.</summary>
    public string? FileIdModifier { get; set; }

    public int EntryCount { get; set; }
    public decimal TotalAmount { get; set; }

    /// <summary>SHA-256 of the file, hex.</summary>
    public required string Sha256 { get; set; }

    public required string DownloadedBy { get; set; }
    public DateTime DownloadedAtUtc { get; set; }
}

/// <summary>A remittance advice emailed to a vendor for a payment run. Append-only.</summary>
public sealed class RemittanceAdvice : Entity
{
    public Guid RunId { get; set; }
    public Guid VendorId { get; set; }
    public required string Email { get; set; }
    public decimal Amount { get; set; }
    public int Payments { get; set; }
    public required string SentBy { get; set; }
    public DateTime SentAtUtc { get; set; }
}
