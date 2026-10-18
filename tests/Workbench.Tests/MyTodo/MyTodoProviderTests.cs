using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Revenue;
using Workbench.Contracts.Sheets;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;
using Workbench.Tests.Mobile;
using static Workbench.Tests.MyTodo.MyTodoKit;

namespace Workbench.Tests.MyTodo;

/// <summary>Each provider's rules, with the services it reads stubbed.</summary>
public sealed class MyTodoProviderTests
{
    private static MyTodoContext Context(IServiceProvider? services = null, OrgRoles roles = OrgRoles.Employee, string email = Editor) =>
        new(new CurrentUserInfo(email, "Alex Rivera", roles, Guid.NewGuid()), Today, services ?? new ServiceCollection().BuildServiceProvider(), null!);

    [Theory]
    [InlineData(-1, TodoUrgency.Overdue)]
    [InlineData(0, TodoUrgency.Today)]
    [InlineData(4, TodoUrgency.ThisWeek)] // Sunday Oct 25 ends the week
    [InlineData(5, TodoUrgency.Later)]
    public void Urgency_follows_the_due_date(int days, TodoUrgency expected) =>
        Assert.Equal(expected, MyTodoRules.UrgencyFor(Today.AddDays(days), Today));

    [Fact]
    public void Sort_orders_by_urgency_due_date_and_priority_and_drops_duplicate_keys()
    {
        MyTodoItem Item(string key, TodoUrgency urgency, DateOnly? due, int priority = 0) => new(key, "x", key, "", urgency, due, "", "", Priority: priority);
        var sorted = MyTodoRules.Sort([
            Item("later", TodoUrgency.Later, null), Item("week-undated", TodoUrgency.ThisWeek, null), Item("week-fri", TodoUrgency.ThisWeek, Today.AddDays(2)),
            Item("today-low", TodoUrgency.Today, Today), Item("today-high", TodoUrgency.Today, Today, 5), Item("overdue", TodoUrgency.Overdue, Today.AddDays(-9)),
            Item("overdue", TodoUrgency.Later, null),
        ]);

        Assert.Equal(["overdue", "today-high", "today-low", "week-fri", "week-undated", "later"], sorted.Select(i => i.Key));
    }

    [Fact]
    public void Ranges_read_naturally() =>
        Assert.Equal(["Oct 5–7", "Oct 30–Nov 2", "Oct 5"], new[]
        {
            MyTodoRules.Range(new(2026, 10, 5), new(2026, 10, 7)), MyTodoRules.Range(new(2026, 10, 30), new(2026, 11, 2)), MyTodoRules.Range(new(2026, 10, 5), new(2026, 10, 5)),
        });

    [Fact]
    public void Missing_days_skip_weekends_today_holidays_and_days_before_hire()
    {
        var employee = new Employee { EmployeeNumber = "E", Email = Editor, DisplayName = "Alex", HireDate = new DateOnly(2026, 10, 13) };

        var missing = TimesheetTodoProvider.MissingDays(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 20), employee,
            [new DateOnly(2026, 10, 13)], [new DateOnly(2026, 10, 16)]);

        Assert.Equal([new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 19)], missing);
    }

    [Fact]
    public void Approvals_become_overdue_after_two_business_days_and_say_what_is_waiting()
    {
        ApprovalInboxItemDto Inbox(string type, string label, int days) => new(Guid.NewGuid(), Guid.NewGuid(), type, label, Guid.NewGuid(), $"{label} {days}", null, null,
            Editor, "Supervisor", null, DateTime.UtcNow.AddDays(-days), days, null, "approvals");

        var fresh = ApprovalTodoProvider.For([Inbox(ApprovalDocumentTypes.Timesheet, "Timesheet", 1)], Context());
        Assert.Equal(TodoUrgency.Today, fresh.Urgency);
        Assert.Equal("Review Timesheet 1", fresh.Title);

        var old = ApprovalTodoProvider.For([Inbox(ApprovalDocumentTypes.Timesheet, "Timesheet", 0), Inbox(ApprovalDocumentTypes.Timesheet, "Timesheet", 3),
            Inbox(ApprovalDocumentTypes.ExpenseReport, "Expense report", 1)], Context());
        Assert.Equal(TodoUrgency.Overdue, old.Urgency);
        Assert.Equal("Review 3 approvals waiting on you", old.Title);
        Assert.Equal("2 timesheets, 1 expense report; the oldest has waited 3 business days.", old.Reason);
        Assert.Equal("approvals", old.Link);
    }

    [Fact]
    public void Eac_is_needed_for_cost_to_cost_projects_every_90_days_and_after_a_rejection()
    {
        var project = new Project { Code = "1001", Name = "Modernization" };
        EstimateDto Estimate(DateOnly asOf, EstimateState state, string? note = null) =>
            new(Guid.NewGuid(), project.Id, "1001", null, null, asOf, 1m, 2m, null, state, Owner, asOf.ToDateTime(TimeOnly.MinValue), null, null, note);
        RevenueSetupDto Setup(RevenueMethodKind method) => new(Guid.NewGuid(), project.Id, "1001", "M", null, null, null, null, method, null, null, null, null, null, Finance, DateTime.UtcNow);
        ProjectRevenueDto Detail(RevenueMethodKind method, params EstimateDto[] estimates) =>
            new(project.Id, "1001", "M", Guid.NewGuid(), "C1", "CPFF", [], null, [Setup(method)], estimates, [], [], true);
        var context = Context();

        Assert.Null(EacReviewTodoProvider.For(project, Detail(RevenueMethodKind.Billings), context)); // no EAC used
        Assert.Equal("Submit an estimate at completion for 1001", EacReviewTodoProvider.For(project, Detail(RevenueMethodKind.CostToCost), context)!.Title);
        Assert.Null(EacReviewTodoProvider.For(project, Detail(RevenueMethodKind.CostToCost, Estimate(Today.AddDays(-30), EstimateState.Approved)), context));

        var due = EacReviewTodoProvider.For(project, Detail(RevenueMethodKind.CostToCost, Estimate(Today.AddDays(-95), EstimateState.Approved)), context)!;
        Assert.Equal((TodoUrgency.Overdue, Today.AddDays(-5)), (due.Urgency, due.Due));
        Assert.Equal($"projects/{project.Id}/revenue", due.Link);

        Assert.Null(EacReviewTodoProvider.For(project, Detail(RevenueMethodKind.CostToCost, Estimate(Today.AddDays(-95), EstimateState.Submitted)), context)); // with Finance

        var rejected = EacReviewTodoProvider.For(project, Detail(RevenueMethodKind.CostPlusFee, Estimate(Today.AddDays(-3), EstimateState.Rejected, "Too low")), context)!;
        Assert.Equal("Revise the EAC for 1001", rejected.Title);
        Assert.Contains("Too low", rejected.Reason);
    }

    [Fact]
    public async Task Variances_list_open_write_ups_due_after_the_reporting_period()
    {
        var periodId = Guid.NewGuid();
        EvmVarianceDto Var(string code, bool needs, string? cause = null, bool frozen = false) =>
            new(Guid.NewGuid(), code, code, periodId, "FY27 P1", needs, ["CV% over 10"], 0, -5000, 0, -12, 1, 1, 0, cause, cause, cause, null, null, null, frozen);
        var asked = new List<Guid>();
        var (evm, fake) = Fake<IEvmService>.Create();
        fake.On(nameof(IEvmService.GetBaselineAsync), args =>
        {
            asked.Add((Guid)args[0]!);
            return new EvmBaselineDto(Guid.NewGuid(), (Guid)args[0]!, "1001", Guid.NewGuid(), "Baseline", null, null, 2, 0, 0, 0, 0, 0, 0, 10, 0, false, DateTime.UtcNow, Finance, [], [], []);
        });
        fake.Returns(nameof(IEvmService.ListVariancesAsync), (IReadOnlyList<EvmVarianceDto>)[Var("1001.01", true), Var("1001.02", true, "Done"), Var("1001.03", false), Var("1001.04", true, frozen: true)]);
        fake.Returns(nameof(IEvmService.ListPeriodsAsync), (IReadOnlyList<EvmPeriodRefDto>)[new(periodId, "FY27 P1", new(2026, 10, 1), new(2026, 10, 31), EvmPeriodState.Reporting, null, null)]);
        await using var kit = await MyTodoKit.CreateAsync(services => services.AddSingleton(evm));

        var item = Assert.Single((await kit.GetAsync(Owner)).Items, i => i.Key.StartsWith("evm:", StringComparison.Ordinal));
        Assert.Equal("Explain the variance on 1001.01", item.Title);
        Assert.Equal(new DateOnly(2026, 11, 10), item.Due);
        Assert.Equal(TodoUrgency.Later, item.Urgency);
        Assert.Equal($"projects/{kit.Fx.Project1}/evm", item.Link);
        Assert.Equal([kit.Fx.Project1], asked);

        asked.Clear();
        Assert.DoesNotContain((await kit.GetAsync(Editor)).Items, i => i.Key.StartsWith("evm:", StringComparison.Ordinal));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Receiving_asks_only_named_receivers()
    {
        var mine = Guid.NewGuid();
        var notMine = Guid.NewGuid();
        PurchaseOrderSummaryDto Summary(Guid id, string number, PurchaseOrderState state) => new(id, number, Guid.NewGuid(), "ACME", "Acme Corp", new(2026, 10, 1), state, 0, 1200m, 0m, 0m, 1200m);
        var (orders, fake) = Fake<IPurchaseOrderService>.Create();
        fake.Returns(nameof(IPurchaseOrderService.ListReceivableAsync), (IReadOnlyList<PurchaseOrderSummaryDto>)[Summary(mine, "PO-1", PurchaseOrderState.Issued), Summary(notMine, "PO-2", PurchaseOrderState.Issued)]);
        fake.On(nameof(IPurchaseOrderService.GetAsync), args => new PurchaseOrderDto(Summary((Guid)args[0]!, "", PurchaseOrderState.Issued), null, null, null,
            (Guid)args[0]! == mine ? ["ANALYST@example.com"] : [Finance], Finance, null, null, null, [], [], [], [], false, true));
        var services = new ServiceCollection().AddSingleton(orders).BuildServiceProvider();

        var items = await new ReceivingTodoProvider().GetAsync(Context(services), default);

        var item = Assert.Single(items);
        Assert.Equal("Receive PO-1 from Acme Corp", item.Title);
        Assert.Equal($"purchasing/orders/{mine}", item.Link);
        Assert.DoesNotContain("$", item.Reason); // PO dollars are for Finance, as on the Receiving page

        var finance = await new ReceivingTodoProvider().GetAsync(Context(services, OrgRoles.Finance, "analyst@example.com"), default);
        Assert.Contains("$1,200", Assert.Single(finance).Reason);
    }

    [Fact]
    public async Task Sheet_items_overdue_or_due_within_a_week_are_listed()
    {
        var sheet = Guid.NewGuid();
        MyWorkItem Row(string title, int? days, MyWorkBucket bucket) =>
            new(sheet, "Schedule", new ItemDto(Guid.NewGuid(), null, 0, title, null, null, null, new Dictionary<string, string?>()), days is { } d ? Today.AddDays(d) : null, null, null, bucket);
        var work = new MyWorkDto(Editor, Today, Today.AddDays(4), new MyWorkCounts(0, 0, 0, 0),
            [Row("Late", -2, MyWorkBucket.Overdue), Row("Soon", 6, MyWorkBucket.Later), Row("Far", 30, MyWorkBucket.Later), Row("Undated", null, MyWorkBucket.NoDate), Row("Done", -1, MyWorkBucket.Done)]);
        var (myWork, fake) = Fake<IMyWorkService>.Create();
        fake.Returns(nameof(IMyWorkService.GetAsync), work);
        var services = new ServiceCollection().AddSingleton(myWork).BuildServiceProvider();

        var items = await new SheetTodoProvider().GetAsync(Context(services), default);

        Assert.Equal([("Late", TodoUrgency.Overdue), ("Soon", TodoUrgency.Later)], items.Select(i => (i.Title, i.Urgency)));
        Assert.Contains("was due", items[0].Reason);
    }

    [Fact]
    public void Close_checks_name_the_step_and_link_where_it_is_done()
    {
        var period = Guid.NewGuid();
        CloseCheckDto Check(CloseCheckKind kind, bool passed) => new(kind, kind.ToString(), passed, passed ? 0 : 1, passed ? "OK" : "1 account isn't reconciled", []);
        var checklist = new PeriodCloseChecklistDto(period, "FY27 P1", new(2026, 10, 1), new(2026, 10, 31), false,
            [Check(CloseCheckKind.BankReconciled, false), Check(CloseCheckKind.RevenueRecognized, false), Check(CloseCheckKind.TimesheetsApproved, true)]);

        var items = PeriodCloseTodoProvider.For(checklist, Context()).ToList();

        Assert.Equal(["Reconcile the bank accounts for FY27 P1", "Post the revenue run for FY27 P1"], items.Select(i => i.Title));
        Assert.Equal(["cash/reconciliations", "accounting/revenue"], items.Select(i => i.Link));
        Assert.All(items, i => Assert.Equal(new DateOnly(2026, 11, 10), i.Due));
        Assert.Contains("2 close steps still open for FY27 P1", items[0].Reason);

        var ready = PeriodCloseTodoProvider.For(checklist with { Checks = [Check(CloseCheckKind.BankReconciled, true)] }, Context()).Single();
        Assert.Equal("Close FY27 P1", ready.Title);
        Assert.Empty(PeriodCloseTodoProvider.For(checklist with { IsClosed = true }, Context()));
    }

    [Theory]
    [InlineData(PayrollRunState.Draft, "Export hours for payroll (Oct 5–18)")]
    [InlineData(PayrollRunState.Exported, "Import the payroll register (Oct 5–18)")]
    [InlineData(PayrollRunState.RegisterImported, "Post payroll for Oct 5–18")]
    [InlineData(PayrollRunState.Posted, null)]
    [InlineData(PayrollRunState.Voided, null)]
    public void Payroll_runs_waiting_on_finance(PayrollRunState state, string? title)
    {
        var run = new PayrollRunDto(Guid.NewGuid(), Guid.NewGuid(), "BW", "paylocity", Guid.NewGuid(), new(2026, 10, 5), new(2026, 10, 18), new(2026, 10, 23), state,
            Finance, DateTime.UtcNow, null, null, null, null, null, 12_000m, null, null, null, null);

        Assert.Equal(title, PayrollTodoProvider.For(run, Context(roles: OrgRoles.Finance))?.Title);
    }

    [Fact]
    public async Task Finance_providers_ask_nothing_of_other_people()
    {
        var (payroll, fake) = Fake<IPayrollRunService>.Create();
        var services = new ServiceCollection().AddSingleton(payroll).BuildServiceProvider();

        Assert.Empty(await new PayrollTodoProvider().GetAsync(Context(services, OrgRoles.Timekeeper), default));
        Assert.Empty(await new PeriodCloseTodoProvider().GetAsync(Context(services), default));
        Assert.Empty(fake.Calls);
    }
}
