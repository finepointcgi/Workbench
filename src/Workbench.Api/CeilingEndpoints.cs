using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;

namespace Workbench.Api;

internal static class CeilingEndpoints
{
    public static void MapCeilingEndpoints(this RouteGroupBuilder v1)
    {
        var ceilings = v1.MapGroup("").WithTags("Ceilings");
        ceilings.MapGet("/contracts/{id:guid}/ceilings", (Guid id, ICeilingService s, CancellationToken ct) => s.GetContractStatusAsync(id, ct));
        ceilings.MapGet("/projects/{id:guid}/ceilings", (Guid id, ICeilingService s, CancellationToken ct) => s.GetProjectStatusAsync(id, ct));
        ceilings.MapPost("/ceilings", async (SaveCeilingRequest r, ICeilingService s, CancellationToken ct) =>
        {
            var ceiling = await s.CreateAsync(r, ct);
            return Results.Created($"/api/v1/ceilings/{ceiling.Id}", ceiling);
        });
        ceilings.MapGet("/ceilings/{id:guid}", (Guid id, ICeilingService s, CancellationToken ct) => s.GetAsync(id, ct));
        ceilings.MapPut("/ceilings/{id:guid}", (Guid id, SaveCeilingRequest r, ICeilingService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        ceilings.MapPost("/ceilings/{id:guid}/deactivate", (Guid id, ICeilingService s, CancellationToken ct) => s.DeactivateAsync(id, ct));
        ceilings.MapGet("/ceilings/{id:guid}/overrides", (Guid id, ICeilingService s, CancellationToken ct) => s.ListOverridesAsync(id, ct));
        ceilings.MapPost("/ceilings/{id:guid}/overrides", (Guid id, GrantCeilingOverrideRequest r, ICeilingService s, CancellationToken ct) => s.GrantOverrideAsync(id, r, ct));
        ceilings.MapPost("/ceiling-overrides/{id:guid}/revoke", (Guid id, CeilingReasonRequest r, ICeilingService s, CancellationToken ct) => s.RevokeOverrideAsync(id, r, ct));
        ceilings.MapGet("/contracts/{id:guid}/rate-ceilings", (Guid id, ICeilingService s, CancellationToken ct) => s.ListRateCeilingsAsync(id, ct));
        ceilings.MapPost("/contracts/{id:guid}/rate-ceilings", (Guid id, SaveRateCeilingRequest r, ICeilingService s, CancellationToken ct) => s.CreateRateCeilingAsync(id, r, ct));
        ceilings.MapPost("/rate-ceilings/{id:guid}/deactivate", (Guid id, ICeilingService s, CancellationToken ct) => s.DeactivateRateCeilingAsync(id, ct));
        ceilings.MapPost("/contracts/{id:guid}/ceilings/check-alerts", (Guid id, ICeilingService s, CancellationToken ct) => s.CheckAlertsAsync(id, ct));

        // Closeout: {scope} is contract or project.
        var closeout = v1.MapGroup("/closeout/{scope}/{id:guid}").WithTags("Closeout");
        closeout.MapGet("", (string scope, Guid id, ICloseoutService s, CancellationToken ct) => s.GetAsync(Scope(scope), id, ct));
        closeout.MapPost("/physically-complete", (string scope, Guid id, PhysicallyCompleteRequest r, ICloseoutService s, CancellationToken ct) =>
            s.MarkPhysicallyCompleteAsync(Scope(scope), id, r, ct));
        closeout.MapPost("/start-closing", (string scope, Guid id, ICloseoutService s, CancellationToken ct) => s.StartClosingAsync(Scope(scope), id, ct));
        closeout.MapPost("/close", (string scope, Guid id, ICloseoutService s, CancellationToken ct) => s.CloseAsync(Scope(scope), id, ct));
        closeout.MapPost("/reopen", (string scope, Guid id, CloseoutReasonRequest r, ICloseoutService s, CancellationToken ct) => s.ReopenAsync(Scope(scope), id, r, ct));
        closeout.MapPut("/records", (string scope, Guid id, SaveCloseoutRecordsRequest r, ICloseoutService s, CancellationToken ct) => s.SaveRecordsAsync(Scope(scope), id, r, ct));
        closeout.MapPost("/waivers", (string scope, Guid id, WaiveCloseoutItemRequest r, ICloseoutService s, CancellationToken ct) => s.WaiveAsync(Scope(scope), id, r, ct));
        closeout.MapDelete("/waivers/{check}", (string scope, Guid id, CloseoutCheck check, ICloseoutService s, CancellationToken ct) => s.RemoveWaiverAsync(Scope(scope), id, check, ct));
        var contract = v1.MapGroup("/closeout/contract/{id:guid}").WithTags("Closeout");
        contract.MapPut("/final-invoice", (Guid id, MarkFinalInvoiceRequest r, ICloseoutService s, CancellationToken ct) => s.MarkFinalInvoiceAsync(id, r, ct));
        contract.MapPost("/deobligation", (Guid id, ICloseoutService s, CancellationToken ct) => s.RecordDeobligationAsync(id, ct));
    }

    private static CloseoutScope Scope(string scope) => scope.ToLowerInvariant() switch
    {
        "contract" => CloseoutScope.Contract,
        "project" => CloseoutScope.Project,
        _ => throw new ArgumentException("The closeout scope is contract or project."),
    };
}
