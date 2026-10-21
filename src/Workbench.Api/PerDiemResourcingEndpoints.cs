using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Resourcing;

namespace Workbench.Api;

internal static class PerDiemResourcingEndpoints
{
    public static void MapPerDiemResourcingEndpoints(this RouteGroupBuilder v1)
    {
        var rates = v1.MapGroup("/per-diem").WithTags("Per diem");
        rates.MapGet("/rates", (int? fiscalYear, PerDiemTable? kind, string? search, int? take, IPerDiemRateService s, CancellationToken ct) =>
            s.GetTableAsync(new PerDiemRateQuery(fiscalYear, kind, search, take ?? 500), ct));
        rates.MapGet("/locations", (int fiscalYear, PerDiemTable? kind, string? search, IPerDiemRateService s, CancellationToken ct) =>
            s.ListLocationsAsync(fiscalYear, kind, search, ct));
        rates.MapPost("/lookup", (PerDiemLookupRequest r, IPerDiemRateService s, CancellationToken ct) => s.LookupAsync(r.Location, r.Date, ct));
        rates.MapPost("/imports/file", (ImportPerDiemFileRequest r, IPerDiemRateService s, CancellationToken ct) => s.ImportFileAsync(r, ct));
        rates.MapPost("/imports/gsa/{fiscalYear:int}", (int fiscalYear, IPerDiemRateService s, CancellationToken ct) => s.ImportFromGsaApiAsync(fiscalYear, ct));
        rates.MapPost("/imports/sample/{fiscalYear:int}", (int fiscalYear, IPerDiemRateService s, CancellationToken ct) => s.InstallSampleRatesAsync(fiscalYear, ct));

        var trips = v1.MapGroup("/expense-reports/{reportId:guid}/per-diem").WithTags("Per diem").AddEndpointFilter(MapConflicts);
        trips.MapGet("", (Guid reportId, ITravelPerDiemService s, CancellationToken ct) => s.GetAsync(reportId, ct));
        trips.MapPut("", (Guid reportId, SaveTravelPerDiemRequest r, ITravelPerDiemService s, CancellationToken ct) => s.SaveAsync(reportId, r, ct));
        trips.MapPost("/apply", (Guid reportId, ITravelPerDiemService s, CancellationToken ct) => s.ApplyToReportAsync(reportId, ct));
        trips.MapDelete("", async (Guid reportId, ITravelPerDiemService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(reportId, ct);
            return Results.NoContent();
        });

        var res = v1.MapGroup("/resourcing").WithTags("Resourcing");
        res.MapGet("/allocations", (DateOnly from, DateOnly to, Guid? projectId, Guid? employeeId, IResourceAllocationService s, CancellationToken ct) =>
            s.ListAsync(new AllocationQuery(from, to, projectId, employeeId), ct));
        res.MapGet("/allocations/mine", (DateOnly from, DateOnly to, IResourceAllocationService s, CancellationToken ct) => s.ListMineAsync(from, to, ct));
        res.MapPost("/allocations", (SaveAllocationRequest r, IResourceAllocationService s, CancellationToken ct) => s.CreateAsync(r, ct));
        res.MapPut("/allocations/{id:guid}", (Guid id, SaveAllocationRequest r, IResourceAllocationService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        res.MapDelete("/allocations/{id:guid}", async (Guid id, IResourceAllocationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        res.MapPost("/projects/{projectId:guid}/generate-from-plan", (Guid projectId, GenerateAllocationsRequest? r, IResourceAllocationService s, CancellationToken ct) =>
            s.GenerateFromPlanAsync(projectId, r, ct));
        res.MapGet("/projects/{projectId:guid}/can-manage", (Guid projectId, IResourceAllocationService s, CancellationToken ct) => s.CanManageAsync(projectId, ct));

        res.MapPost("/capacity", (CapacityRequest r, IResourcePlanningService s, CancellationToken ct) => s.GetCapacityAsync(r.From, r.To, r.EmployeeIds, ct));
        res.MapPost("/heat-map", (HeatMapQuery q, IResourcePlanningService s, CancellationToken ct) => s.GetHeatMapAsync(q, ct));
        res.MapGet("/projects/{projectId:guid}/staffing", (Guid projectId, IResourcePlanningService s, CancellationToken ct) => s.GetProjectStaffingAsync(projectId, ct));
        res.MapGet("/utilization", (DateOnly from, DateOnly to, IResourcePlanningService s, CancellationToken ct) => s.GetUtilizationAsync(from, to, ct));
        res.MapGet("/open-demand", (Guid? projectId, IResourcePlanningService s, CancellationToken ct) => s.ListOpenDemandAsync(projectId, ct));
        res.MapPost("/find-people", (FindPeopleQuery q, IResourcePlanningService s, CancellationToken ct) => s.FindAvailablePeopleAsync(q, ct));

        res.MapGet("/projects/{projectId:guid}/staffing-plan", (Guid projectId, IProjectStaffingService s, CancellationToken ct) => s.GetPlanAsync(projectId, ct));
        res.MapGet("/projects/{projectId:guid}/setup-progress", (Guid projectId, IProjectStaffingService s, CancellationToken ct) => s.GetSetupProgressAsync(projectId, ct));
        res.MapPost("/staffing-check", (StaffingCheckRequest r, IProjectStaffingService s, CancellationToken ct) => s.CheckAsync(r, ct));
        res.MapGet("/projects/{projectId:guid}/staffing-candidates", (Guid projectId, Guid? laborCategoryId, IProjectStaffingService s, CancellationToken ct) =>
            s.ListCandidatesAsync(projectId, laborCategoryId, ct));

        v1.MapGet("/projects/{projectId:guid}/financials/allocation-etc", (Guid projectId, Guid? budgetVersionId, IAllocationForecastService s, CancellationToken ct) =>
            s.GetProjectSummaryAsync(projectId, budgetVersionId, ct)).WithTags("Resourcing");
        v1.MapGet("/projects/{projectId:guid}/financials/allocation-etc/wbs", (Guid projectId, Guid? budgetVersionId, IAllocationForecastService s, CancellationToken ct) =>
            s.GetWbsFinancialsAsync(projectId, budgetVersionId, ct)).WithTags("Resourcing");
    }

    /// <summary>Wrong state (a submitted or approved report) → 409.</summary>
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
