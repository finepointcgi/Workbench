using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;

namespace Workbench.Api;

internal static class PayrollLeaveEndpoints
{
    public static void MapPayrollLeaveEndpoints(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/payroll").WithTags("Payroll").AddEndpointFilter(MapPayrollErrors);

        p.MapGet("/pay-groups", (IPayGroupService s, CancellationToken ct) => s.ListAsync(ct));
        p.MapGet("/pay-groups/{id:guid}", (Guid id, IPayGroupService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/pay-groups", (SavePayGroupRequest r, IPayGroupService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPut("/pay-groups/{id:guid}", (Guid id, SavePayGroupRequest r, IPayGroupService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapPost("/pay-groups/{id:guid}/periods/{year:int}", (Guid id, int year, IPayGroupService s, CancellationToken ct) => s.GeneratePeriodsAsync(id, year, ct));
        p.MapGet("/pay-groups/{id:guid}/periods", (Guid id, int? year, IPayGroupService s, CancellationToken ct) => s.ListPeriodsAsync(id, year, ct));
        p.MapPost("/pay-groups/{id:guid}/sync-employees", (Guid id, bool? dryRun, IPayrollProviderService s, CancellationToken ct) => s.SyncEmployeesAsync(id, dryRun ?? true, ct));

        p.MapGet("/profiles", (IPayrollProfileService s, CancellationToken ct) => s.ListAsync(ct));
        p.MapGet("/profiles/{employeeId:guid}", (Guid employeeId, IPayrollProfileService s, CancellationToken ct) => s.GetAsync(employeeId, ct));
        p.MapPut("/profiles/{employeeId:guid}", (Guid employeeId, SavePayProfileRequest r, IPayrollProfileService s, CancellationToken ct) => s.SaveAsync(employeeId, r, ct));
        p.MapPost("/profiles/{employeeId:guid}/reveal", (Guid employeeId, IPayrollProfileService s, CancellationToken ct) => s.RevealAsync(employeeId, ct));

        p.MapGet("/providers", (IPayrollProviderService s, CancellationToken ct) => s.ListProvidersAsync(ct));
        p.MapGet("/providers/{key}/settings", (string key, IPayrollProviderService s, CancellationToken ct) => s.GetSettingsAsync(key, ct));
        p.MapPut("/providers/{key}/settings", (string key, SavePayrollProviderSettingsRequest r, IPayrollProviderService s, CancellationToken ct) => s.SaveSettingsAsync(key, r, ct));

        p.MapGet("/runs", (Guid? payGroupId, IPayrollRunService s, CancellationToken ct) => s.ListAsync(payGroupId, ct));
        p.MapGet("/runs/{id:guid}", (Guid id, IPayrollRunService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/runs", (StartPayrollRunBody r, IPayrollRunService s, CancellationToken ct) => s.StartAsync(r.PayGroupId, r.PayPeriodId, ct));
        p.MapGet("/runs/{id:guid}/hours", (Guid id, IPayrollRunService s, CancellationToken ct) => s.PreviewHoursAsync(id, ct));
        p.MapPost("/runs/{id:guid}/export", (Guid id, ExportHoursRequest r, IPayrollRunService s, CancellationToken ct) => s.ExportHoursAsync(id, r, ct));
        p.MapPost("/runs/{id:guid}/register", (Guid id, ImportRegisterRequest r, IPayrollRunService s, CancellationToken ct) => s.ImportRegisterAsync(id, r, ct));
        p.MapGet("/runs/{id:guid}/register", (Guid id, IPayrollRunService s, CancellationToken ct) => s.GetRegisterAsync(id, ct));
        p.MapGet("/runs/{id:guid}/reconciliation", (Guid id, IPayrollRunService s, CancellationToken ct) => s.GetReconciliationAsync(id, ct));
        p.MapPost("/runs/{id:guid}/post", (Guid id, IPayrollRunService s, CancellationToken ct) => s.PostAsync(id, ct));
        p.MapPost("/runs/{id:guid}/void", (Guid id, VoidPayrollRequest r, IPayrollRunService s, CancellationToken ct) => s.VoidAsync(id, r, ct));

        var l = v1.MapGroup("/leave").WithTags("Leave").AddEndpointFilter(MapPayrollErrors);

        l.MapGet("/policies", (bool? includeInactive, ILeavePolicyService s, CancellationToken ct) => s.ListAsync(includeInactive ?? false, ct));
        l.MapGet("/policies/charge-codes", (ILeavePolicyService s, CancellationToken ct) => s.ListLeaveChargeCodesAsync(ct));
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

    /// <summary>Starts a run for a pay group and period.</summary>
    internal sealed record StartPayrollRunBody(Guid PayGroupId, Guid PayPeriodId);

    /// <summary>Coming-soon providers (501), invalid state (409) and employee lookups (403) on top of the shared mapping.</summary>
    private static async ValueTask<object?> MapPayrollErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (NotSupportedException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
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
