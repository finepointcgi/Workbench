using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Automations;

namespace Workbench.Api;

/// <summary>Admin › Automation (cross-module automations) and the to-dos they create. Sheet automations are here too (<c>/automations/sheets/{id}</c>); the older sheet rules API is in <see cref="AutomationEndpoints"/>.</summary>
internal static class AutomationBuilderEndpoints
{
    public static void MapAutomationBuilderEndpoints(this RouteGroupBuilder v1)
    {
        var automations = v1.MapGroup("/automations").WithTags("Automations");
        automations.MapGet("", (IAutomationService s, CancellationToken ct) => s.ListAsync(ct));
        automations.MapGet("/catalog", (IAutomationService s, CancellationToken ct) => s.GetCatalogAsync(ct));
        automations.MapGet("/templates", (IAutomationService s) => s.ListTemplates());
        automations.MapPost("/templates/{key}/build", (string key, BuildRecipeBody body, IAutomationService s, CancellationToken ct) =>
            s.BuildRecipeAsync(key, body.SheetId, body.Blanks ?? new Dictionary<string, string?>(), ct));
        automations.MapGet("/sheets/{sheetId:guid}", (Guid sheetId, IAutomationService s, CancellationToken ct) => s.ListForSheetAsync(sheetId, ct));
        automations.MapGet("/sheets/{sheetId:guid}/catalog", (Guid sheetId, IAutomationService s, CancellationToken ct) => s.GetSheetCatalogAsync(sheetId, ct));
        automations.MapGet("/settings", (IAutomationService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        automations.MapPut("/settings", (SaveAutomationSettingsRequest request, IAutomationService s, CancellationToken ct) => s.SaveSettingsAsync(request, ct));
        automations.MapGet("/runs", (Guid? automationId, AutomationRunStatus? status, string? entityType, Guid? recordId, int? take, IAutomationService s, CancellationToken ct) =>
            s.ListRunsAsync(new AutomationRunQuery(automationId, status, entityType, recordId, take ?? 100), ct));
        automations.MapGet("/runs/{runId:guid}", (Guid runId, IAutomationService s, CancellationToken ct) => s.GetRunAsync(runId, ct));
        automations.MapGet("/records/{entityType}", (string entityType, string? search, IAutomationService s, CancellationToken ct) => s.ListRecordsAsync(entityType, search, ct));
        automations.MapPost("/dry-run", (AutomationDryRunRequest request, IAutomationService s, CancellationToken ct) => s.DryRunAsync(request, ct));
        automations.MapGet("/tasks/mine", (IAutomationTaskService s, CancellationToken ct) => s.ListMineAsync(ct));
        automations.MapPost("/tasks/{taskId:guid}/complete", (Guid taskId, IAutomationTaskService s, CancellationToken ct) => s.CompleteAsync(taskId, ct));

        automations.MapPost("", async (SaveAutomationRequest request, IAutomationService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(request, ct);
            return Results.Created($"/api/v1/automations/{created.Id}", created);
        });
        automations.MapGet("/{id:guid}", (Guid id, IAutomationService s, CancellationToken ct) => s.GetAsync(id, ct));
        automations.MapPut("/{id:guid}", (Guid id, SaveAutomationRequest request, IAutomationService s, CancellationToken ct) => s.UpdateAsync(id, request, ct));
        automations.MapDelete("/{id:guid}", async (Guid id, IAutomationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        automations.MapPut("/{id:guid}/enabled", (Guid id, SetAutomationEnabledBody body, IAutomationService s, CancellationToken ct) => s.SetEnabledAsync(id, body.Enabled, ct));
        automations.MapGet("/{id:guid}/versions/{version:int}", (Guid id, int version, IAutomationService s, CancellationToken ct) => s.GetVersionAsync(id, version, ct));
        automations.MapPost("/{id:guid}/versions/{version:int}/restore", (Guid id, int version, IAutomationService s, CancellationToken ct) => s.RestoreVersionAsync(id, version, ct));
        automations.MapPost("/{id:guid}/run", (Guid id, AutomationRunNowRequest request, IAutomationService s, CancellationToken ct) => s.RunNowAsync(id, request, ct));
    }
}

/// <summary>Body of <c>POST /automations/templates/{key}/build</c>: the sheet (for sheet recipes) and the blanks filled in.</summary>
public sealed record BuildRecipeBody(Guid? SheetId, Dictionary<string, string?>? Blanks);

/// <summary>Body of <c>PUT /automations/{id}/enabled</c>.</summary>
public sealed record SetAutomationEnabledBody(bool Enabled);
