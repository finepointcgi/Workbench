using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Collaboration;

namespace Workbench.Tests.Collaboration;

public sealed class ItemDetailsDrawerTests
{
    private static readonly ColumnDto Owner = new(Guid.NewGuid(), "Owner", ColumnKind.Person, 0, [], null);
    private static readonly SheetDetail Sheet = new(
        new SheetSummary(Guid.NewGuid(), "Plan", null, "General", 2, true, null, DateTime.UtcNow), [Owner]);
    private static readonly ItemDto Kickoff = new(Guid.NewGuid(), null, 0, "Kickoff meeting", new DateOnly(2026, 11, 2), null, null,
        new Dictionary<string, string?> { [Owner.Id.ToString()] = "pm@example.com" });
    private static readonly ItemDto Review = new(Guid.NewGuid(), null, 1, "Review", null, null, null,
        new Dictionary<string, string?> { [Owner.Id.ToString()] = "analyst@example.com" });

    [Fact]
    public async Task Opens_for_the_item_in_the_query_and_shows_values_and_comments()
    {
        await using var ctx = CreateContext();
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"sheets/{Sheet.Summary.Id}?item={Kickoff.Id}");

        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains("Kickoff meeting", cut.Markup));
        Assert.Contains("pm@example.com", cut.Markup);
        Assert.Contains("Nov 2, 2026", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("Looks good", cut.Markup));
    }

    [Fact]
    public async Task Stays_closed_without_an_item_query()
    {
        await using var ctx = CreateContext();

        var cut = Render(ctx);

        Assert.DoesNotContain("Kickoff meeting", cut.Markup);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("not-a-guid")]
    public async Task Ignores_a_malformed_item_query(string value)
    {
        await using var ctx = CreateContext();
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"sheets/{Sheet.Summary.Id}?item={value}");

        var cut = Render(ctx);

        Assert.DoesNotContain("Kickoff meeting", cut.Markup);
    }

    [Fact]
    public async Task Mention_composer_suggests_people_and_completes_the_email()
    {
        await using var ctx = CreateContext();
        var cut = ctx.Render<MentionComposer>(p => p
            .Add(c => c.People, ["analyst@example.com", "pm@example.com"])
            .Add(c => c.OnSubmit, _ => Task.FromResult(true)));

        cut.Find("textarea").Input("Can you look @an");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=option]")));
        cut.Find("[role=option]").Click();

        Assert.Equal("Can you look @analyst@example.com ", cut.Find("textarea").GetAttribute("value"));
    }

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICommentService>(new FakeComments());
        ctx.Services.AddSingleton<IAttachmentService>(new FakeAttachments());
        ctx.Services.AddSingleton<IActivityService>(new FakeActivity());
        return ctx;
    }

    private static IRenderedComponent<MudLayout> Render(BunitContext ctx)
    {
        ctx.Render<MudPopoverProvider>();
        return ctx.Render<MudLayout>(p => p.AddChildContent<ItemDetailsDrawer>(d => d
            .Add(c => c.Sheet, Sheet)
            .Add(c => c.Items, [Kickoff, Review])));
    }

    private sealed class FakeComments : ICommentService
    {
        public Task<IReadOnlyList<CommentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CommentDto>>(
                [new CommentDto(Guid.NewGuid(), itemId, null, "pm@example.com", "Looks good @analyst@example.com", DateTime.UtcNow, null, ["analyst@example.com"], false, [])]);

        public Task<CommentDto> CreateAsync(Guid sheetId, Guid itemId, CreateCommentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CommentDto> UpdateAsync(Guid sheetId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid sheetId, Guid commentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAttachments : IAttachmentService
    {
        public Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttachmentDto>>([]);

        public Task<AttachmentDto> UploadAsync(Guid sheetId, Guid itemId, string fileName, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AttachmentContent> DownloadAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string GetDownloadUrl(Guid sheetId, Guid attachmentId) => $"files/{attachmentId}";
        public Task DeleteAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeActivity : IActivityService
    {
        public Task<IReadOnlyList<ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityDto>>([]);
    }
}
