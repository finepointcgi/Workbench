using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Automation;

namespace Workbench.Api;

internal static class AutomationEndpoints
{
    public static void MapAutomationEndpoints(this RouteGroupBuilder v1)
    {
        var rules = v1.MapGroup("/sheets/{sheetId:guid}/rules").WithTags("Automation");

        rules.MapGet("/", (Guid sheetId, IRuleService service, CancellationToken ct) => service.ListAsync(sheetId, ct));

        rules.MapGet("/{ruleId:guid}", (Guid sheetId, Guid ruleId, IRuleService service, CancellationToken ct) =>
            service.GetAsync(sheetId, ruleId, ct));

        rules.MapPost("/", async (Guid sheetId, SaveRuleRequest request, IRuleService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/rules/{created.Id}", created);
        });

        rules.MapPut("/{ruleId:guid}", (Guid sheetId, Guid ruleId, SaveRuleRequest request, IRuleService service, CancellationToken ct) =>
            service.UpdateAsync(sheetId, ruleId, request, ct));

        rules.MapPut("/{ruleId:guid}/enabled", (Guid sheetId, Guid ruleId, SetRuleEnabledBody body, IRuleService service, CancellationToken ct) =>
            service.SetEnabledAsync(sheetId, ruleId, body.Enabled, ct));

        rules.MapDelete("/{ruleId:guid}", async (Guid sheetId, Guid ruleId, IRuleService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sheetId, ruleId, ct);
            return Results.NoContent();
        });

        rules.MapGet("/runs", (Guid sheetId, Guid? ruleId, int? limit, IRuleService service, CancellationToken ct) =>
            service.ListRunsAsync(sheetId, ruleId, limit ?? 50, ct));

        rules.MapPost("/test", (Guid sheetId, TestRuleRequest request, IRuleService service, CancellationToken ct) =>
            service.TestAsync(sheetId, request, ct));
    }
}

/// <summary>Body of <c>PUT /sheets/{sheetId}/rules/{ruleId}/enabled</c>.</summary>
public sealed record SetRuleEnabledBody(bool Enabled);
