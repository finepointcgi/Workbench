namespace Workbench.Domain.Files;

/// <summary>
/// A file owned by any record (for example an ODC receipt), not tied to sheet items like
/// <c>Collaboration.Attachment</c>. The bytes live in the blob store under <see cref="StorageKey"/>.
/// </summary>
public sealed class StoredFile : Entity
{
    /// <summary>The owning record's type, for example <c>OdcEntry</c>.</summary>
    public required string OwnerType { get; set; }

    public Guid OwnerId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string StorageKey { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}
