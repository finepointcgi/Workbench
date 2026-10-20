namespace Workbench.Domain.Purchasing;

public enum EdiTransportKind
{
    /// <summary>Files dropped in and picked up from a folder on the server (or a mounted share).</summary>
    FileDrop,

    /// <summary>An SFTP server; needs an SFTP client registered on the host.</summary>
    Sftp,
}

/// <summary>A vendor that trades X12 documents with us: its interchange ids and where files go.</summary>
public sealed class EdiPartner : Entity
{
    public Guid VendorId { get; set; }

    /// <summary>ISA05/ISA07 qualifier for the vendor's id, for example "ZZ" (mutually defined) or "01" (DUNS).</summary>
    public string InterchangeQualifier { get; set; } = "ZZ";

    /// <summary>The vendor's interchange id (ISA06 on their files, ISA08 on ours). Upper case.</summary>
    public required string InterchangeId { get; set; }

    /// <summary>The vendor's application code (GS02/GS03); defaults to <see cref="InterchangeId"/>.</summary>
    public string? ApplicationCode { get; set; }

    public EdiTransportKind Transport { get; set; } = EdiTransportKind.FileDrop;

    /// <summary>FileDrop: a folder under the EDI root. SFTP: the host name.</summary>
    public required string Address { get; set; }

    /// <summary>Send 850s automatically when a purchase order to this vendor is issued.</summary>
    public bool SendPurchaseOrders { get; set; } = true;

    public bool IsActive { get; set; } = true;

    /// <summary>The next interchange control number to use on files we send (ISA13).</summary>
    public int NextControlNumber { get; set; } = 1;

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum EdiDirection
{
    Outbound,
    Inbound,
}

public enum EdiMessageStatus
{
    /// <summary>Outbound: handed to the transport. Inbound: applied.</summary>
    Processed,

    /// <summary>Couldn't be sent, parsed or applied; see <see cref="EdiMessage.Error"/>.</summary>
    Failed,

    /// <summary>Inbound: a file we already processed (same partner and control number).</summary>
    Duplicate,
}

/// <summary>The EDI log: one row per transaction set sent or received (or per file that couldn't be read). Append-only, except that a retry adds a new row.</summary>
public sealed class EdiMessage : Entity
{
    public const int MaxContentLength = 200_000;

    public EdiDirection Direction { get; set; }

    /// <summary>"850", "855", "856" or "810"; "?" when the file couldn't be read.</summary>
    public required string TransactionSet { get; set; }

    public Guid? PartnerId { get; set; }
    public Guid? VendorId { get; set; }

    /// <summary>"ISA13/ST02": the interchange and transaction set control numbers.</summary>
    public string? ControlNumber { get; set; }

    public required string FileName { get; set; }
    public EdiMessageStatus Status { get; set; }
    public string? Error { get; set; }

    /// <summary>The business document, for example "PO-000012" or "ASN-000003".</summary>
    public string? DocumentNumber { get; set; }

    public Guid? DocumentId { get; set; }

    /// <summary>The raw X12 (capped; larger files keep the first part).</summary>
    public required string Content { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
