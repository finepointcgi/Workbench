namespace Workbench.Contracts.Collaboration;

public sealed record ActivityDto(Guid Id, Guid SheetId, Guid? ItemId, string Actor, string Kind, string Summary, DateTime OccurredAtUtc);

/// <summary>Values of <see cref="ActivityDto.Kind"/>.</summary>
public static class ActivityKinds
{
    public const string ItemCreated = "item.created";
    public const string ItemUpdated = "item.updated";
    public const string ItemMoved = "item.moved";
    public const string ItemDeleted = "item.deleted";
    public const string ColumnCreated = "column.created";
    public const string ColumnUpdated = "column.updated";
    public const string ColumnDeleted = "column.deleted";
    public const string CommentAdded = "comment.added";
    public const string CommentEdited = "comment.edited";
    public const string CommentDeleted = "comment.deleted";
    public const string AttachmentAdded = "attachment.added";
    public const string AttachmentDeleted = "attachment.deleted";
}

public interface IActivityService
{
    /// <summary>Newest first. Pass <paramref name="itemId"/> to get one item's activity.</summary>
    Task<IReadOnlyList<ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default);
}
