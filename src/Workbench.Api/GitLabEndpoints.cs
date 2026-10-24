using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Integrations.GitLab;

namespace Workbench.Api;

/// <summary>
/// The GitLab sync over HTTP: the organization's connection (administrators) and each project's link and sync panel
/// (project setup; reading needs the project's plan). Under <c>/integrations</c>, so API keys never reach it. GitLab's own
/// webhook calls go to <c>/webhooks/gitlab/{linkId}</c> in the web host.
/// </summary>
internal static class GitLabEndpoints
{
    public static void MapGitLabEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/integrations/gitlab").WithTags("GitLab");

        g.MapGet("/connection", (IGitLabConnectionService s, CancellationToken ct) => s.GetAsync(ct));
        g.MapPut("/connection", (SaveGitLabConnectionRequest r, IGitLabConnectionService s, CancellationToken ct) => s.SaveAsync(r, ct));
        g.MapPost("/connection/test", (SaveGitLabConnectionRequest? r, IGitLabConnectionService s, CancellationToken ct) => s.TestAsync(r, ct));
        g.MapDelete("/connection", async (IGitLabConnectionService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(ct);
            return Results.NoContent();
        });
        g.MapGet("/people", (IGitLabConnectionService s, CancellationToken ct) => s.ListPeopleAsync(ct));
        g.MapPost("/people", (MatchGitLabPersonRequest r, IGitLabConnectionService s, CancellationToken ct) => s.MatchPersonAsync(r, ct));
        g.MapDelete("/people/{email}", async (string email, IGitLabConnectionService s, CancellationToken ct) =>
        {
            await s.UnmatchPersonAsync(email, ct);
            return Results.NoContent();
        });
        g.MapGet("/users", (string q, IGitLabConnectionService s, CancellationToken ct) => s.SearchUsersAsync(q, ct));

        var p = g.MapGroup("/projects/{projectId:guid}");
        p.MapGet("", (Guid projectId, IGitLabProjectSyncService s, CancellationToken ct) => s.GetAsync(projectId, ct));
        p.MapGet("/gitlab-projects", (Guid projectId, string? q, IGitLabProjectSyncService s, CancellationToken ct) => s.SearchGitLabProjectsAsync(projectId, q, ct));
        p.MapPost("/link", (Guid projectId, LinkGitLabProjectRequest r, IGitLabProjectSyncService s, CancellationToken ct) => s.LinkAsync(projectId, r, ct));
        p.MapDelete("/link", async (Guid projectId, IGitLabProjectSyncService s, CancellationToken ct) =>
        {
            await s.UnlinkAsync(projectId, ct);
            return Results.NoContent();
        });
        p.MapPost("/sync", (Guid projectId, IGitLabProjectSyncService s, CancellationToken ct) => s.SyncNowAsync(projectId, ct));
        p.MapGet("/webhook", (Guid projectId, IGitLabProjectSyncService s, CancellationToken ct) => s.GetWebhookSetupAsync(projectId, ct));
        p.MapPost("/conflicts/{conflictId:guid}", (Guid projectId, Guid conflictId, ResolveGitLabConflictRequest r, IGitLabProjectSyncService s, CancellationToken ct) =>
            s.ResolveConflictAsync(projectId, conflictId, r, ct));
        p.MapPost("/pending/{pendingId:guid}/approve", (Guid projectId, Guid pendingId, IGitLabProjectSyncService s, CancellationToken ct) =>
            s.DecidePendingAsync(projectId, pendingId, true, ct));
        p.MapPost("/pending/{pendingId:guid}/reject", (Guid projectId, Guid pendingId, IGitLabProjectSyncService s, CancellationToken ct) =>
            s.DecidePendingAsync(projectId, pendingId, false, ct));
        p.MapGet("/users", (Guid projectId, string q, IGitLabProjectSyncService s, CancellationToken ct) => s.SearchUsersAsync(projectId, q, ct));
        p.MapPost("/people", (Guid projectId, MatchGitLabPersonRequest r, IGitLabProjectSyncService s, CancellationToken ct) => s.MatchPersonAsync(projectId, r, ct));

        g.MapGet("/sheets/{sheetId:guid}/items/{itemId:guid}", async (Guid sheetId, Guid itemId, IGitLabProjectSyncService s, CancellationToken ct) =>
            await s.GetTaskStatusAsync(sheetId, itemId, ct) is { } status ? Results.Ok(status) : Results.NoContent());
    }
}
