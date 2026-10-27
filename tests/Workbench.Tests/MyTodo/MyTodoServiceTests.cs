using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Leave;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.MyTodo.MyTodoKit;

namespace Workbench.Tests.MyTodo;

public sealed class MyTodoServiceTests
{
    private static IReadOnlyList<string> Keys(MyTodoDto todo, string prefix) => todo.Items.Where(i => i.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(i => i.Key).ToList();

    [Fact]
    public async Task Timesheet_not_started_is_due_at_period_end_and_the_week_strip_shows_zero_of_standard()
    {
        await using var kit = await MyTodoKit.CreateAsync();

        var todo = await kit.GetAsync(Editor);

        var item = Assert.Single(todo.Items, i => i.Category == TodoCategories.Timesheet);
        Assert.Equal("Start your timesheet", item.Title);
        Assert.Equal(new DateOnly(2026, 10, 25), item.Due);
        Assert.Equal(TodoUrgency.ThisWeek, item.Urgency);
        Assert.Equal("time?date=2026-10-21", item.Link);
        Assert.Equal("Alex", todo.FirstName);
        Assert.Equal((0m, 40m, (TimesheetState?)null), (todo.Week!.HoursEntered, todo.Week.StandardHours, todo.Week.Status));
        Assert.Equal((Monday, new DateOnly(2026, 10, 25)), (todo.Week.PeriodStart, todo.Week.PeriodEnd));
    }

    [Fact]
    public async Task Draft_timesheet_lists_past_days_without_hours_then_the_submit_step()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        await kit.TimesheetAsync(kit.Fx.AnalystId, Monday, TimesheetStatus.Draft, null, (Monday, 8m));

        var todo = await kit.GetAsync(Editor);

        var missing = Assert.Single(todo.Items, i => i.Key.StartsWith("timesheet:missing:", StringComparison.Ordinal));
        Assert.Equal("Enter your hours for Tuesday", missing.Title); // Monday has hours; today (Wednesday) is still being worked
        Assert.Equal(TodoUrgency.Overdue, missing.Urgency);
        Assert.Equal("time?date=2026-10-20", missing.Link);
        var submit = Assert.Single(todo.Items, i => i.Key.StartsWith("timesheet:submit:", StringComparison.Ordinal));
        Assert.Contains("8 of 40 hours", submit.Reason);
        Assert.Equal(8m, todo.Week!.HoursEntered);
        Assert.Equal(TimesheetState.Draft, todo.Week.Status);
        Assert.True(todo.Items.ToList().IndexOf(missing) < todo.Items.ToList().IndexOf(submit));
    }

    [Fact]
    public async Task Holidays_are_not_missing_days()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        await kit.TimesheetAsync(kit.Fx.AnalystId, Monday, TimesheetStatus.Draft, null, (Monday, 8m));
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<Workbench.Domain.Leave.Holiday>().Add(new Workbench.Domain.Leave.Holiday { Date = Monday.AddDays(1), Name = "Founders' day" });
            await db.SaveChangesAsync();
        }

        var todo = await kit.GetAsync(Editor);

        Assert.Empty(Keys(todo, "timesheet:missing:"));
    }

    [Fact]
    public async Task Rejected_returned_and_late_timesheets_need_fixing()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        await kit.TimesheetAsync(kit.Fx.AnalystId, Monday, TimesheetStatus.Rejected, "Returned for changes: move Monday to 1001.02", (Monday, 8m));
        await kit.TimesheetAsync(kit.Fx.ViewerId, Monday, TimesheetStatus.Rejected, "Wrong project", (Monday, 8m));
        await kit.TimesheetAsync(kit.Fx.ViewerId, Monday.AddDays(-14), TimesheetStatus.Draft);

        var analyst = await kit.GetAsync(Editor);
        var returned = Assert.Single(analyst.Items, i => i.Category == TodoCategories.Timesheet);
        Assert.StartsWith("Make the changes your approver asked for", returned.Title);
        Assert.Equal("Revision requested: \"move Monday to 1001.02\"", returned.Reason);
        Assert.Equal(TodoUrgency.Today, returned.Urgency);

        var viewer = await kit.GetAsync(Viewer);
        var timesheets = viewer.Items.Where(i => i.Category == TodoCategories.Timesheet).ToList();
        Assert.Equal(["Submit your timesheet for Oct 5–11", "Fix and resubmit your timesheet (Oct 19–25)"], timesheets.Select(i => i.Title));
        Assert.Equal("Rejected: \"Wrong project\"", timesheets[1].Reason);
        Assert.Equal(TodoUrgency.Overdue, timesheets[0].Urgency);
    }

    [Fact]
    public async Task Submitted_and_approved_timesheets_need_nothing()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        await kit.TimesheetAsync(kit.Fx.AnalystId, Monday, TimesheetStatus.Submitted, null, (Monday, 8m));

        var todo = await kit.GetAsync(Editor);

        Assert.DoesNotContain(todo.Items, i => i.Category == TodoCategories.Timesheet);
        Assert.Equal(TimesheetState.Submitted, todo.Week!.Status);
    }

    [Fact]
    public async Task Draft_expense_report_with_missing_receipts_and_a_rejected_report()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        Guid draftId, rejectedId;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var draft = new ExpenseReport { EmployeeId = kit.Fx.AnalystId, Title = "DC program review", TripStart = new DateOnly(2026, 10, 5), TripEnd = new DateOnly(2026, 10, 7), CreatedBy = Editor };
            var rejected = new ExpenseReport { EmployeeId = kit.Fx.AnalystId, Title = "Training", Status = OdcStatus.Rejected, RejectionReason = "Missing hotel folio", CreatedBy = Editor };
            var other = new ExpenseReport { EmployeeId = kit.Fx.ViewerId, Title = "Val's trip", CreatedBy = Viewer };
            db.Set<ExpenseReport>().AddRange(draft, rejected, other);
            OdcEntry Line(Guid report, Guid employee, decimal amount, string text) => new()
            {
                ProjectId = kit.Fx.Project1, WbsElementId = kit.Fx.Wbs1, Type = OdcType.Travel, IncurredOn = new DateOnly(2026, 10, 5), Amount = amount,
                Description = text, EmployeeId = employee, ExpenseReportId = report, SubmittedBy = Editor,
            };
            db.Set<OdcEntry>().AddRange(Line(draft.Id, kit.Fx.AnalystId, 486.20m, "Airfare"), Line(draft.Id, kit.Fx.AnalystId, 20m, "Taxi"),
                Line(other.Id, kit.Fx.ViewerId, 300m, "Hotel"));
            await db.SaveChangesAsync();
            (draftId, rejectedId) = (draft.Id, rejected.Id);
        }

        var todo = await kit.GetAsync(Editor);

        var draftItem = Assert.Single(todo.Items, i => i.Key == $"expense:draft:{draftId}");
        Assert.Equal("Add receipts and submit \"DC program review\"", draftItem.Title);
        Assert.Contains("Oct 5–7", draftItem.Reason);
        Assert.Contains("1 receipt missing", draftItem.Reason); // the $20 taxi is under the $75 threshold
        Assert.Equal(TodoUrgency.ThisWeek, draftItem.Urgency); // the trip is over
        Assert.Equal($"expenses/{draftId}", draftItem.Link);
        var rejectedItem = Assert.Single(todo.Items, i => i.Key == $"expense:rejected:{rejectedId}");
        Assert.Contains("Missing hotel folio", rejectedItem.Reason);
        Assert.DoesNotContain(todo.Items, i => i.Title.Contains("Val's trip"));
    }

    [Fact]
    public async Task Leave_waiting_on_the_approver_has_an_inline_approve_and_the_requester_sees_it_pending()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        var request = await kit.As(Editor).Get<ILeaveRequestService>().CreateAsync(new CreateLeaveRequest(kit.Pto.Id, new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30), Comment: "Family visit"));

        var pm = await kit.GetAsync(Owner);
        var approve = Assert.Single(pm.Items, i => i.Key == $"leave:approve:{request.Id}");
        Assert.Equal("Approve Alex Rivera's leave", approve.Title);
        Assert.Equal(new DateOnly(2026, 10, 28), approve.Due); // decide before it starts
        Assert.Equal(new TodoInlineAction(TodoInlineActionKind.ApproveLeave, "Approve", request.Id), approve.Action);
        // It's in the approvals inbox preview too, but not counted again in the approvals to-do.
        Assert.Contains(pm.Approvals!.Top, a => a.DocumentId == request.Id && a.DocumentType == ApprovalDocumentTypes.LeaveRequest);
        Assert.DoesNotContain(pm.Items, i => i.Key == "approvals:inbox" && i.Reason.Contains("leave", StringComparison.OrdinalIgnoreCase));

        var analyst = await kit.GetAsync(Editor);
        Assert.Contains(analyst.Items, i => i.Key == $"leave:pending:{request.Id}" && i.Urgency == TodoUrgency.Later);
        Assert.DoesNotContain(analyst.Items, i => i.Key.StartsWith("leave:approve:", StringComparison.Ordinal));

        var viewer = await kit.GetAsync(Viewer);
        Assert.DoesNotContain(viewer.Items, i => i.Category == TodoCategories.Leave);

        await kit.As(Owner).Get<ILeaveRequestService>().ApproveAsync(request.Id, new DecideLeaveRequest());
        Assert.DoesNotContain((await kit.GetAsync(Owner)).Items, i => i.Category == TodoCategories.Leave); // done items disappear
    }

    [Fact]
    public async Task Approvals_waiting_on_the_supervisor_come_from_the_inbox_and_nobody_else_sees_them()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        var sheetId = await kit.TimesheetAsync(kit.Fx.AnalystId, Monday, TimesheetStatus.Draft, null, (Monday, 8m), (Monday.AddDays(1), 8m));
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = kit.Fx.Wbs1, EmployeeId = kit.Fx.AnalystId });
            await db.SaveChangesAsync();
        }

        await kit.As(Editor).Get<ITimesheetService>().SubmitAsync(sheetId, new SubmitTimesheetRequest(true));

        var pm = await kit.GetAsync(Owner);
        var item = Assert.Single(pm.Items, i => i.Category == TodoCategories.Approval);
        Assert.StartsWith("Review ", item.Title);
        Assert.Contains("timesheet", item.Reason);
        var preview = Assert.IsType<ApprovalsPreviewDto>(pm.Approvals);
        Assert.Equal(1, preview.Count);
        var top = Assert.Single(preview.Top);
        Assert.Equal(sheetId, top.DocumentId);
        Assert.True(ApprovalsPreviewDto.CanQuickApprove(top));

        foreach (var other in new[] { Editor, Viewer, Outsider })
        {
            var todo = await kit.GetAsync(other);
            Assert.Null(todo.Approvals);
            Assert.DoesNotContain(todo.Items, i => i.Category == TodoCategories.Approval);
        }

        await kit.As(Owner).Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.Timesheet, sheetId, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));
        var after = await kit.GetAsync(Owner);
        Assert.Null(after.Approvals);
        Assert.DoesNotContain(after.Items, i => i.Category == TodoCategories.Approval);
    }

    [Fact]
    public async Task Nobody_sees_another_persons_own_items()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        await kit.TimesheetAsync(kit.Fx.AnalystId, Monday, TimesheetStatus.Rejected, "Wrong project", (Monday, 8m));
        await kit.As(Editor).Get<ILeaveRequestService>().CreateAsync(new CreateLeaveRequest(kit.Pto.Id, new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 29)));

        foreach (var other in new[] { Viewer, Outsider, Admin, Finance })
        {
            var todo = await kit.GetAsync(other);
            Assert.DoesNotContain(todo.Items, i => i.Key.StartsWith("timesheet:rejected:", StringComparison.Ordinal));
            Assert.DoesNotContain(todo.Items, i => i.Key.StartsWith("leave:pending:", StringComparison.Ordinal));
            Assert.DoesNotContain(todo.Items, i => i.Title.Contains("Alex"));
        }
    }

    [Fact]
    public async Task Ceilings_and_funding_are_asked_only_for_projects_the_user_manages()
    {
        var ceilings = new FakeCeilings();
        var funding = new FakeFunding();
        await using var kit = await MyTodoKit.CreateAsync(services =>
        {
            services.AddScoped<ICeilingService>(_ => ceilings);
            services.AddScoped<IFundingService>(_ => funding);
        });

        var pm = await kit.GetAsync(Owner);
        var ceiling = Assert.Single(pm.Items, i => i.Key.StartsWith("ceiling:", StringComparison.Ordinal));
        Assert.Equal("1001 hours is at 92% of its hours ceiling", ceiling.Title);
        Assert.Equal(TodoUrgency.Today, ceiling.Urgency); // critical
        var clin = Assert.Single(pm.Items, i => i.Key.StartsWith("funding:", StringComparison.Ordinal));
        Assert.Equal("CLIN 0001 on C1 has used 80% of its funding", clin.Title);
        Assert.Contains("projected to run out Nov 30", clin.Reason);
        Assert.Null(clin.Due); // the run-out date is in the reason, not shown as a due date
        Assert.Equal(TodoUrgency.ThisWeek, clin.Urgency);
        Assert.Equal([kit.Fx.Project1], ceilings.Asked.Distinct());
        Assert.Equal([kit.Fx.Project1], funding.Asked.Distinct());

        ceilings.Asked.Clear();
        foreach (var other in new[] { Editor, Viewer, Admin, Finance })
        {
            var todo = await kit.GetAsync(other);
            Assert.DoesNotContain(todo.Items, i => i.Category == TodoCategories.Project);
        }

        Assert.Empty(ceilings.Asked); // other people's projects aren't even read
    }

    [Fact]
    public async Task Period_close_steps_are_for_finance_once_the_period_has_ended()
    {
        await using var kit = await MyTodoKit.CreateAsync();
        Assert.DoesNotContain((await kit.GetAsync(Finance)).Items, i => i.Key.StartsWith("close:", StringComparison.Ordinal)); // October hasn't ended

        kit.Clock.UtcNow = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var finance = await kit.GetAsync(Finance);
        var close = finance.Items.Where(i => i.Key.StartsWith("close:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(close);
        Assert.All(close, i => Assert.Equal(new DateOnly(2026, 11, 10), i.Due));
        Assert.All(close, i => Assert.Equal(TodoCategories.Finance, i.Category));

        foreach (var other in new[] { Owner, Editor, Viewer })
        {
            Assert.DoesNotContain((await kit.GetAsync(other)).Items, i => i.Category == TodoCategories.Finance);
        }
    }

    [Fact]
    public async Task A_failing_provider_doesnt_break_the_page()
    {
        await using var kit = await MyTodoKit.CreateAsync(services => services.AddScoped<IMyTodoProvider, ThrowingProvider>());

        var todo = await kit.GetAsync(Editor);

        Assert.Equal(["Broken source"], todo.Unavailable);
        Assert.Contains(todo.Items, i => i.Title == "Start your timesheet");
        Assert.NotNull(todo.Week);
    }

    [Fact]
    public async Task Providers_are_merged_and_sorted_by_urgency_then_due_date()
    {
        await using var kit = await MyTodoKit.CreateAsync(services => services.AddScoped<IMyTodoProvider, FixedProvider>());

        var todo = await kit.GetAsync(Editor);

        var urgencies = todo.Items.Select(i => i.Urgency).ToList();
        Assert.Equal(urgencies.Order().ToList(), urgencies);
        Assert.Equal(["fixed:overdue", "fixed:today", "fixed:week-early", "timesheet:start:2026-10-19", "fixed:later"],
            todo.Items.Select(i => i.Key).Where(k => k.StartsWith("fixed:", StringComparison.Ordinal) || k.StartsWith("timesheet:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_user_without_an_employee_record_gets_an_empty_list()
    {
        await using var kit = await MyTodoKit.CreateAsync();

        var todo = await kit.GetAsync("nobody@example.com");

        Assert.Empty(todo.Items);
        Assert.Null(todo.Week);
        Assert.Empty(todo.Unavailable);
    }

    private sealed class ThrowingProvider : IMyTodoProvider
    {
        public string Name => "Broken source";

        public Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class FixedProvider : IMyTodoProvider
    {
        public string Name => "Fixed";

        public Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
        {
            MyTodoItem Item(string key, DateOnly? due) => new($"fixed:{key}", TodoCategories.Sheet, key, "", context.UrgencyFor(due), due, "", "");
            return Task.FromResult<IReadOnlyList<MyTodoItem>>([Item("later", Today.AddDays(30)), Item("week-early", Today.AddDays(1)), Item("today", Today), Item("overdue", Today.AddDays(-3))]);
        }
    }

    private sealed class FakeCeilings : ICeilingService
    {
        public List<Guid> Asked { get; } = [];

        public Task<ProjectCeilingStatusDto> GetProjectStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            Asked.Add(projectId);
            var ceiling = new CeilingDto(Guid.NewGuid(), CeilingScope.Project, null, null, null, null, projectId, "1001", null, null, null, null, 1000m, null, null,
                CeilingMode.Warn, [80, 90], false, null, true, "1001 hours");
            var status = new CeilingStatusDto(ceiling, [new CeilingMeasureDto(CeilingMetric.Hours, 1000m, 1000m, 920m, 80m, 92m, CeilingAlertLevel.Critical)], [], CeilingAlertLevel.Critical, false);
            return Task.FromResult(new ProjectCeilingStatusDto(projectId, "1001", null, [status], false));
        }

        public Task<ContractCeilingStatusDto> GetContractStatusAsync(Guid contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CeilingStatusDto> GetAsync(Guid ceilingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CeilingDto> CreateAsync(SaveCeilingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CeilingDto> UpdateAsync(Guid ceilingId, SaveCeilingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CeilingDto> DeactivateAsync(Guid ceilingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CeilingOverrideDto>> ListOverridesAsync(Guid ceilingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CeilingOverrideDto> GrantOverrideAsync(Guid ceilingId, GrantCeilingOverrideRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CeilingOverrideDto> RevokeOverrideAsync(Guid overrideId, CeilingReasonRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RateCeilingDto>> ListRateCeilingsAsync(Guid contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RateCeilingDto> CreateRateCeilingAsync(Guid contractId, SaveRateCeilingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RateCeilingDto> DeactivateRateCeilingAsync(Guid rateCeilingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CheckAlertsAsync(Guid contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeFunding : IFundingService
    {
        public List<Guid> Asked { get; } = [];

        public Task<ContractFundingStatusDto?> GetProjectFundingStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            Asked.Add(projectId);
            var clin = new ClinFundingStatusDto(Guid.NewGuid(), "0001", "Labor", false, true, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), 500_000m, 100_000m,
                80_000m, 0m, 20_000m, 0.8m, 0.8m, 500m, new DateOnly(2026, 11, 30), FundingLevel.Warning);
            var ok = clin with { ClinId = Guid.NewGuid(), Number = "0002", Level = FundingLevel.Ok };
            return Task.FromResult<ContractFundingStatusDto?>(new ContractFundingStatusDto(Guid.NewGuid(), "C1", Today, false, 0.75m, 0.9m, [clin, ok], []));
        }

        public Task<IReadOnlyList<FundingActionDto>> ListFundingHistoryAsync(Guid contractId, Guid? clinId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContractFundingStatusDto> GetFundingStatusAsync(Guid contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FundingNoticeDto>> CheckNoticesAsync(Guid contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
