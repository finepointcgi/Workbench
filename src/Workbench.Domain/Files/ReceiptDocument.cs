namespace Workbench.Domain.Files;

public enum ReceiptKind
{
    Receipt,
    LodgingFolio,
    CardStatement,
    Other,
}

/// <summary>How a receipt's values were read: nothing found, the PDF's text layer, or the AI assistant (only when an admin turned it on).</summary>
public enum ReceiptReadSource
{
    None,
    TextLayer,
    Assistant,
}

/// <summary>
/// A file in someone's receipt library ("My receipts"): receipts, hotel folios and card statements they uploaded. The bytes
/// are a <see cref="StoredFile"/> (<see cref="FileId"/>), stored once. Expense lines point at the same file through
/// <c>OdcEntry.ReceiptAttachmentId</c>, so one folio can back several lines (lodging and parking); which lines use a file is
/// read from those lines, never copied here. A file that was ever attached is only hidden when removed
/// (<see cref="RemovedAtUtc"/>), so the evidence behind a submitted report stays.
/// </summary>
public sealed class ReceiptDocument : Entity
{
    public Guid FileId { get; set; }

    /// <summary>Whose library it is (lowercased email of the uploader).</summary>
    public required string OwnerEmail { get; set; }

    public ReceiptKind Kind { get; set; }

    // What the receipt says (read from it, or typed by the owner), for searching the library.
    public string? Vendor { get; set; }
    public string? Location { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public decimal? Total { get; set; }

    public ReceiptReadSource ReadSource { get; set; }

    /// <summary>The full reading (JSON of the contract's extraction), so choosing the file again doesn't read it again.</summary>
    public string? ReadingJson { get; set; }
    public DateTime? ReadAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was first attached to anything. Set once; a file that was attached is never hard-deleted.</summary>
    public DateTime? FirstAttachedAtUtc { get; set; }

    public DateTime? RemovedAtUtc { get; set; }
    public string? RemovedBy { get; set; }
}
