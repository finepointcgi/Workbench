using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Approvals;
using Workbench.Ui.Approvals;

namespace Workbench.Tests.Approvals;

public sealed class DecideHereTests
{
    private static ApprovalInboxItemDto Item(Guid id, string link, string type = ApprovalDocumentTypes.Timesheet) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, ApprovalDocumentTypes.Label(type), id, $"Alex: {link}", "40.0 h", 40m, "alex@example.com", "Manager", null, null, 0, null, link,
            SubmitterName: "Alex");

    private sealed class FakeInbox(List<ApprovalInboxItemDto> items) : IApprovalInboxService
    {
        public List<(string Type, Guid Id, ApprovalDecisionKind Kind)> Decided { get; } = [];

        public Task<IReadOnlyList<ApprovalInboxItemDto>> ListMineAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApprovalInboxItemDto>>(items.ToList());

        public Task<int> CountMineAsync(CancellationToken cancellationToken = default) => Task.FromResult(items.Count);

        public Task DecideAsync(string documentType, Guid documentId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default)
        {
            Decided.Add((documentType, documentId, request.Decision));
            items.RemoveAll(i => i.DocumentId == documentId);
            return Task.CompletedTask;
        }

        public Task<BulkApprovalResultDto> DecideManyAsync(BulkApprovalRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ApprovalInstanceDto>> GetHistoryAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static BunitContext Context(FakeInbox inbox)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IApprovalInboxService>(inbox);
        ctx.Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return ctx;
    }

    [Fact]
    public async Task A_document_waiting_on_you_can_be_approved_where_you_are_looking_at_it_and_points_to_the_next()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var inbox = new FakeInbox([Item(mine, "time/history/a"), Item(other, "time/history/b")]);
        await using var ctx = Context(inbox);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("time/history/a");
        var cut = ctx.Render<DecideHere>();

        cut.WaitForAssertion(() => Assert.Contains("Timesheet waiting on you", cut.Find("[data-testid='decide-here']").TextContent));
        cut.Find("[data-testid='here-approve']").Click();

        // The page reloads so what's underneath shows the new state; the outcome rides in the address.
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.Contains("decided=approved", navigation.Uri));
        Assert.Equal([(ApprovalDocumentTypes.Timesheet, mine, ApprovalDecisionKind.Approve)], inbox.Decided);

        await using var reloaded = Context(inbox);
        reloaded.Services.GetRequiredService<NavigationManager>().NavigateTo("time/history/a?decided=approved");
        var after = reloaded.Render<DecideHere>();
        after.WaitForAssertion(() => Assert.Contains("Approved.", after.Find("[data-testid='decide-here-done']").TextContent));
        Assert.Contains("(1 waiting)", after.Find("[data-testid='here-next']").TextContent);
        Assert.Equal("time/history/b", after.Find("[data-testid='here-next']").GetAttribute("href"));
    }

    [Fact]
    public async Task Pages_with_nothing_waiting_on_you_show_nothing()
    {
        var inbox = new FakeInbox([Item(Guid.NewGuid(), "time/history/a")]);
        await using var ctx = Context(inbox);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("time/history/zzz");
        var cut = ctx.Render<DecideHere>();

        await Task.Delay(100);
        Assert.Empty(cut.FindAll("[data-testid='decide-here']"));
        Assert.Empty(cut.FindAll("[data-testid='decide-here-many']"));
    }

    [Fact]
    public async Task Several_items_on_one_page_send_you_to_Approvals_instead_of_guessing()
    {
        var inbox = new FakeInbox([Item(Guid.NewGuid(), "leave?tab=approvals", ApprovalDocumentTypes.LeaveRequest), Item(Guid.NewGuid(), "leave?tab=approvals", ApprovalDocumentTypes.LeaveRequest)]);
        await using var ctx = Context(inbox);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("leave?tab=approvals");
        var cut = ctx.Render<DecideHere>();

        cut.WaitForAssertion(() => Assert.Contains("2 items on this page", cut.Find("[data-testid='decide-here-many']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='here-approve']"));
    }
}
