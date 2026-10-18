using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Leave;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Time;
using Workbench.Tests.Mobile;
using Workbench.Ui.Home;
using Workbench.Ui.Pages;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.MyTodo.MyTodoKit;

namespace Workbench.Tests.MyTodo;

public sealed class HomePageTests
{
    private static readonly Guid LeaveId = Guid.NewGuid();
    private static readonly Guid SheetId = Guid.NewGuid();

    private sealed class FixedTime(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(local, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static MyTodoItem Item(string key, TodoUrgency urgency, DateOnly? due, string title, TodoInlineAction? action = null) =>
        new(key, TodoCategories.Timesheet, title, $"Because {key}", urgency, due, $"go/{key}", "Open", action);

    private static ApprovalInboxItemDto Inbox(string type, string title, Guid? id = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, ApprovalDocumentTypes.Label(type), id ?? Guid.NewGuid(), title, "16.0 h", 16m, Editor, "Supervisor", null, DateTime.UtcNow, 1, null, "approvals");

    private static MyTodoDto Todo(IReadOnlyList<MyTodoItem>? items = null, ApprovalsPreviewDto? approvals = null, IReadOnlyList<string>? unavailable = null) =>
        new("Alex Rivera", Today, items ?? [], new MyWeekDto(Monday, Monday.AddDays(6), 16m, 40m, TimesheetState.Draft, [new LeaveBalanceChipDto("Paid time off", 48m)], 32m, 2),
            approvals, unavailable ?? []);

    private static (BunitContext Ctx, Fake<IMyTodoService> Todo) Context(params MyTodoDto[] loads)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<TimeProvider>(new FixedTime(new DateTime(2026, 10, 21, 9, 30, 0)));
        var (service, fake) = Fake<IMyTodoService>.Create();
        var queue = new Queue<MyTodoDto>(loads);
        fake.On(nameof(IMyTodoService.GetAsync), _ => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        ctx.Services.AddSingleton(service);
        var (myWork, mf) = Fake<IMyWorkService>.Create();
        mf.Returns(nameof(IMyWorkService.GetAsync), new MyWorkDto(Editor, Today, Today.AddDays(4), new MyWorkCounts(0, 0, 0, 0), []));
        ctx.Services.AddSingleton(myWork);
        ctx.Services.AddSingleton(Fake<IItemService>.Create().Service);
        ctx.Services.AddSingleton(Fake<IApprovalInboxService>.Create().Service); // tests that approve register their own
        return (ctx, fake);
    }

    [Fact]
    public async Task Greets_by_first_name_and_lists_todos_by_urgency_with_the_week_and_approvals()
    {
        var items = new[]
        {
            Item("overdue", TodoUrgency.Overdue, Today.AddDays(-1), "Enter your hours for Tuesday"),
            Item("today", TodoUrgency.Today, Today, "Fix your timesheet"),
            Item("week", TodoUrgency.ThisWeek, Today.AddDays(2), "Submit \"DC program review\""),
        };
        var approvals = new ApprovalsPreviewDto(7, [Inbox(ApprovalDocumentTypes.Timesheet, "Val Chen · Oct 12–18"), Inbox(ApprovalDocumentTypes.ExpenseReport, "Val's trip")]);
        var (ctx, _) = Context(Todo(items, approvals));
        await using var _ctx = ctx;

        var page = ctx.Render<Home>();

        page.WaitForAssertion(() => Assert.Equal("Good morning, Alex", page.Find("[data-testid='greeting']").TextContent.Trim()));
        Assert.Equal("Wednesday, October 21", page.Find("[data-testid='today']").TextContent.Trim());
        Assert.Equal(["Overdue", "Today", "ThisWeek"], page.FindAll("[data-urgency]").Select(e => e.GetAttribute("data-urgency")));
        Assert.Equal(items.Select(i => i.Title), page.FindAll("[data-testid='todo-title']").Select(e => e.TextContent.Trim()));
        Assert.Equal("go/overdue", page.Find("[data-todo='overdue'] a").GetAttribute("href"));
        Assert.Contains("Was due Oct 20", page.Find("[data-todo='overdue']").TextContent);
        Assert.Empty(page.FindAll("[data-tile]")); // the counter tiles are gone

        var week = page.Find("[data-card='this-week']");
        Assert.Contains("16", page.Find("[data-testid='hours']").TextContent);
        Assert.Contains("of 40 hours entered", week.TextContent);
        Assert.Contains("Not submitted", week.TextContent);
        Assert.Contains("Paid time off: 48 h available", week.TextContent);
        Assert.Contains("32 h on 2 projects", week.TextContent);

        var waiting = page.Find("[data-card='waiting-on-you']");
        Assert.Contains("7", waiting.TextContent);
        Assert.Single(page.FindAll("[data-testid='quick-approve']")); // only the timesheet; the expense report opens for review
        Assert.Contains("My sheet items", page.Markup);
    }

    [Fact]
    public async Task Empty_list_says_youre_all_caught_up()
    {
        var (ctx, _) = Context(Todo() with { Week = null });
        await using var _ctx = ctx;

        var page = ctx.Render<Home>();

        page.WaitForAssertion(() => Assert.Contains("You're all caught up", page.Find("[data-testid='all-caught-up']").TextContent));
        Assert.Empty(page.FindAll("[data-card='this-week']"));
        Assert.Empty(page.FindAll("[data-card='waiting-on-you']"));
    }

    [Fact]
    public async Task A_source_that_failed_is_named_and_the_rest_still_shows()
    {
        var (ctx, _) = Context(Todo([Item("a", TodoUrgency.Today, Today, "Fix your timesheet")], unavailable: ["Payroll"]));
        await using var _ctx = ctx;

        var page = ctx.Render<Home>();

        page.WaitForAssertion(() => Assert.Contains("Payroll", page.Find("[data-testid='unavailable']").TextContent));
        Assert.Single(page.FindAll("[data-todo]"));
    }

    [Fact]
    public async Task Inline_leave_approval_removes_the_item_and_reloads()
    {
        var approve = Item($"leave:approve:{LeaveId}", TodoUrgency.ThisWeek, Today.AddDays(3), "Approve Val Chen's leave",
            new TodoInlineAction(TodoInlineActionKind.ApproveLeave, "Approve", LeaveId));
        var (ctx, todo) = Context(Todo([approve]), Todo());
        await using var _ctx = ctx;
        var (leave, lf) = Fake<ILeaveRequestService>.Create();
        lf.Returns(nameof(ILeaveRequestService.ApproveAsync), null);
        ctx.Services.AddSingleton(leave);
        ctx.Render<MudBlazor.MudPopoverProvider>();

        var page = ctx.Render<Home>();
        page.WaitForAssertion(() => page.Find("[data-testid='todo-action']"));
        page.Find("[data-testid='todo-action']").Click();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='all-caught-up']")));
        Assert.Equal(LeaveId, lf.Single(nameof(ILeaveRequestService.ApproveAsync))[0]);
        Assert.Equal(2, todo.Calls.Count); // reloaded after the action
    }

    [Fact]
    public async Task Quick_approve_decides_the_timesheet_through_the_inbox()
    {
        var sheet = Inbox(ApprovalDocumentTypes.Timesheet, "Val Chen · Oct 12–18", SheetId);
        var (ctx, todo) = Context(Todo(approvals: new ApprovalsPreviewDto(1, [sheet])), Todo());
        await using var _ctx = ctx;
        var (inbox, inf) = Fake<IApprovalInboxService>.Create();
        inf.Returns(nameof(IApprovalInboxService.DecideAsync), null);
        ctx.Services.AddSingleton(inbox);

        var page = ctx.Render<Home>();
        page.WaitForAssertion(() => page.Find("[data-testid='quick-approve']"));
        page.Find("[data-testid='quick-approve']").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-card='waiting-on-you']")));
        var args = inf.Single(nameof(IApprovalInboxService.DecideAsync));
        Assert.Equal((ApprovalDocumentTypes.Timesheet, SheetId, ApprovalDecisionKind.Approve), ((string)args[0]!, (Guid)args[1]!, ((ApprovalDecisionRequest)args[2]!).Decision));
        Assert.Equal(2, todo.Calls.Count);
    }

    [Fact]
    public async Task Mobile_today_shows_the_first_few_todos_with_the_rest_one_tap_away()
    {
        var items = Enumerable.Range(1, 5).Select(i => Item($"k{i}", TodoUrgency.Today, Today, $"Task {i}")).ToList();
        var (ctx, _) = Context(Todo(items));
        await using var _ctx = ctx;

        var panel = ctx.Render<MobileTodo>();

        panel.WaitForAssertion(() => Assert.Equal(3, panel.FindAll("[data-todo]").Count));
        panel.Find("[data-testid='todo-more']").Click();
        Assert.Equal(5, panel.FindAll("[data-todo]").Count);
    }

    [Fact]
    public async Task Mobile_todo_is_hidden_without_the_service()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var panel = ctx.Render<MobileTodo>();

        Assert.Empty(panel.FindAll("[data-testid='mobile-todo']"));
    }
}
