using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Contracts;

namespace Workbench.Api;

internal static class ContractDetailEndpoints
{
    public static void MapContractDetailEndpoints(this RouteGroupBuilder v1)
    {
        var clins = v1.MapGroup("").WithTags("CLINs");
        clins.MapGet("/contracts/{id:guid}/clins", (Guid id, IClinService s, CancellationToken ct) => s.ListClinsAsync(id, ct));
        clins.MapPost("/contracts/{id:guid}/clins", async (Guid id, SaveClinRequest r, IClinService s, CancellationToken ct) =>
        {
            var clin = await s.CreateClinAsync(id, r, ct);
            return Results.Created($"/api/v1/clins/{clin.Id}", clin);
        });
        clins.MapGet("/clins/{id:guid}", async (Guid id, IClinService s, CancellationToken ct) =>
            await s.GetClinAsync(id, ct) is { } clin ? Results.Ok(clin) : Results.NotFound());
        clins.MapPut("/clins/{id:guid}", (Guid id, SaveClinRequest r, IClinService s, CancellationToken ct) => s.UpdateClinAsync(id, r, ct));
        clins.MapGet("/projects/{id:guid}/clins", (Guid id, IClinService s, CancellationToken ct) => s.ListClinsForProjectAsync(id, ct));
        clins.MapGet("/projects/{id:guid}/wbs-clins", (Guid id, IClinService s, CancellationToken ct) => s.ListWbsMappingsAsync(id, ct));
        clins.MapPut("/wbs/{id:guid}/clin", (Guid id, SetWbsClinBody r, IClinService s, CancellationToken ct) => s.SetWbsClinAsync(id, r.ClinId, ct));

        var mods = v1.MapGroup("").WithTags("Contract modifications");
        mods.MapGet("/contracts/{id:guid}/modifications", (Guid id, IContractModificationService s, CancellationToken ct) => s.ListAsync(id, ct));
        mods.MapPost("/contracts/{id:guid}/modifications", async (Guid id, SaveModificationRequest r, IContractModificationService s, CancellationToken ct) =>
        {
            var mod = await s.CreateDraftAsync(id, r, ct);
            return Results.Created($"/api/v1/modifications/{mod.Id}", mod);
        });
        mods.MapGet("/modifications/{id:guid}", async (Guid id, IContractModificationService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } mod ? Results.Ok(mod) : Results.NotFound());
        mods.MapPut("/modifications/{id:guid}", (Guid id, SaveModificationRequest r, IContractModificationService s, CancellationToken ct) => s.UpdateDraftAsync(id, r, ct));
        mods.MapDelete("/modifications/{id:guid}", async (Guid id, IContractModificationService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        });
        mods.MapPost("/modifications/{id:guid}/execute", (Guid id, IContractModificationService s, CancellationToken ct) => s.ExecuteAsync(id, ct));

        var funding = v1.MapGroup("").WithTags("CLIN funding");
        funding.MapGet("/contracts/{id:guid}/funding-history", (Guid id, Guid? clinId, IFundingService s, CancellationToken ct) => s.ListFundingHistoryAsync(id, clinId, ct));
        funding.MapGet("/contracts/{id:guid}/clin-funding", (Guid id, IFundingService s, CancellationToken ct) => s.GetFundingStatusAsync(id, ct));
        funding.MapGet("/projects/{id:guid}/clin-funding", async (Guid id, IFundingService s, CancellationToken ct) =>
            await s.GetProjectFundingStatusAsync(id, ct) is { } status ? Results.Ok(status) : Results.NoContent());
        funding.MapPost("/contracts/{id:guid}/funding-notices/check", (Guid id, IFundingService s, CancellationToken ct) => s.CheckNoticesAsync(id, ct));
    }
}

/// <summary>Body of <c>PUT /wbs/{id}/clin</c>; a null CLIN clears the mapping.</summary>
public sealed record SetWbsClinBody(Guid? ClinId);
