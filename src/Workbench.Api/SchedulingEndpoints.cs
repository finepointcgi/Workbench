using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Scheduling;

namespace Workbench.Api;

internal static class SchedulingEndpoints
{
    public static void MapSchedulingEndpoints(this RouteGroupBuilder v1)
    {
        var sheet = v1.MapGroup("/sheets/{sheetId:guid}").WithTags("Scheduling");

        sheet.MapGet("/dependencies", (Guid sheetId, IDependencyService service, CancellationToken ct) => service.ListAsync(sheetId, ct));

        sheet.MapPost("/dependencies", async (Guid sheetId, CreateDependencyRequest request, IDependencyService service, CancellationToken ct) =>
        {
            var created = await service.AddAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/dependencies/{created.Id}", created);
        });

        sheet.MapDelete("/dependencies/{dependencyId:guid}", async (Guid sheetId, Guid dependencyId, IDependencyService service, CancellationToken ct) =>
        {
            await service.RemoveAsync(sheetId, dependencyId, ct);
            return Results.NoContent();
        });

        sheet.MapGet("/schedule", (Guid sheetId, IDependencyService service, CancellationToken ct) => service.AnalyzeAsync(sheetId, ct));

        sheet.MapGet("/baselines", (Guid sheetId, IBaselineService service, CancellationToken ct) => service.ListAsync(sheetId, ct));

        sheet.MapGet("/baselines/{baselineId:guid}", (Guid sheetId, Guid baselineId, IBaselineService service, CancellationToken ct) =>
            service.GetAsync(sheetId, baselineId, ct));

        sheet.MapPost("/baselines", async (Guid sheetId, CaptureBaselineRequest request, IBaselineService service, CancellationToken ct) =>
        {
            var created = await service.CaptureAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/baselines/{created.Id}", created);
        });
    }
}
