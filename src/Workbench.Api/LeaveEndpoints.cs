using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Leave;

namespace Workbench.Api;

internal static class LeaveEndpoints
{
    public static void MapLeaveEndpoints(this RouteGroupBuilder v1)
    {
        var l = v1.MapGroup("/leave").WithTags("Leave").AddEndpointFilter(MapLeaveErrors);

        l.MapGet("/pay-groups", (IPayGroupChoiceService s, CancellationToken ct) => s.ListAsync(ct));
        l.MapGet("/policies", (bool? includeInactive, ILeavePolicyService s, CancellationToken ct) => s.ListAsync(includeInactive ?? false, ct));
        l.MapGet("/policies/charge-codes", (ILeavePolicyService s, CancellationToken ct) => s.ListLeaveChargeCodesAsync(ct));
        l.MapGet("/policies/mine", (ILeavePolicyService s, CancellationToken ct) => s.ListMineAsync(ct));
        l.MapPost("/policies", (SaveLeavePolicyRequest r, ILeavePolicyService s, CancellationToken ct) => s.CreateAsync(r, ct));
        l.MapPut("/policies/{id:guid}", (Guid id, SaveLeavePolicyRequest r, ILeavePolicyService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));

        l.MapGet("/holidays/{year:int}", (int year, IHolidayService s, CancellationToken ct) => s.ListAsync(year, ct));
        l.MapPost("/holidays", (SaveHolidayRequest r, IHolidayService s, CancellationToken ct) => s.AddAsync(r, ct));
        l.MapDelete("/holidays/{id:guid}", async (Guid id, IHolidayService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(id, ct);
            return Results.NoContent();
        });
        l.MapPost("/holidays/{year:int}/federal", (int year, IHolidayService s, CancellationToken ct) => s.ApplyFederalTemplateAsync(year, ct));

        l.MapGet("/balances/mine", (ILeaveBalanceService s, CancellationToken ct) => s.GetMyBalancesAsync(ct));
        l.MapGet("/balances", (ILeaveBalanceService s, CancellationToken ct) => s.ListBalancesAsync(ct));
        l.MapGet("/balances/{employeeId:guid}/{policyId:guid}/history", (Guid employeeId, Guid policyId, ILeaveBalanceService s, CancellationToken ct) => s.GetHistoryAsync(employeeId, policyId, ct));
        l.MapPost("/balances/adjust", (AdjustLeaveRequest r, ILeaveBalanceService s, CancellationToken ct) => s.AdjustAsync(r, ct));
        l.MapPost("/accruals/{payPeriodId:guid}", (Guid payPeriodId, ILeaveBalanceService s, CancellationToken ct) => s.RunAccrualAsync(payPeriodId, ct));
        l.MapPost("/year-end/{year:int}", (int year, ILeaveBalanceService s, CancellationToken ct) => s.RunYearEndAsync(year, ct));

        l.MapGet("/requests/mine", (ILeaveRequestService s, CancellationToken ct) => s.ListMineAsync(ct));
        l.MapPost("/requests", (CreateLeaveRequest r, ILeaveRequestService s, CancellationToken ct) => s.CreateAsync(r, ct));
        l.MapPost("/requests/{id:guid}/cancel", (Guid id, ILeaveRequestService s, CancellationToken ct) => s.CancelAsync(id, ct));
        l.MapGet("/requests/is-approver", (ILeaveRequestService s, CancellationToken ct) => s.IsApproverAsync(ct));
        l.MapGet("/requests/approvals", (ILeaveRequestService s, CancellationToken ct) => s.ListPendingApprovalsAsync(ct));
        l.MapPost("/requests/{id:guid}/approve", (Guid id, DecideLeaveRequest r, ILeaveRequestService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        l.MapPost("/requests/{id:guid}/reject", (Guid id, DecideLeaveRequest r, ILeaveRequestService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        l.MapGet("/calendar", (DateOnly from, DateOnly to, ILeaveRequestService s, CancellationToken ct) => s.GetTeamCalendarAsync(from, to, ct));
        l.MapGet("/timesheets/{timesheetId:guid}", (Guid timesheetId, ILeaveTimesheetService s, CancellationToken ct) => s.GetForTimesheetAsync(timesheetId, ct));
    }

    /// <summary>Invalid state (409) and employee lookups (403) on top of the shared mapping.</summary>
    internal static async ValueTask<object?> MapLeaveErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException ex) when (ex is not Contracts.WorkbenchForbiddenException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
