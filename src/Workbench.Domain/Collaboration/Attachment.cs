namespace Workbench.Domain.Collaboration;

/// <summary>A file attached to an item. The bytes live in the blob store under <see cref="StorageKey"/>.</summary>
public sealed class Attachment : Entity
{
    public Guid SheetId { get; set; }
    public Guid ItemId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string StorageKey { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}
