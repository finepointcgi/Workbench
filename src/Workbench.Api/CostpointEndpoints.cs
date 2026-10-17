using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Costpoint;

namespace Workbench.Api;

internal static class CostpointEndpoints
{
    public static void MapCostpointEndpoints(this RouteGroupBuilder v1)
    {
        var costpoint = v1.MapGroup("/costpoint").WithTags("Costpoint");

        costpoint.MapGet("/status", (ICostpointService service, CancellationToken ct) => service.GetStatusAsync(ct));

        costpoint.MapGet("/sync-runs", (int? take, ICostpointService service, CancellationToken ct) =>
            service.ListSyncRunsAsync(take ?? 20, ct));

        // TODO: restrict to admins once identity is wired up. Only refreshes Workbench's mirror; never writes to Costpoint.
        costpoint.MapPost("/sync", (ICostpointService service, CancellationToken ct) => service.SyncNowAsync(ct));

        costpoint.MapGet("/projects", (string? q, int? take, ICostpointService service, CancellationToken ct) =>
            service.SearchProjectsAsync(q, take ?? 20, ct));

        costpoint.MapGet("/wbs", (string? projectId, string? q, int? take, ICostpointService service, CancellationToken ct) =>
            service.SearchWbsAsync(projectId, q, take ?? 20, ct));

        v1.MapGet("/sheets/{sheetId:guid}/plan-vs-actual", (Guid sheetId, ICostpointService service, CancellationToken ct) =>
            service.GetPlanVsActualAsync(sheetId, ct)).WithTags("Costpoint");
    }
}
