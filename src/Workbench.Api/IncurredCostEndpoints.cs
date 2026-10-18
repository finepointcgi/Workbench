using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.IncurredCost;

namespace Workbench.Api;

internal static class IncurredCostEndpoints
{
    public static void MapIncurredCostEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/incurred-cost").WithTags("Incurred cost submission").AddEndpointFilter(MapConflicts);

        g.MapGet("", (IIncurredCostService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("", (CreateIncurredCostRequest r, IIncurredCostService s, CancellationToken ct) => s.CreateAsync(r, ct));
        g.MapGet("/{id:guid}", (Guid id, IIncurredCostService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapPost("/{id:guid}/regenerate", (Guid id, IIncurredCostService s, CancellationToken ct) => s.RegenerateAsync(id, ct));
        g.MapPut("/{id:guid}/inputs", (Guid id, SaveIceInputsRequest r, IIncurredCostService s, CancellationToken ct) => s.SaveInputsAsync(id, r, ct));
        g.MapPost("/{id:guid}/review", (Guid id, IIncurredCostService s, CancellationToken ct) => s.MarkReviewedAsync(id, ct));
        g.MapPost("/{id:guid}/reopen", (Guid id, ReopenIncurredCostRequest r, IIncurredCostService s, CancellationToken ct) => s.ReopenAsync(id, r, ct));
        g.MapPost("/{id:guid}/certify", (Guid id, CertifyIncurredCostRequest r, IIncurredCostService s, CancellationToken ct) => s.CertifyAsync(id, r, ct));
        g.MapPost("/{id:guid}/submit", (Guid id, SubmitIncurredCostRequest r, IIncurredCostService s, CancellationToken ct) => s.SubmitAsync(id, r, ct));
        g.MapGet("/{id:guid}/export", (Guid id, IIncurredCostService s, CancellationToken ct) => s.ExportWorkbookAsync(id, ct));
    }

    /// <summary>Wrong workflow state (locked, not reviewed, already exists) → 409.</summary>
    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
