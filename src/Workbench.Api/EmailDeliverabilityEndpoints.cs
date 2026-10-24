using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Notifications;

namespace Workbench.Api;

internal static class EmailDeliverabilityEndpoints
{
    public static void MapEmailDeliverabilityEndpoints(this RouteGroupBuilder v1)
    {
        var email = v1.MapGroup("/email-issues").WithTags("Email delivery");
        email.MapGet("/", (bool? includeCleared, IEmailDeliverabilityService s, CancellationToken ct) => s.ListAsync(includeCleared ?? false, ct));
        email.MapGet("/mine", async (IEmailDeliverabilityService s, CancellationToken ct) =>
            await s.GetMineAsync(ct) is { } issue ? Results.Ok(issue) : Results.NoContent());
        email.MapPost("/clear", async (ClearEmailIssueRequest request, IEmailDeliverabilityService s, CancellationToken ct) =>
        {
            await s.ClearAsync(request.Address, ct);
            return Results.NoContent();
        });
    }
}

public sealed record ClearEmailIssueRequest(string Address);
