using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;

namespace Workbench.Tests.Collaboration;

public sealed class CommentServiceTests
{
    [Fact]
    public async Task Create_parses_mentions_records_activity_and_publishes()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync("Kickoff");

        var comment = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Ping @PM@example.com and @pm@example.com, thanks"));

        Assert.Equal(["pm@example.com"], comment.Mentions);
        Assert.Equal("alice@example.com", comment.AuthorEmail);
        Assert.True(comment.CanEdit);
        var entry = (await host.Activity.ListAsync(sheetId, item.Id))[0];
        Assert.Equal(ActivityKinds.CommentAdded, entry.Kind);
        Assert.Equal("Commented on \"Kickoff\" and mentioned pm@example.com", entry.Summary);
        Assert.Contains(host.Changes, c => c.Kind == SheetChangeKind.CommentAdded && c.ItemId == item.Id);
    }

    [Theory]
    [InlineData("email me at bob@example.com", new string[0])]
    [InlineData("@a.b@example.co.uk.", new[] { "a.b@example.co.uk" })]
    [InlineData("(@x@example.com) @y@example.com", new[] { "x@example.com", "y@example.com" })]
    public void ParseMentions_finds_at_prefixed_emails_only(string body, string[] expected) =>
        Assert.Equal(expected, CommentService.ParseMentions(body));

    [Fact]
    public async Task Reply_to_a_reply_joins_the_top_level_thread()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();
        var root = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Root"));
        var reply = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Reply", root.Id));

        var nested = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Nested", reply.Id));

        Assert.Equal(root.Id, nested.ParentCommentId);
        var thread = Assert.Single(await host.Comments.ListAsync(sheetId, item.Id));
        Assert.Equal(["Reply", "Nested"], thread.Replies.Select(r => r.Body));
    }

    [Fact]
    public async Task Only_the_author_can_edit_or_delete()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();
        var comment = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Mine"));

        host.User.Email = "bob@example.com";
        Assert.False((await host.Comments.ListAsync(sheetId, item.Id))[0].CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Comments.UpdateAsync(sheetId, comment.Id, new UpdateCommentRequest("Hijacked")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Comments.DeleteAsync(sheetId, comment.Id));

        host.User.Email = "alice@example.com";
        var edited = await host.Comments.UpdateAsync(sheetId, comment.Id, new UpdateCommentRequest("Edited @bob@example.com"));
        Assert.NotNull(edited.EditedAtUtc);
        Assert.Equal(["bob@example.com"], edited.Mentions);
    }

    [Fact]
    public async Task Delete_removes_replies()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();
        var root = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Root"));
        await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("Reply", root.Id));

        await host.Comments.DeleteAsync(sheetId, root.Id);

        Assert.Empty(await host.Comments.ListAsync(sheetId, item.Id));
    }

    [Fact]
    public async Task Rejects_blank_body_and_unknown_item()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("  ")));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Comments.CreateAsync(sheetId, Guid.NewGuid(), new CreateCommentRequest("Hi")));
    }
}
