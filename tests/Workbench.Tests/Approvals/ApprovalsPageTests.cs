using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Approvals;
using Workbench.Tests.Mobile;
using Workbench.Ui.Approvals;

namespace Workbench.Tests.Approvals;

/// <summary>The inbox page: filters, selecting all of a type, and one bulk decision with its summary.</summary>
public sealed class ApprovalsPageTests
{
    private static ApprovalInboxItemDto Item(string type, string title, Guid? projectId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, ApprovalDocumentTypes.Label(type), Guid.NewGuid(), title, null, 8m, "val@example.com", "Supervisor", null,
            DateTime.UtcNow, 0, null, "approvals", projectId);

    private static readonly ApprovalInboxItemDto Mine = Item(ApprovalDocumentTypes.Timesheet, "Val Chen: timesheet", Guid.NewGuid());
    private static readonly ApprovalInboxItemDto Other = Item(ApprovalDocumentTypes.Timesheet, "Kim Ray: timesheet");
    private static readonly ApprovalInboxItemDto Trip = Item(ApprovalDocumentTypes.ExpenseReport, "Val's trip");

    private static (BunitContext Ctx, Fake<IApprovalInboxService> Inbox) Context(params IReadOnlyList<ApprovalInboxItemDto>[] loads)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var (inbox, fake) = Fake<IApprovalInboxService>.Create();
        var queue = new Queue<IReadOnlyList<ApprovalInboxItemDto>>(loads);
        fake.On(nameof(IApprovalInboxService.ListMineAsync), _ => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        ctx.Services.AddSingleton(inbox);
        var (delegations, df) = Fake<IApprovalDelegationService>.Create();
        df.Returns(nameof(IApprovalDelegationService.ListAsync), (IReadOnlyList<ApprovalDelegationDto>)[]);
        ctx.Services.AddSingleton(delegations);
        return (ctx, fake);
    }

    [Fact]
    public async Task Selecting_all_of_a_type_and_approving_sends_one_bulk_decision_and_shows_its_summary()
    {
        var (ctx, inbox) = Context([Mine, Other, Trip], [Trip]);
        await using var _ctx = ctx;
        inbox.Returns(nameof(IApprovalInboxService.DecideManyAsync), new BulkApprovalResultDto(1, 1, "1 approved, 1 couldn't be: you entered it yourself",
            [new(Mine.DocumentType, Mine.DocumentId, Mine.Title, true, null), new(Other.DocumentType, Other.DocumentId, Other.Title, false, "you entered it yourself")]));

        var page = ctx.Render<ApprovalsPage>();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("[data-testid='inbox-item']").Count));
        Assert.Contains("Time on my projects (1)", page.Find("[data-testid='filter-my-projects']").TextContent);

        page.Find($"[data-testid='filter-{ApprovalDocumentTypes.Timesheet}']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid='inbox-item']").Count));
        page.Find("[data-testid='inbox'] thead input[type=checkbox]").Change(true);
        page.WaitForAssertion(() => Assert.Equal("2 selected", page.Find("[data-testid='bulk-count']").TextContent.Trim()));
        Assert.True(page.Find("[data-testid='bulk-reject']").HasAttribute("disabled")); // Rejecting needs the comment as its reason.

        page.Find("[data-testid='bulk-approve']").Click();

        page.WaitForAssertion(() => Assert.Contains("1 approved, 1 couldn't be: you entered it yourself", page.Find("[data-testid='bulk-result']").TextContent));
        var request = (BulkApprovalRequest)inbox.Single(nameof(IApprovalInboxService.DecideManyAsync))[0]!;
        Assert.Equal(ApprovalDecisionKind.Approve, request.Decision);
        Assert.Equal(new[] { Mine.DocumentId, Other.DocumentId }.Order(), request.Items.Select(i => i.DocumentId).Order());
        Assert.Contains("Kim Ray: timesheet: you entered it yourself", page.Find("[data-testid='bulk-failure']").TextContent);
        Assert.Single(page.FindAll("[data-testid='inbox-item']")); // Reloaded; the filter falls back to All.
        Assert.Empty(page.FindAll("[data-testid='bulk-bar']"));
    }

    [Fact]
    public async Task The_my_projects_link_opens_on_time_charged_to_my_projects()
    {
        var (ctx, _) = Context([Mine, Other, Trip]);
        await using var _ctx = ctx;
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("approvals?show=my-projects");

        var page = ctx.Render<ApprovalsPage>();

        page.WaitForAssertion(() => Assert.Contains("Val Chen: timesheet", Assert.Single(page.FindAll("[data-testid='inbox-item']")).TextContent));
    }
}
