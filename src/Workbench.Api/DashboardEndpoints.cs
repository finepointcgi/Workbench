using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Dashboards;

namespace Workbench.Api;

internal static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this RouteGroupBuilder v1)
    {
        var dashboards = v1.MapGroup("/dashboards").WithTags("Dashboards");

        dashboards.MapGet("/", (IDashboardService service, CancellationToken ct) => service.ListAsync(ct));

        dashboards.MapGet("/{dashboardId:guid}", (Guid dashboardId, IDashboardService service, CancellationToken ct) =>
            service.GetAsync(dashboardId, ct));

        dashboards.MapPost("/", async (CreateDashboardRequest request, IDashboardService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/dashboards/{created.Id}", created);
        });

        dashboards.MapPut("/{dashboardId:guid}", (Guid dashboardId, UpdateDashboardRequest request, IDashboardService service, CancellationToken ct) =>
            service.UpdateAsync(dashboardId, request, ct));

        dashboards.MapDelete("/{dashboardId:guid}", async (Guid dashboardId, IDashboardService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(dashboardId, ct);
            return Results.NoContent();
        });

        dashboards.MapPost("/{dashboardId:guid}/widgets", async (Guid dashboardId, SaveWidgetRequest request, IDashboardService service, CancellationToken ct) =>
        {
            var created = await service.AddWidgetAsync(dashboardId, request, ct);
            return Results.Created($"/api/v1/dashboards/{dashboardId}/widgets/{created.Id}", created);
        });

        dashboards.MapPut("/{dashboardId:guid}/widgets/{widgetId:guid}",
            (Guid dashboardId, Guid widgetId, SaveWidgetRequest request, IDashboardService service, CancellationToken ct) =>
                service.UpdateWidgetAsync(dashboardId, widgetId, request, ct));

        dashboards.MapDelete("/{dashboardId:guid}/widgets/{widgetId:guid}", async (Guid dashboardId, Guid widgetId, IDashboardService service, CancellationToken ct) =>
        {
            await service.DeleteWidgetAsync(dashboardId, widgetId, ct);
            return Results.NoContent();
        });

        dashboards.MapPost("/{dashboardId:guid}/widgets/reorder", (Guid dashboardId, ReorderWidgetsRequest request, IDashboardService service, CancellationToken ct) =>
            service.ReorderWidgetsAsync(dashboardId, request, ct));

        dashboards.MapGet("/{dashboardId:guid}/widgets/{widgetId:guid}/data", (Guid dashboardId, Guid widgetId, Guid? companyId, IWidgetDataService service, CancellationToken ct) =>
            service.GetAsync(dashboardId, widgetId, companyId, ct));
    }
}
