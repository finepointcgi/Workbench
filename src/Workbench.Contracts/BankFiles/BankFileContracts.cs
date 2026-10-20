namespace Workbench.Contracts.BankFiles;

public enum BankAccountType { Checking, Savings }

public enum BankChangeState { Pending, Approved, Rejected, Cancelled }

// ---- Vendor ACH details ----

/// <summary>A vendor's bank details as everyone sees them: masked, last four digits only.</summary>
public sealed record VendorBankDetailsDto(
    Guid VendorId,
    string VendorCode,
    string VendorName,
    bool HasAccount,
    string? AccountHolderName,
    BankAccountType? AccountType,
    string? MaskedRouting,
    string? MaskedAccount,
    string? RemittanceEmail,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    VendorBankChangeDto? PendingChange,
    IReadOnlyList<VendorBankChangeDto> History);

public sealed record VendorBankChangeDto(
    Guid Id,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    BankChangeState Status,
    string AccountHolderName,
    BankAccountType AccountType,
    string MaskedRouting,
    string MaskedAccount,
    string? RemittanceEmail,
    string Reason,
    string RequestedBy,
    DateTime RequestedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,
    bool CanDecide);

/// <summary>New ACH details for a vendor. Routing is nine digits (ABA checksum); the account 4–17 digits.</summary>
public sealed record RequestVendorBankChangeRequest(
    string AccountHolderName,
    string RoutingNumber,
    string AccountNumber,
    BankAccountType AccountType,
    string Reason,
    string? RemittanceEmail = null);

public sealed record DecideVendorBankChangeRequest(string? Comment = null);

/// <summary>The full numbers (recorded as a security event).</summary>
public sealed record VendorBankRevealDto(Guid VendorId, string RoutingNumber, string AccountNumber);

/// <summary>
/// Vendor ACH bank details. Changes are requested, audited and routed through the <c>vendor-bank-change</c> approval
/// workflow; someone other than the requester approves them. Finance or Admin.
/// </summary>
public interface IVendorBankService
{
    Task<VendorBankDetailsDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default);

    /// <summary>Changes waiting for approval, across vendors.</summary>
    Task<IReadOnlyList<VendorBankChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default);

    Task<VendorBankChangeDto> RequestChangeAsync(Guid vendorId, RequestVendorBankChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Approves the change (never the requester); the new details take effect.</summary>
    Task<VendorBankChangeDto> ApproveChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default);

    Task<VendorBankChangeDto> RejectChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>The requester withdraws a pending change.</summary>
    Task<VendorBankChangeDto> CancelChangeAsync(Guid changeId, CancellationToken cancellationToken = default);

    Task<VendorBankRevealDto> RevealAsync(Guid vendorId, CancellationToken cancellationToken = default);
}

// ---- Bank account file settings ----

public enum PositivePayFileFormat { Csv, FixedWidth }

/// <summary>The fields a positive pay layout can write.</summary>
public enum PositivePayField { AccountNumber, RoutingNumber, CheckNumber, IssueDate, Amount, AmountCents, Payee, IssueCode, Literal, Blank }

public enum FieldAlign { Left, Right }

/// <summary>
/// One field of a positive pay layout. <see cref="Width"/> pads or cuts it (fixed width; optional for CSV); <see cref="Pad"/>
/// is the pad character; <see cref="Format"/> is a .NET date format for dates and "0.00" style for amounts; <see cref="Value"/>
/// is the text of a Literal and the column header for CSV.
/// </summary>
public sealed record PositivePayFieldSpec(PositivePayField Field, int? Width = null, char Pad = ' ', FieldAlign Align = FieldAlign.Left, string? Format = null, string? Value = null);

public sealed record PositivePayLayoutDto(
    Guid? Id,
    string Name,
    PositivePayFileFormat Format,
    IReadOnlyList<PositivePayFieldSpec> Fields,
    bool IncludeHeader,
    string IssueCode,
    string VoidCode,
    bool IsPreset);

public sealed record SavePositivePayLayoutRequest(
    string Name,
    PositivePayFileFormat Format,
    IReadOnlyList<PositivePayFieldSpec> Fields,
    bool IncludeHeader = false,
    string IssueCode = "I",
    string VoidCode = "V");

/// <summary>A bank account's payment file settings (NACHA origination and its positive pay layout).</summary>
public sealed record BankFileSettingsDto(
    Guid BankAccountId,
    string BankAccountName,
    string BankName,
    string MaskedAccountNumber,
    bool HasFullAccountNumber,
    string? RoutingNumber,
    bool IsConfigured,
    string? ImmediateDestination,
    string? ImmediateDestinationName,
    string? ImmediateOrigin,
    string? ImmediateOriginName,
    string? CompanyIdentification,
    string? CompanyName,
    string EntryDescription,
    string? OriginatingDfi,
    bool BalancedFile,
    Guid? PositivePayLayoutId,
    string PositivePayLayoutName,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record SaveBankFileSettingsRequest(
    string ImmediateDestination,
    string ImmediateDestinationName,
    string ImmediateOrigin,
    string ImmediateOriginName,
    string CompanyIdentification,
    string CompanyName,
    string EntryDescription,
    string? OriginatingDfi = null,
    bool BalancedFile = false,
    Guid? PositivePayLayoutId = null);

// ---- Files and remittance ----

public enum PaymentFileType { Nacha, PositivePay, PositivePayVoids }

/// <summary>A generated payment file. Content is ASCII text; the caller saves it, Workbench never sends it.</summary>
public sealed record PaymentFileDto(string FileName, string ContentType, string Content, int EntryCount, decimal TotalAmount);

public sealed record PaymentFileDownloadDto(Guid Id, PaymentFileType Kind, string FileName, string? FileIdModifier, int EntryCount, decimal TotalAmount, string Sha256, string BankAccountName, string DownloadedBy, DateTime DownloadedAtUtc);

public sealed record RemittanceAdviceDto(Guid VendorId, string VendorCode, string VendorName, string? Email, decimal Amount, int Payments, DateTime? SentAtUtc, string? SentBy);

/// <summary>Everything about a payment run's files: which bank account, what's ready, what was downloaded and remittances.</summary>
public sealed record PaymentRunFilesDto(
    Guid RunId,
    IReadOnlyList<BankFileSettingsDto> BankAccounts,
    Guid? DefaultBankAccountId,
    IReadOnlyList<string> Problems,
    IReadOnlyList<PaymentFileDownloadDto> Downloads,
    IReadOnlyList<RemittanceAdviceDto> Remittances);

public sealed record GeneratePaymentFileRequest(Guid BankAccountId, PaymentFileType Kind);

public sealed record RemittanceResultDto(int Sent, IReadOnlyList<string> Skipped);

/// <summary>
/// Bank payment files from AP payment runs: NACHA ACH (CCD+ with remittance addenda) and positive pay, plus emailed
/// remittance advice. Files are only downloaded, and every download is logged. Finance or Admin.
/// </summary>
public interface IPaymentFileService
{
    Task<IReadOnlyList<BankFileSettingsDto>> ListBankAccountsAsync(CancellationToken cancellationToken = default);

    Task<BankFileSettingsDto> GetSettingsAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    Task<BankFileSettingsDto> SaveSettingsAsync(Guid bankAccountId, SaveBankFileSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>The built-in presets and the organization's own layouts.</summary>
    Task<IReadOnlyList<PositivePayLayoutDto>> ListLayoutsAsync(CancellationToken cancellationToken = default);

    Task<PositivePayLayoutDto> SaveLayoutAsync(Guid? layoutId, SavePositivePayLayoutRequest request, CancellationToken cancellationToken = default);

    Task<PaymentRunFilesDto> GetRunFilesAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Builds the file and logs the download. NACHA needs an approved or completed ACH run; positive pay a check run.</summary>
    Task<PaymentFileDto> GenerateAsync(Guid runId, GeneratePaymentFileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Emails each vendor in the run what was paid (bill, invoice, amount). Skips vendors without an email.</summary>
    Task<RemittanceResultDto> SendRemittanceAsync(Guid runId, CancellationToken cancellationToken = default);
}
