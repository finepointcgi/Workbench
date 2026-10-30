using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Integrations.Jira;

namespace Workbench.Api;

/// <summary>
/// The Jira sync over HTTP: the organization's connection (administrators) and each project's link and sync panel (project
/// setup; reading needs the project's plan). Under <c>/integrations</c>, so API keys never reach it. Jira's own webhook calls
/// go to <c>/webhooks/jira/{linkId}</c> in the web host.
/// </summary>
internal static class JiraEndpoints
{
    public static void MapJiraEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/integrations/jira").WithTags("Jira");

        g.MapGet("/connection", (IJiraConnectionService s, CancellationToken ct) => s.GetAsync(ct));
        g.MapPut("/connection", (SaveJiraConnectionRequest r, IJiraConnectionService s, CancellationToken ct) => s.SaveAsync(r, ct));
        g.MapPost("/connection/test", (SaveJiraConnectionRequest? r, IJiraConnectionService s, CancellationToken ct) => s.TestAsync(r, ct));
        g.MapDelete("/connection", async (IJiraConnectionService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(ct);
            return Results.NoContent();
        });

        var p = g.MapGroup("/projects/{projectId:guid}");
        p.MapGet("", (Guid projectId, IJiraProjectSyncService s, CancellationToken ct) => s.GetAsync(projectId, ct));
        p.MapGet("/jira-projects", (Guid projectId, string? q, IJiraProjectSyncService s, CancellationToken ct) => s.SearchJiraProjectsAsync(projectId, q, ct));
        p.MapPost("/link", (Guid projectId, LinkJiraProjectRequest r, IJiraProjectSyncService s, CancellationToken ct) => s.LinkAsync(projectId, r, ct));
        p.MapPut("/link", (Guid projectId, UpdateJiraLinkRequest r, IJiraProjectSyncService s, CancellationToken ct) => s.UpdateLinkAsync(projectId, r, ct));
        p.MapDelete("/link", async (Guid projectId, IJiraProjectSyncService s, CancellationToken ct) =>
        {
            await s.UnlinkAsync(projectId, ct);
            return Results.NoContent();
        });
        p.MapPost("/sync", (Guid projectId, IJiraProjectSyncService s, CancellationToken ct) => s.SyncNowAsync(projectId, ct));
        p.MapGet("/webhook", (Guid projectId, IJiraProjectSyncService s, CancellationToken ct) => s.GetWebhookSetupAsync(projectId, ct));
        p.MapPost("/notes/{noteId:guid}/dismiss", (Guid projectId, Guid noteId, IJiraProjectSyncService s, CancellationToken ct) => s.DismissNoteAsync(projectId, noteId, ct));

        g.MapGet("/sheets/{sheetId:guid}/items/{itemId:guid}", async (Guid sheetId, Guid itemId, IJiraProjectSyncService s, CancellationToken ct) =>
            await s.GetTaskStatusAsync(sheetId, itemId, ct) is { } status ? Results.Ok(status) : Results.NoContent());
    }
}
