namespace Workbench.Contracts.Purchasing;

// EDI (ANSI X12 004010): 850 purchase orders out; 855 acknowledgements, 856 ship notices and 810 invoices in. Files move
// through a pluggable transport (a file drop folder, or SFTP when the host registers a client; no VAN). Inbound
// documents go through the same purchase order, ship notice and draft bill paths as the supplier portal. Every file and
// transaction set is in the EDI log with its errors. Finance and Admin.

public enum EdiTransportType { FileDrop, Sftp }

public enum EdiFlow { Outbound, Inbound }

public enum EdiOutcome { Processed, Failed, Duplicate }

public sealed record EdiPartnerDto(
    Guid Id,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    string InterchangeQualifier,
    string InterchangeId,
    string? ApplicationCode,
    EdiTransportType Transport,
    string Address,
    bool SendPurchaseOrders,
    bool IsActive,
    DateTime UpdatedAtUtc);

/// <summary>
/// <see cref="Address"/>: for a file drop, a folder name (letters, digits, dashes) under the server's EDI folder, with
/// <c>inbound</c> and <c>outbound</c> inside; for SFTP, the host name.
/// </summary>
public sealed record SaveEdiPartnerRequest(
    Guid VendorId,
    string InterchangeId,
    string Address,
    string InterchangeQualifier = "ZZ",
    string? ApplicationCode = null,
    EdiTransportType Transport = EdiTransportType.FileDrop,
    bool SendPurchaseOrders = true,
    bool IsActive = true);

public sealed record EdiMessageDto(
    Guid Id,
    EdiFlow Direction,
    string TransactionSet,
    string? VendorName,
    string? ControlNumber,
    string FileName,
    EdiOutcome Status,
    string? Error,
    string? DocumentNumber,
    Guid? DocumentId,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record EdiMessageDetailDto(EdiMessageDto Message, string Content);

public sealed record EdiLogQuery(EdiFlow? Direction = null, EdiOutcome? Status = null, string? TransactionSet = null, Guid? VendorId = null, int Take = 200);

public sealed record EdiPollResultDto(int Files, int Processed, int Failed, int Duplicates);

public interface IEdiService
{
    Task<IReadOnlyList<EdiPartnerDto>> ListPartnersAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a partner (<paramref name="partnerId"/> null) or changes one.</summary>
    Task<EdiPartnerDto> SavePartnerAsync(Guid? partnerId, SaveEdiPartnerRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EdiMessageDto>> ListLogAsync(EdiLogQuery query, CancellationToken cancellationToken = default);

    Task<EdiMessageDetailDto> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Sends an issued purchase order to its vendor as an 850 (the vendor needs an active partner).</summary>
    Task<EdiMessageDto> SendPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default);

    /// <summary>Picks up and applies every partner's waiting inbound files.</summary>
    Task<EdiPollResultDto> PollAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies an X12 file uploaded by hand (one log row per transaction set in it).</summary>
    Task<IReadOnlyList<EdiMessageDto>> ImportAsync(string fileName, string content, CancellationToken cancellationToken = default);

    /// <summary>Tries a failed message again (sends it again, or applies it again), adding a new log row.</summary>
    Task<IReadOnlyList<EdiMessageDto>> RetryAsync(Guid messageId, CancellationToken cancellationToken = default);
}
