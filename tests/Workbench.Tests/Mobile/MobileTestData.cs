using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;

namespace Workbench.Tests.Mobile;

/// <summary>A week of Dana's time (Mon 2026-10-19, "today" Wednesday 10-21) and a bUnit context with a dialog provider.</summary>
internal static class MobileTestData
{
    public static readonly DateOnly Monday = new(2026, 10, 19);
    public static readonly DateOnly Wednesday = Monday.AddDays(2);
    public static readonly Guid Direct = Guid.NewGuid();
    public static readonly Guid Overhead = Guid.NewGuid();
    public static readonly Guid Pto = Guid.NewGuid();
    public static readonly Guid LaborCategory = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc);

    public static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>Renders the popover and dialog providers; call after registering services (rendering locks them).</summary>
    public static IRenderedComponent<MudDialogProvider> Providers(BunitContext ctx)
    {
        ctx.Render<MudPopoverProvider>();
        return ctx.Render<MudDialogProvider>();
    }

    public static TimesheetDto Sheet(TimesheetState status = TimesheetState.Draft, DateTime? firstSubmitted = null, decimal total = 16m) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.NewGuid(), "Dana Lee", "dana@example.com", Monday, Monday.AddDays(6),
            status, 0, total, 40m, null, null, firstSubmitted, null, null, null, null, Now);

    public static TimeEntryDto Entry(Guid wbs, string code, DateOnly date, decimal hours) =>
        new(Guid.NewGuid(), wbs, code, code == "1001.01" ? "Requirements" : "Overhead", "1001", LaborCategory, "SE2", date, hours, null, Now, Now);

    public static TimesheetDetailDto Detail(TimesheetDto? sheet = null, params TimeEntryDto[] entries)
    {
        sheet ??= Sheet();
        var days = Enumerable.Range(0, 7)
            .Select(i => new TimesheetDayDto(Monday.AddDays(i), i > 2, true, true, entries.Any(e => e.WorkDate == Monday.AddDays(i)) ? Now : null, i == 0))
            .ToList();
        var all = days.Select(d => d.Date).ToList();
        ChargeCodeDto Code(Guid id, string code, string name, ChargeCodeKind kind) => new(id, code, name, Guid.NewGuid(), "1001", "Project", kind, LaborCategory);
        return new TimesheetDetailDto(sheet, entries, [], days,
        [
            new TimesheetChargeCodeDto(Code(Direct, "1001.01", "Requirements", ChargeCodeKind.Direct), all, true),
            new TimesheetChargeCodeDto(Code(Overhead, "IND.OH", "Overhead", ChargeCodeKind.Indirect), all, false),
            new TimesheetChargeCodeDto(Code(Pto, "LV.PTO", "Paid time off", ChargeCodeKind.Leave), all, false),
        ], []);
    }

    public static TimesheetLeaveDto NoLeave(Guid timesheetId) => new(timesheetId, [], []);

    public static ExpenseReportDto Report(OdcApprovalState status = OdcApprovalState.Draft, int lines = 0, decimal total = 0m) =>
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.NewGuid(), "Dana Lee", "Dayton trip", null, Monday, Monday.AddDays(2), status, lines,
            total, total, Now, null, null, null, null, null);

    public static OdcEntryDto Line(ExpenseReportDto report, decimal amount, string description = "Taxi", bool receipt = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "1001", Direct, "1001.01", "Requirements", OdcCostType.Travel, Monday, amount, description, null, null,
            report.EmployeeId, report.Id, false, null, receipt ? Guid.NewGuid() : null, receipt ? "taxi.jpg" : null, report.Status, "dana@example.com", Now,
            null, null, null, null);

    public static LeaveRequestDto LeaveRequest(LeaveRequestState status = LeaveRequestState.Pending) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Dana Lee", Guid.NewGuid(), "Paid time off", "LV.PTO", Monday.AddDays(7), Monday.AddDays(8), 16m, [],
            status, "Family visit", Now, null, null, null, []);

    public static RequisitionSummaryDto Requisition() =>
        new(Guid.NewGuid(), "REQ-0001", "analyst@example.com", "Alex Rivera", "Test equipment", RequisitionState.PendingApproval,
            RequisitionApprovalStage.ProjectManager, 1200m, Now, Now, 1);

    public static BillApprovalItemDto Bill() =>
        new(new BillSummaryDto(Guid.NewGuid(), "BILL-0001", Guid.NewGuid(), "ACME", "ACME Supply", "INV-9", Monday, Monday.AddDays(30), BillState.PendingApproval,
            500m, 0m, 0m, "ap@example.com", null), []);
}
