using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;

namespace Workbench.Api;

internal static class AccountingLiveEndpoints
{
    public static void MapAccountingLiveEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/accounting-live").WithTags("Accounting export").AddEndpointFilter(MapConflicts);
        g.MapGet("/connections", (IAccountingConnectionService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/connections/{target}/start", (AccountingTarget target, IAccountingConnectionService s, CancellationToken ct) => s.StartAsync(target, ct));
        g.MapPost("/connections/callback", (AccountingOAuthCallback c, IAccountingConnectionService s, CancellationToken ct) => s.CompleteAsync(c, ct));
        g.MapPost("/connections/{target}/organisation/{organisationId}", (AccountingTarget target, string organisationId, IAccountingConnectionService s, CancellationToken ct) => s.ChooseOrganisationAsync(target, organisationId, ct));
        g.MapDelete("/connections/{target}", async (AccountingTarget target, IAccountingConnectionService s, CancellationToken ct) =>
        {
            await s.DisconnectAsync(target, ct);
            return Results.NoContent();
        });
        g.MapPost("/preview", (AccountingPushRequest r, IAccountingPushService s, CancellationToken ct) => s.PreviewAsync(r, ct));
        g.MapPost("/push", (AccountingPushRequest r, IAccountingPushService s, CancellationToken ct) => s.PushAsync(r, ct));
        g.MapPost("/runs/{runId:guid}/retry", (Guid runId, bool? createMissingContacts, IAccountingPushService s, CancellationToken ct) => s.RetryFailedAsync(runId, createMissingContacts ?? false, ct));
        g.MapGet("/runs", (AccountingTarget? target, int? limit, IAccountingPushService s, CancellationToken ct) => s.ListRunsAsync(target, limit ?? 50, ct));
        g.MapGet("/runs/{runId:guid}", (Guid runId, IAccountingPushService s, CancellationToken ct) => s.GetRunAsync(runId, ct));
    }

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
