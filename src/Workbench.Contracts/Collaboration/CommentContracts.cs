namespace Workbench.Contracts.Collaboration;

/// <summary>
/// A comment on an item. <see cref="Replies"/> holds the replies of a top-level comment (one level deep).
/// <see cref="CanEdit"/> is true when the current user wrote it.
/// </summary>
public sealed record CommentDto(
    Guid Id,
    Guid ItemId,
    Guid? ParentCommentId,
    string AuthorEmail,
    string Body,
    DateTime CreatedAtUtc,
    DateTime? EditedAtUtc,
    IReadOnlyList<string> Mentions,
    bool CanEdit,
    IReadOnlyList<CommentDto> Replies);

/// <summary>
/// Adds a comment. Mention people with <c>@email</c> in the body. A reply to a reply is attached to the
/// top-level comment, so threads stay one level deep.
/// </summary>
public sealed record CreateCommentRequest(string Body, Guid? ParentCommentId = null);

public sealed record UpdateCommentRequest(string Body);

public interface ICommentService
{
    /// <summary>Top-level comments of an item, oldest first, each with its replies.</summary>
    Task<IReadOnlyList<CommentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);

    Task<CommentDto> CreateAsync(Guid sheetId, Guid itemId, CreateCommentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Edits a comment. Only its author may; others get <see cref="UnauthorizedAccessException"/>.</summary>
    Task<CommentDto> UpdateAsync(Guid sheetId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a comment and its replies. Only its author may.</summary>
    Task DeleteAsync(Guid sheetId, Guid commentId, CancellationToken cancellationToken = default);
}
