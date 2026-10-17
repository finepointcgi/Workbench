namespace Workbench.Domain.Collaboration;

/// <summary>A comment on an item. Replies are one level deep: a reply's parent is always a top-level comment.</summary>
public sealed class Comment : Entity
{
    public Guid SheetId { get; set; }
    public Guid ItemId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public required string AuthorEmail { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }

    /// <summary>Lower-case emails @mentioned in <see cref="Body"/>, stored as JSON.</summary>
    public List<string> Mentions { get; set; } = [];
}
