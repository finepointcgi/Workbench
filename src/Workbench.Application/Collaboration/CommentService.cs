using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Domain;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Collaboration;

public sealed partial class CommentService(
    IWorkbenchDbContextFactory dbFactory,
    ActivityRecorder activity,
    ICurrentUser currentUser,
    TimeProvider clock) : ICommentService
{
    public const int MaxBodyLength = 10_000;

    public async Task<IReadOnlyList<CommentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindItemAsync(db, sheetId, itemId, cancellationToken);

        var comments = await db.Set<Comment>()
            .AsNoTracking()
            .Where(c => c.SheetId == sheetId && c.ItemId == itemId)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var replies = comments.Where(c => c.ParentCommentId is not null).ToLookup(c => c.ParentCommentId!.Value);
        return comments
            .Where(c => c.ParentCommentId is null)
            .Select(c => ToDto(c, replies[c.Id].Select(r => ToDto(r, [])).ToList()))
            .ToList();
    }

    public async Task<CommentDto> CreateAsync(Guid sheetId, Guid itemId, CreateCommentRequest request, CancellationToken cancellationToken = default)
    {
        var body = ValidateBody(request.Body);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await FindItemAsync(db, sheetId, itemId, cancellationToken);

        Guid? parentId = null;
        if (request.ParentCommentId is { } requestedParent)
        {
            var parent = await db.Set<Comment>().FirstOrDefaultAsync(c => c.Id == requestedParent && c.ItemId == itemId && c.SheetId == sheetId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Comment", requestedParent);

            // Threads are one level deep: a reply to a reply joins the top-level comment's thread.
            parentId = parent.ParentCommentId ?? parent.Id;
        }

        var comment = new Comment
        {
            SheetId = sheetId,
            ItemId = itemId,
            ParentCommentId = parentId,
            AuthorEmail = currentUser.Email,
            Body = body,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            Mentions = ParseMentions(body),
        };
        db.Set<Comment>().Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        var summary = $"{(parentId is null ? "Commented" : "Replied")} on \"{item.Title}\"";
        if (comment.Mentions.Count > 0)
        {
            summary += $" and mentioned {string.Join(", ", comment.Mentions)}";
        }

        await activity.RecordAsync(sheetId, itemId, ActivityKinds.CommentAdded, summary, SheetChangeKind.CommentAdded);
        return ToDto(comment, []);
    }

    public async Task<CommentDto> UpdateAsync(Guid sheetId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default)
    {
        var body = ValidateBody(request.Body);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var comment = await FindOwnCommentAsync(db, sheetId, commentId, cancellationToken);
        comment.Body = body;
        comment.Mentions = ParseMentions(body);
        comment.EditedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var title = await ItemTitleAsync(db, comment.ItemId, cancellationToken);
        await activity.RecordAsync(sheetId, comment.ItemId, ActivityKinds.CommentEdited, $"Edited a comment on \"{title}\"", SheetChangeKind.CommentUpdated);

        var replies = await db.Set<Comment>().AsNoTracking().Where(c => c.ParentCommentId == commentId).OrderBy(c => c.CreatedAtUtc).ToListAsync(cancellationToken);
        return ToDto(comment, replies.Select(r => ToDto(r, [])).ToList());
    }

    public async Task DeleteAsync(Guid sheetId, Guid commentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var comment = await FindOwnCommentAsync(db, sheetId, commentId, cancellationToken);
        var replies = await db.Set<Comment>().Where(c => c.ParentCommentId == commentId).ToListAsync(cancellationToken);
        db.Set<Comment>().RemoveRange([comment, .. replies]);
        await db.SaveChangesAsync(cancellationToken);

        var title = await ItemTitleAsync(db, comment.ItemId, cancellationToken);
        await activity.RecordAsync(sheetId, comment.ItemId, ActivityKinds.CommentDeleted, $"Deleted a comment on \"{title}\"", SheetChangeKind.CommentDeleted);
    }

    /// <summary>Emails written as <c>@name@example.com</c>, lower-cased and de-duplicated, in order of appearance.</summary>
    public static List<string> ParseMentions(string body) =>
        MentionPattern().Matches(body)
            .Select(m => m.Groups["email"].Value.TrimEnd('.').ToLowerInvariant())
            .Distinct()
            .ToList();

    [GeneratedRegex(@"(?<![\w.@])@(?<email>[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})")]
    private static partial Regex MentionPattern();

    private static string ValidateBody(string? body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        body = body.Trim();
        if (body.Length > MaxBodyLength)
        {
            throw new ArgumentException($"Comments can be at most {MaxBodyLength:N0} characters.", nameof(body));
        }

        return body;
    }

    private CommentDto ToDto(Comment c, IReadOnlyList<CommentDto> replies) =>
        new(c.Id, c.ItemId, c.ParentCommentId, c.AuthorEmail, c.Body, c.CreatedAtUtc, c.EditedAtUtc, c.Mentions.ToList(),
            string.Equals(c.AuthorEmail, currentUser.Email, StringComparison.OrdinalIgnoreCase), replies);

    private async Task<Comment> FindOwnCommentAsync(IWorkbenchDbContext db, Guid sheetId, Guid commentId, CancellationToken cancellationToken)
    {
        var comment = await db.Set<Comment>().FirstOrDefaultAsync(c => c.Id == commentId && c.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Comment", commentId);
        if (!string.Equals(comment.AuthorEmail, currentUser.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Only the author can change this comment.");
        }

        return comment;
    }

    private static async Task<Item> FindItemAsync(IWorkbenchDbContext db, Guid sheetId, Guid itemId, CancellationToken cancellationToken) =>
        await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Item", itemId);

    private static async Task<string> ItemTitleAsync(IWorkbenchDbContext db, Guid itemId, CancellationToken cancellationToken) =>
        await db.Items.Where(i => i.Id == itemId).Select(i => i.Title).FirstOrDefaultAsync(cancellationToken) ?? "a deleted item";
}
