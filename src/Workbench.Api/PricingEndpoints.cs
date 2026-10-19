using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Pricing;

namespace Workbench.Api;

internal static class PricingEndpoints
{
    public static void MapPricingEndpoints(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/pricing").WithTags("Proposal pricing");
        p.MapGet("/proposals", (IProposalPricingService s, CancellationToken ct) => s.ListProposalsAsync(ct));
        p.MapPost("/proposals", (SaveProposalRequest r, IProposalPricingService s, CancellationToken ct) => s.CreateProposalAsync(r, ct));
        p.MapGet("/proposals/{id:guid}", (Guid id, IProposalPricingService s, CancellationToken ct) => s.GetProposalAsync(id, ct));
        p.MapPut("/proposals/{id:guid}", (Guid id, SaveProposalRequest r, IProposalPricingService s, CancellationToken ct) => s.UpdateProposalAsync(id, r, ct));
        p.MapGet("/proposals/{id:guid}/compare", (Guid id, IProposalPricingService s, CancellationToken ct) => s.CompareAsync(id, ct));
        p.MapGet("/proposals/{id:guid}/history", (Guid id, IProposalPricingService s, CancellationToken ct) => s.ListHistoryAsync(id, ct));

        p.MapGet("/models/{id:guid}", (Guid id, IProposalPricingService s, CancellationToken ct) => s.GetModelAsync(id, ct));
        p.MapPut("/models/{id:guid}/settings", (Guid id, SavePriceSettingsRequest r, IProposalPricingService s, CancellationToken ct) => s.SaveSettingsAsync(id, r, ct));
        p.MapPut("/models/{id:guid}/periods", (Guid id, List<SavePricePeriodRequest> r, IProposalPricingService s, CancellationToken ct) => s.SavePeriodsAsync(id, r, ct));
        p.MapPut("/models/{id:guid}/clins", (Guid id, List<SavePriceClinRequest> r, IProposalPricingService s, CancellationToken ct) => s.SaveClinsAsync(id, r, ct));
        p.MapPut("/models/{id:guid}/wbs", (Guid id, List<SavePriceWbsRequest> r, IProposalPricingService s, CancellationToken ct) => s.SaveWbsAsync(id, r, ct));
        p.MapPut("/models/{id:guid}/escalations", (Guid id, List<PriceEscalationDto> r, IProposalPricingService s, CancellationToken ct) => s.SaveEscalationsAsync(id, r, ct));
        p.MapPut("/models/{id:guid}/labor", (Guid id, List<SavePriceLaborLineRequest> r, IProposalPricingService s, CancellationToken ct) => s.SaveLaborLinesAsync(id, r, ct));
        p.MapPut("/models/{id:guid}/costs", (Guid id, List<SavePriceCostLineRequest> r, IProposalPricingService s, CancellationToken ct) => s.SaveCostLinesAsync(id, r, ct));
        p.MapGet("/models/{id:guid}/result", (Guid id, IProposalPricingService s, CancellationToken ct) => s.CalculateAsync(id, ct));
        p.MapPost("/models/{id:guid}/copy", (Guid id, CopyPriceModelRequest r, IProposalPricingService s, CancellationToken ct) => s.CopyModelAsync(id, r, ct));
        p.MapPost("/models/{id:guid}/lock", (Guid id, LockPriceModelRequest r, IProposalPricingService s, CancellationToken ct) => s.LockModelAsync(id, r, ct));
        p.MapDelete("/models/{id:guid}", async (Guid id, IProposalPricingService s, CancellationToken ct) =>
        {
            await s.DeleteModelAsync(id, ct);
            return Results.NoContent();
        });
        p.MapPost("/models/{id:guid}/handoff", (Guid id, HandoffRequest r, IProposalPricingService s, CancellationToken ct) => s.HandOffAsync(id, r, ct));

        // JSON-wrapped for the desktop client; the .xlsx route serves the file to browsers.
        p.MapGet("/models/{id:guid}/export", (Guid id, IProposalPricingService s, CancellationToken ct) => s.ExportCostVolumeAsync(id, ct));
        p.MapGet("/models/{id:guid}/cost-volume.xlsx", async (Guid id, IProposalPricingService s, CancellationToken ct) =>
        {
            var file = await s.ExportCostVolumeAsync(id, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        p.MapGet("/options", (DateOnly? asOf, IProposalPricingService s, CancellationToken ct) => s.GetOptionsAsync(asOf, ct));
        p.MapGet("/opportunities", (string? search, IPricingOpportunityLookup s, CancellationToken ct) => s.SearchAsync(search, ct));
        p.MapGet("/opportunities/{id:guid}", async (Guid id, IPricingOpportunityLookup s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } o ? Results.Ok(o) : Results.NoContent());
    }
}
