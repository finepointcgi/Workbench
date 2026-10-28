using Workbench.Contracts.Approvals;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.MyWork;
using Workbench.Ui.Home;

namespace Workbench.Tests.MyTodo;

public sealed class ApprovalsOnHomeTests
{
    private static MyTodoItem Item(string key) => new(key, TodoCategories.Approval, key, "why", TodoUrgency.Today, null, "go", "Open");

    private static ApprovalInboxItemDto Waiting() =>
        new(Guid.NewGuid(), Guid.NewGuid(), ApprovalDocumentTypes.Timesheet, "Timesheet", Guid.NewGuid(), "Alex: timesheet", null, 16m, "alex@example.com", "Manager", null, null, 0, null, "time/history/x");

    private static MyTodoDto Todo(ApprovalsPreviewDto? approvals) =>
        new("Pat", new DateOnly(2026, 10, 28), [Item("approvals:inbox"), Item("timesheet:mine")], null, approvals, []);

    [Fact]
    public void The_approvals_roll_up_is_left_out_when_the_Waiting_on_you_card_lists_the_same_things()
    {
        var todo = Todo(new ApprovalsPreviewDto(1, [Waiting()]));

        Assert.Equal(["timesheet:mine"], TodoFormat.ItemsBesideApprovalsCard(todo, cardShown: true).Select(i => i.Key));
    }

    [Fact]
    public void Without_the_card_or_without_approvals_the_roll_up_stays()
    {
        Assert.Equal(2, TodoFormat.ItemsBesideApprovalsCard(Todo(new ApprovalsPreviewDto(1, [Waiting()])), cardShown: false).Count);
        Assert.Equal(2, TodoFormat.ItemsBesideApprovalsCard(Todo(null), cardShown: true).Count);
    }
}
