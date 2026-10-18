using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;
using Workbench.Ui.Mobile;
using static Workbench.Tests.Mobile.MobileTestData;

namespace Workbench.Tests.Mobile;

/// <summary>Expenses, approvals and leave on the phone pages, against fakes of the existing services.</summary>
public sealed class MobileWorkflowPageTests
{
    private static Fake<T> Add<T>(BunitContext ctx) where T : class
    {
        var (service, fake) = Fake<T>.Create();
        ctx.Services.AddSingleton(service);
        return fake;
    }

    // ---- Expenses ----

    private static (Fake<IExpenseReportService> Reports, Fake<IOdcService> Odcs) ExpenseServices(BunitContext ctx, ExpenseReportDetailDto detail)
    {
        var reports = Add<IExpenseReportService>(ctx).Returns(nameof(IExpenseReportService.GetReportAsync), detail);
        var odcs = Add<IOdcService>(ctx)
            .Returns(nameof(IOdcService.GetPolicyAsync), new ExpensePolicyDto(75m))
            .On(nameof(IOdcService.GetReceiptUrl), a => $"api/v1/odcs/{a[0]}/receipt");
        Add<IProjectService>(ctx).Returns(nameof(IProjectService.ListChargeCodesAsync), new List<ChargeCodeDto>
        {
            new(Direct, "1001.01", "Requirements", Guid.NewGuid(), "1001", "Project", ChargeCodeKind.Direct, LaborCategory),
            new(Pto, "LV.PTO", "Paid time off", Guid.NewGuid(), "LV", "Leave", ChargeCodeKind.Leave, LaborCategory),
        });
        Add<IPerDiemRateService>(ctx);
        return (reports, odcs);
    }

    [Fact]
    public async Task Expense_report_adds_a_line_with_a_camera_receipt()
    {
        await using var ctx = Context();
        var report = Report();
        var (reports, _) = ExpenseServices(ctx, new ExpenseReportDetailDto(report, []));
        Guid? lineId = null;
        reports.On(nameof(IExpenseReportService.AddLineAsync), a =>
        {
            var line = Line(report, ((SaveExpenseLineRequest)a[1]!).Amount);
            lineId = line.Id;
            return line;
        });
        byte[]? uploaded = null;
        reports.On(nameof(IExpenseReportService.AttachReceiptAsync), a =>
        {
            using var copy = new MemoryStream();
            ((Stream)a[3]!).CopyTo(copy);
            uploaded = copy.ToArray();
            return Line(report, 92.5m, receipt: true);
        });
        Providers(ctx);

        var page = ctx.Render<ExpenseReportPage>(p => p.Add(x => x.ReportId, report.Id));
        page.WaitForAssertion(() => page.Find("button[data-testid='add']"));
        page.Find("button[data-testid='add']").Click();

        // Leave codes take hours, not costs; the only cost code is picked for you.
        page.WaitForAssertion(() => Assert.DoesNotContain("LV.PTO", page.Find("[data-testid='line-code']").InnerHtml));
        page.Find("[data-testid='line-amount']").Input("92.50");
        page.Find("[data-testid='line-description']").Input("Hotel taxi");
        var camera = page.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .Single(c => c.Instance.AdditionalAttributes?["data-testid"] is "new-receipt-camera");
        Assert.Equal("environment", camera.Instance.AdditionalAttributes!["capture"]);
        Assert.Equal("image/*", camera.Instance.AdditionalAttributes!["accept"]);
        camera.UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3, 4], "photo.jpg", contentType: "image/jpeg"));
        page.WaitForAssertion(() => Assert.Contains("photo.jpg", page.Find("[data-testid='picked']").TextContent));

        page.Find("button[data-testid='add-line']").Click();

        page.WaitForAssertion(() => Assert.Equal([1, 2, 3, 4], uploaded));
        var request = (SaveExpenseLineRequest)reports.Single(nameof(IExpenseReportService.AddLineAsync))[1]!;
        Assert.Equal(92.5m, request.Amount);
        Assert.Equal(Direct, request.WbsElementId);
        Assert.Equal("Hotel taxi", request.Description);
        var attach = reports.Single(nameof(IExpenseReportService.AttachReceiptAsync));
        Assert.Equal(lineId, attach[1]);
        Assert.Equal("photo.jpg", attach[2]);
    }

    [Fact]
    public async Task Expense_report_submits_and_flags_missing_receipts()
    {
        await using var ctx = Context();
        var report = Report(lines: 1, total: 120m);
        var (reports, _) = ExpenseServices(ctx, new ExpenseReportDetailDto(report, [Line(report, 120m)]));
        reports.Returns(nameof(IExpenseReportService.SubmitAsync), report with { Status = OdcApprovalState.Submitted });
        Providers(ctx);

        var page = ctx.Render<ExpenseReportPage>(p => p.Add(x => x.ReportId, report.Id));

        page.WaitForAssertion(() => page.Find("[data-testid='receipt-required']"));
        page.Find("button[data-testid='submit']").Click();
        page.WaitForAssertion(() => Assert.Equal(report.Id, reports.Single(nameof(IExpenseReportService.SubmitAsync))[0]));
    }

    [Fact]
    public async Task Submitted_report_is_read_only()
    {
        await using var ctx = Context();
        var report = Report(OdcApprovalState.Submitted, 1, 20m);
        ExpenseServices(ctx, new ExpenseReportDetailDto(report, [Line(report, 20m)]));
        Providers(ctx);

        var page = ctx.Render<ExpenseReportPage>(p => p.Add(x => x.ReportId, report.Id));

        page.WaitForAssertion(() => page.Find("[data-testid='line']"));
        Assert.Empty(page.FindAll("button[data-testid='add']"));
        Assert.Empty(page.FindAll("button[data-testid='submit']"));
    }

    [Fact]
    public async Task New_expense_report_goes_to_its_page()
    {
        await using var ctx = Context();
        var created = Report();
        var reports = Add<IExpenseReportService>(ctx)
            .Returns(nameof(IExpenseReportService.ListMyReportsAsync), new List<ExpenseReportDto>())
            .Returns(nameof(IExpenseReportService.CreateReportAsync), created);
        Providers(ctx);

        var page = ctx.Render<ExpensesPage>();
        page.WaitForAssertion(() => page.Find("[data-testid='empty']"));
        page.Find("button[data-testid='new']").Click();
        page.Find("[data-testid='title']").Input("Dayton trip");
        page.Find("button[data-testid='create']").Click();

        Assert.Equal("Dayton trip", ((SaveExpenseReportRequest)reports.Single(nameof(IExpenseReportService.CreateReportAsync))[0]!).Title);
        Assert.EndsWith($"m/expenses/{created.Id}", ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    // ---- Approvals ----

    private sealed record Queues(
        Fake<ITimesheetApprovalService> Timesheets,
        Fake<IOdcApprovalService> Expenses,
        Fake<ILeaveRequestService> Leave,
        Fake<IRequisitionService> Requisitions,
        Fake<IApBillService> Bills);

    private static Queues ApprovalServices(BunitContext ctx, IReadOnlyList<TimesheetDto>? timesheets = null, IReadOnlyList<ExpenseReportDetailDto>? expenses = null,
        IReadOnlyList<LeaveRequestDto>? leave = null, IReadOnlyList<RequisitionSummaryDto>? requisitions = null, IReadOnlyList<BillApprovalItemDto>? bills = null)
    {
        Add<ITimesheetService>(ctx);
        Add<IOdcService>(ctx).On(nameof(IOdcService.GetReceiptUrl), a => $"api/v1/odcs/{a[0]}/receipt");
        return new Queues(
            Add<ITimesheetApprovalService>(ctx).Returns(nameof(ITimesheetApprovalService.ListPendingAsync), timesheets ?? []),
            Add<IOdcApprovalService>(ctx).Returns(nameof(IOdcApprovalService.GetMyQueueAsync), new OdcApprovalQueueDto(expenses ?? [], [])),
            Add<ILeaveRequestService>(ctx).Returns(nameof(ILeaveRequestService.ListPendingApprovalsAsync), leave ?? []),
            Add<IRequisitionService>(ctx).Returns(nameof(IRequisitionService.ListApprovalQueueAsync), requisitions ?? []),
            Add<IApBillService>(ctx).Returns(nameof(IApBillService.ListApprovalQueueAsync), bills ?? []));
    }

    [Fact]
    public async Task Approvals_lists_everything_routed_to_me()
    {
        await using var ctx = Context();
        var report = Report(OdcApprovalState.Submitted, 1, 40m);
        ApprovalServices(ctx, [Sheet(TimesheetState.Submitted, total: 40m)], [new ExpenseReportDetailDto(report, [Line(report, 40m, receipt: true)])],
            [LeaveRequest()], [Requisition()], [Bill()]);
        Providers(ctx);

        var page = ctx.Render<ApprovalsPage>();

        page.WaitForAssertion(() => page.Find("[data-testid='timesheet-item']"));
        Assert.Single(page.FindAll("[data-testid='expense-item']"));
        Assert.Single(page.FindAll("[data-testid='leave-item']"));
        Assert.Single(page.FindAll("[data-testid='requisition-item']"));
        Assert.Single(page.FindAll("[data-testid='bill-item']"));
        Assert.Contains("receipt", page.Find("[data-testid='expense-item']").InnerHtml);
    }

    [Fact]
    public async Task Approving_a_timesheet_calls_the_approval_service_and_refreshes()
    {
        await using var ctx = Context();
        var sheet = Sheet(TimesheetState.Submitted, total: 40m);
        var queues = ApprovalServices(ctx, [sheet]);
        var pending = new List<TimesheetDto> { sheet };
        queues.Timesheets.On(nameof(ITimesheetApprovalService.ListPendingAsync), _ => pending.ToList());
        queues.Timesheets.On(nameof(ITimesheetApprovalService.ApproveAsync), _ =>
        {
            pending.Clear();
            return sheet with { Status = TimesheetState.Approved };
        });
        Providers(ctx);

        var page = ctx.Render<ApprovalsPage>();
        page.WaitForAssertion(() => page.Find($"button[data-testid='approve-timesheet-{sheet.Id}']"));
        page.Find($"button[data-testid='approve-timesheet-{sheet.Id}']").Click();

        page.WaitForAssertion(() => page.Find("[data-testid='empty']"));
        Assert.Equal(sheet.Id, queues.Timesheets.Single(nameof(ITimesheetApprovalService.ApproveAsync))[0]);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason()
    {
        await using var ctx = Context();
        var request = LeaveRequest();
        var queues = ApprovalServices(ctx, leave: [request]);
        queues.Leave.Returns(nameof(ILeaveRequestService.RejectAsync), request with { Status = LeaveRequestState.Rejected });
        var dialogs = Providers(ctx);

        var page = ctx.Render<ApprovalsPage>();
        page.WaitForAssertion(() => page.Find($"button[data-testid='reject-leave-{request.Id}']"));
        page.Find($"button[data-testid='reject-leave-{request.Id}']").Click();

        dialogs.WaitForAssertion(() => dialogs.Find("textarea"));
        Assert.True(dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Reject").HasAttribute("disabled"));
        dialogs.Find("textarea").Input("Team is short that week");
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Reject").Click();

        page.WaitForAssertion(() =>
            Assert.Equal("Team is short that week", ((DecideLeaveRequest)queues.Leave.Single(nameof(ILeaveRequestService.RejectAsync))[1]!).Comment));
    }

    [Fact]
    public async Task Approvals_skips_queues_the_user_cannot_read()
    {
        await using var ctx = Context();
        var queues = ApprovalServices(ctx, requisitions: [Requisition()]);
        queues.Bills.On(nameof(IApBillService.ListApprovalQueueAsync), _ => throw new UnauthorizedAccessException("Finance only"));
        queues.Timesheets.On(nameof(ITimesheetApprovalService.ListPendingAsync), _ => throw new Workbench.Contracts.WorkbenchForbiddenException("No"));
        Providers(ctx);

        var page = ctx.Render<ApprovalsPage>();

        page.WaitForAssertion(() => page.Find("[data-testid='requisition-item']"));
        Assert.Empty(page.FindAll("[data-testid='bill-item']"));
        Assert.Empty(page.FindAll("[data-testid='timesheet-item']"));
    }

    [Fact]
    public async Task Bottom_nav_counts_pending_approvals()
    {
        await using var ctx = Context();
        ApprovalServices(ctx, leave: [LeaveRequest()], requisitions: [Requisition(), Requisition()]);
        Providers(ctx);

        var nav = ctx.Render<MobileBottomNav>();

        nav.WaitForAssertion(() => Assert.Contains(">3<", nav.Find("[data-testid='tab-approvals']").InnerHtml));
        Assert.Equal(5, nav.FindAll("a.wb-m-tab").Count);
    }

    // ---- Leave ----

    [Fact]
    public async Task Leave_shows_balances_and_sends_a_request()
    {
        await using var ctx = Context();
        var policy = new LeavePolicyDto(Guid.NewGuid(), "PTO", "Paid time off", LeaveTypeKind.Pto, Pto, "LV.PTO", AccrualMethodKind.PerPayPeriod, [],
            null, null, 0, false, true);
        var holiday = policy with { Id = Guid.NewGuid(), Code = "HOL", Name = "Holiday", Type = LeaveTypeKind.Holiday };
        Add<ILeaveBalanceService>(ctx).Returns(nameof(ILeaveBalanceService.GetMyBalancesAsync), new List<LeaveBalanceDto>
        {
            new(Guid.NewGuid(), "Dana Lee", policy.Id, "PTO", "Paid time off", LeaveTypeKind.Pto, "LV.PTO", 40m, 40m, 0m, 8m, 32m, false),
        });
        var requests = Add<ILeaveRequestService>(ctx)
            .Returns(nameof(ILeaveRequestService.ListMineAsync), new List<LeaveRequestDto>())
            .Returns(nameof(ILeaveRequestService.CreateAsync), LeaveRequest());
        Add<ILeavePolicyService>(ctx).Returns(nameof(ILeavePolicyService.ListMineAsync), new List<LeavePolicyDto> { policy, holiday });
        Providers(ctx);

        var page = ctx.Render<LeavePage>();

        page.WaitForAssertion(() => Assert.Equal("32 h", page.Find("[data-testid='available-PTO']").TextContent.Trim()));
        Assert.DoesNotContain("Holiday", page.Find("[data-testid='request-policy']").InnerHtml);
        Assert.True(page.Find("button[data-testid='request-submit']").HasAttribute("disabled"));
        page.Find("[data-testid='request-policy']").Change(policy.Id.ToString());
        page.Find("[data-testid='request-start']").Change("2026-10-26");
        page.Find("[data-testid='request-end']").Change("2026-10-27");
        page.Find("button[data-testid='request-submit']").Click();

        var created = (CreateLeaveRequest)requests.Single(nameof(ILeaveRequestService.CreateAsync))[0]!;
        Assert.Equal(policy.Id, created.PolicyId);
        Assert.Equal(new DateOnly(2026, 10, 26), created.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 27), created.EndDate);
        Assert.Null(created.HoursPerDay);
    }

    // ---- Small pieces ----

    [Fact]
    public void Mobile_pages_use_the_phone_layout_and_the_layout_has_none_of_its_own()
    {
        // A layout that names itself as its layout nests forever (the page never renders).
        Assert.Null(typeof(MobileLayout).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.LayoutAttribute), true).SingleOrDefault());
        var pages = typeof(MobileLayout).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(MobileLayout).Namespace && t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false).Length > 0)
            .ToList();
        Assert.Equal(6, pages.Count);
        Assert.All(pages, p => Assert.Equal(typeof(MobileLayout),
            ((Microsoft.AspNetCore.Components.LayoutAttribute)p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.LayoutAttribute), true).Single()).LayoutType));
    }

    [Theory]
    [InlineData("7.5", 7.5)]
    [InlineData("7,25", 7.25)]
    [InlineData("", 0.0)]
    [InlineData("25", null)]
    [InlineData("abc", null)]
    public void Hours_typed_on_a_phone_keyboard(string text, double? expected) =>
        Assert.Equal(expected is { } e ? (decimal)e : null, MobileFormat.ParseHours(text));

    [Theory]
    [InlineData("IMG_0001.JPG", "image/jpeg", "IMG_0001.JPG")]
    [InlineData("image", "image/jpeg", ".jpg")]
    [InlineData("scan.pdf", "application/pdf", "scan.pdf")]
    [InlineData("photo.heic", "image/heic", null)]
    [InlineData("notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", null)]
    public void Receipts_are_pictures_or_pdfs(string name, string contentType, string? expected)
    {
        var result = ReceiptCapture.NameFor(name, contentType);
        if (expected is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.EndsWith(expected, result);
        }
    }
}
