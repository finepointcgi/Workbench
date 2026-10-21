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

        // The plan editor (MS Project-style outline, durations, predecessors) and its Excel export and import.
        sheet.MapGet("/plan", (Guid sheetId, IPlanScheduleService service, CancellationToken ct) => service.GetAsync(sheetId, ct));

        sheet.MapPost("/plan/items/{itemId:guid}/indent", async (Guid sheetId, Guid itemId, IPlanScheduleService service, CancellationToken ct) =>
        {
            await service.IndentAsync(sheetId, itemId, ct);
            return Results.NoContent();
        });

        sheet.MapPost("/plan/items/{itemId:guid}/outdent", async (Guid sheetId, Guid itemId, IPlanScheduleService service, CancellationToken ct) =>
        {
            await service.OutdentAsync(sheetId, itemId, ct);
            return Results.NoContent();
        });

        sheet.MapPut("/plan/items/{itemId:guid}/duration", async (Guid sheetId, Guid itemId, SetPlanDurationBody body, IPlanScheduleService service, CancellationToken ct) =>
        {
            await service.SetDurationAsync(sheetId, itemId, body.Days, ct);
            return Results.NoContent();
        });

        sheet.MapPut("/plan/items/{itemId:guid}/predecessors", async (Guid sheetId, Guid itemId, SetPlanPredecessorsBody body, IPlanScheduleService service, CancellationToken ct) =>
        {
            await service.SetPredecessorsAsync(sheetId, itemId, body.Predecessors, ct);
            return Results.NoContent();
        });

        sheet.MapGet("/plan/export", (Guid sheetId, IPlanScheduleService service, CancellationToken ct) => service.ExportAsync(sheetId, ct));

        sheet.MapGet("/plan.xlsx", async (Guid sheetId, IPlanScheduleService service, CancellationToken ct) =>
        {
            var file = await service.ExportAsync(sheetId, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        sheet.MapPost("/plan/import/preview", (Guid sheetId, PlanImportRequest request, IPlanScheduleService service, CancellationToken ct) =>
            service.PreviewImportAsync(sheetId, request, ct));

        sheet.MapPost("/plan/import", (Guid sheetId, PlanImportRequest request, IPlanScheduleService service, CancellationToken ct) =>
            service.ApplyImportAsync(sheetId, request, ct));

        v1.MapGet("/plan-template", (IPlanScheduleService service, CancellationToken ct) => service.TemplateAsync(ct)).WithTags("Scheduling");

        sheet.MapPost("/baselines", async (Guid sheetId, CaptureBaselineRequest request, IBaselineService service, CancellationToken ct) =>
        {
            var created = await service.CaptureAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/baselines/{created.Id}", created);
        });
    }
}

/// <summary>Body of <c>PUT /sheets/{id}/plan/items/{itemId}/duration</c>.</summary>
public sealed record SetPlanDurationBody(int Days);

/// <summary>Body of <c>PUT /sheets/{id}/plan/items/{itemId}/predecessors</c>: MS Project text such as <c>3, 5SS+2d</c>.</summary>
public sealed record SetPlanPredecessorsBody(string? Predecessors);
