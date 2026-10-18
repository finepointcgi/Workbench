using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Revenue;

namespace Workbench.Api;

internal static class RevenueEndpoints
{
    public static void MapRevenueEndpoints(this RouteGroupBuilder v1)
    {
        var r = v1.MapGroup("/revenue").WithTags("Revenue recognition").AddEndpointFilter(MapConflicts);

        // Setup, estimates at completion and deliveries
        r.MapGet("/projects", (IRevenueSetupService s, CancellationToken ct) => s.ListProjectsAsync(ct));
        r.MapGet("/projects/{projectId:guid}", (Guid projectId, IRevenueSetupService s, CancellationToken ct) => s.GetProjectAsync(projectId, ct));
        r.MapPut("/setups", (SaveRevenueSetupRequest req, IRevenueSetupService s, CancellationToken ct) => s.SaveSetupAsync(req, ct));
        r.MapDelete("/setups/{id:guid}", async (Guid id, IRevenueSetupService s, CancellationToken ct) =>
        {
            await s.DeleteSetupAsync(id, ct);
            return Results.NoContent();
        });
        r.MapGet("/estimates", (EstimateState? status, IRevenueSetupService s, CancellationToken ct) => s.ListEstimatesAsync(status, ct));
        r.MapPost("/estimates", (SubmitEstimateRequest req, IRevenueSetupService s, CancellationToken ct) => s.SubmitEstimateAsync(req, ct));
        r.MapPost("/estimates/{id:guid}/approve", (Guid id, DecideEstimateRequest req, IRevenueSetupService s, CancellationToken ct) => s.ApproveEstimateAsync(id, req, ct));
        r.MapPost("/estimates/{id:guid}/reject", (Guid id, DecideEstimateRequest req, IRevenueSetupService s, CancellationToken ct) => s.RejectEstimateAsync(id, req, ct));
        r.MapPost("/deliveries", (RecordDeliveryRequest req, IRevenueSetupService s, CancellationToken ct) => s.RecordDeliveryAsync(req, ct));

        // Runs
        r.MapGet("/runs", (Guid? periodId, IRevenueRunService s, CancellationToken ct) => s.ListRunsAsync(periodId, ct));
        r.MapGet("/runs/{id:guid}", (Guid id, IRevenueRunService s, CancellationToken ct) => s.GetRunAsync(id, ct));
        r.MapPost("/runs/{id:guid}/reverse", (Guid id, ReverseRevenueRunRequest req, IRevenueRunService s, CancellationToken ct) => s.ReverseAsync(id, req, ct));
        r.MapGet("/periods/{periodId:guid}/preview", (Guid periodId, IRevenueRunService s, CancellationToken ct) => s.PreviewAsync(periodId, ct));
        r.MapPost("/periods/{periodId:guid}/post", (Guid periodId, PostRevenueRunRequest req, IRevenueRunService s, CancellationToken ct) => s.PostAsync(periodId, req, ct));

        // Reports
        r.MapGet("/reports/by-period", (Guid fromPeriodId, Guid toPeriodId, RevenueGrouping? groupBy, IRevenueReportService s, CancellationToken ct) =>
            s.GetRevenueByPeriodAsync(new RevenueByPeriodQuery(fromPeriodId, toPeriodId, groupBy ?? RevenueGrouping.Project), ct));
        r.MapGet("/reports/revenue-vs-billings/{periodId:guid}", (Guid periodId, IRevenueReportService s, CancellationToken ct) => s.GetRevenueVsBillingsAsync(periodId, ct));
        r.MapGet("/reports/backlog/{periodId:guid}", (Guid periodId, IRevenueReportService s, CancellationToken ct) => s.GetBacklogAsync(periodId, ct));
    }

    /// <summary>Wrong state (already decided, already reversed, later runs posted) → 409.</summary>
    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
