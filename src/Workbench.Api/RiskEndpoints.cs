using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Risks;

namespace Workbench.Api;

internal static class RiskEndpoints
{
    public static void MapRiskEndpoints(this RouteGroupBuilder v1)
    {
        var risks = v1.MapGroup("/risks").WithTags("Risks");
        risks.MapGet("/projects/{projectId:guid}", (Guid projectId, bool? includeClosed, IRiskService s, CancellationToken ct) => s.GetRegisterAsync(projectId, includeClosed ?? false, ct));
        risks.MapPost("/projects/{projectId:guid}", (Guid projectId, SaveRiskRequest r, IRiskService s, CancellationToken ct) => s.CreateAsync(projectId, r, ct));
        risks.MapGet("/{riskId:guid}", (Guid riskId, IRiskService s, CancellationToken ct) => s.GetAsync(riskId, ct));
        risks.MapPut("/{riskId:guid}", (Guid riskId, SaveRiskRequest r, IRiskService s, CancellationToken ct) => s.UpdateAsync(riskId, r, ct));
        risks.MapDelete("/{riskId:guid}", async (Guid riskId, IRiskService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(riskId, ct);
            return Results.NoContent();
        });
    }
}
