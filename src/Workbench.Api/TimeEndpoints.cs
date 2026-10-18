using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Time;

namespace Workbench.Api;

internal static class TimeEndpoints
{
    public static void MapTimeEndpoints(this RouteGroupBuilder v1)
    {
        var time = v1.MapGroup("/time").WithTags("Time").AddEndpointFilter(MapForbidden);

        time.MapGet("/timesheets/mine", (ITimesheetService s, CancellationToken ct) => s.ListMyTimesheetsAsync(ct));
        time.MapPost("/timesheets/mine", (DateOnly date, ITimesheetService s, CancellationToken ct) => s.GetOrCreateMyTimesheetAsync(date, ct));
        time.MapGet("/timesheets/{id:guid}", (Guid id, ITimesheetService s, CancellationToken ct) => s.GetAsync(id, ct));
        time.MapPut("/timesheets/{id:guid}/entries", (Guid id, SaveTimeEntriesRequest r, ITimesheetService s, CancellationToken ct) => s.SaveEntriesAsync(id, r, ct));
        time.MapPost("/timesheets/{id:guid}/submit", (Guid id, SubmitTimesheetRequest r, ITimesheetService s, CancellationToken ct) => s.SubmitAsync(id, r, ct));
        time.MapPost("/timesheets/{id:guid}/reopen", (Guid id, ReasonRequest r, ITimesheetService s, CancellationToken ct) => s.ReopenAsync(id, r.Reason, ct));

        time.MapGet("/approvals/is-approver", (ITimesheetApprovalService s, CancellationToken ct) => s.IsApproverAsync(ct));
        time.MapGet("/approvals/pending", (ITimesheetApprovalService s, CancellationToken ct) => s.ListPendingAsync(ct));
        time.MapPost("/approvals/{id:guid}/approve", (Guid id, ITimesheetApprovalService s, CancellationToken ct) => s.ApproveAsync(id, ct));
        time.MapPost("/approvals/{id:guid}/reject", (Guid id, ReasonRequest r, ITimesheetApprovalService s, CancellationToken ct) => s.RejectAsync(id, r.Reason, ct));
        time.MapGet("/team", (DateOnly date, ITimesheetApprovalService s, CancellationToken ct) => s.GetTeamStatusAsync(date, ct));

        // PermissionCheckingTimeReportService limits rows to who the caller may see; the labor CSV is Finance only.
        time.MapGet("/reports/hours", (DateOnly from, DateOnly to, bool? approvedOnly, ITimeReportService s, CancellationToken ct) =>
            s.GetHoursByChargeCodeAsync(from, to, approvedOnly ?? true, ct));
        time.MapGet("/reports/daily-entry", (DateOnly from, DateOnly to, ITimeReportService s, CancellationToken ct) => s.GetDailyEntryComplianceAsync(from, to, ct));
        time.MapGet("/reports/labor-distribution.csv", async (DateOnly from, DateOnly to, ITimeReportService s, CancellationToken ct) =>
            Results.Text(await s.ExportLaborDistributionCsvAsync(from, to, ct), "text/csv"));
    }

    /// <summary>Permission failures (e.g. a supervisor editing hours, self-approval) become 403.</summary>
    private static async ValueTask<object?> MapForbidden(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
