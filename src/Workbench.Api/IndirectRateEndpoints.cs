using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;

namespace Workbench.Api;

internal static class IndirectRateEndpoints
{
    public static void MapIndirectRateEndpoints(this RouteGroupBuilder v1)
    {
        var ir = v1.MapGroup("/accounting/indirect-rates").WithTags("Indirect rates").AddEndpointFilter(MapConflicts);

        ir.MapGet("/pools", (IIndirectRateService s, CancellationToken ct) => s.ListPoolSetupsAsync(ct));
        ir.MapPut("/pools/{pool}", (CostPool pool, SavePoolSetupRequest r, IIndirectRateService s, CancellationToken ct) => s.SavePoolSetupAsync(pool, r, ct));
        ir.MapPost("/pools/install-default", (IIndirectRateService s, CancellationToken ct) => s.InstallDefaultPoolsAsync(ct));

        ir.MapGet("/rates", (int? fiscalYear, bool? includeHistory, IIndirectRateService s, CancellationToken ct) => s.ListRatesAsync(fiscalYear, includeHistory ?? false, ct));
        ir.MapPost("/rates", (SaveIndirectRateRequest r, IIndirectRateService s, CancellationToken ct) => s.SaveRateAsync(r, ct));
        ir.MapPost("/rates/{id:guid}/approve", (Guid id, IIndirectRateService s, CancellationToken ct) => s.ApproveRateAsync(id, ct));
        ir.MapPost("/rates/{id:guid}/withdraw", async (Guid id, WithdrawIndirectRateRequest r, IIndirectRateService s, CancellationToken ct) =>
        {
            await s.WithdrawRateAsync(id, r, ct);
            return Results.NoContent();
        });

        ir.MapGet("/calculation", (Guid fromPeriodId, Guid toPeriodId, IIndirectRateService s, CancellationToken ct) => s.GetRateCalculationAsync(fromPeriodId, toPeriodId, ct));

        ir.MapGet("/allocations", (int fiscalYear, IIndirectRateService s, CancellationToken ct) => s.ListAllocationRunsAsync(fiscalYear, ct));
        ir.MapGet("/allocations/{periodId:guid}", (Guid periodId, IIndirectRateService s, CancellationToken ct) => s.PreviewAllocationAsync(periodId, ct));
        ir.MapPost("/allocations/{periodId:guid}/post", (Guid periodId, IIndirectRateService s, CancellationToken ct) => s.PostAllocationAsync(periodId, ct));
        ir.MapPost("/allocations/{periodId:guid}/reverse", (Guid periodId, ReverseAllocationRequest r, IIndirectRateService s, CancellationToken ct) => s.ReverseAllocationAsync(periodId, r, ct));

        ir.MapGet("/true-up/{fiscalYear:int}", (int fiscalYear, IIndirectRateService s, CancellationToken ct) => s.GetTrueUpAsync(fiscalYear, ct));
        ir.MapPost("/true-up/{fiscalYear:int}/post", (int fiscalYear, PostTrueUpRequest r, IIndirectRateService s, CancellationToken ct) => s.PostTrueUpAsync(fiscalYear, r, ct));

        // Burdened cost (billing, financials): Finance, Admin or the project's manager.
        v1.MapGet("/projects/{projectId:guid}/burdened-cost", (Guid projectId, DateOnly from, DateOnly to, RateBasis? basis, IBurdenedCostService s, CancellationToken ct) =>
            s.GetProjectCostAsync(projectId, from, to, basis ?? RateBasis.Provisional, ct)).WithTags("Indirect rates");
        v1.MapGet("/projects/{projectId:guid}/financials/burdened", (Guid projectId, Guid? budgetVersionId, Guid? forecastVersionId, IBurdenedFinancialsService s, CancellationToken ct) =>
            s.GetProjectAsync(projectId, budgetVersionId, forecastVersionId, ct)).WithTags("Indirect rates");
    }

    /// <summary>Wrong state (closed period, nothing posted, missing actual rates) → 409.</summary>
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
