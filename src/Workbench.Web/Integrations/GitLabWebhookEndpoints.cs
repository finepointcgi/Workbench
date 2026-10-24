using System.Text;
using Workbench.Application.Integrations.GitLab;

namespace Workbench.Web.Integrations;

/// <summary>
/// GitLab's webhook calls for a linked project: <c>POST /webhooks/gitlab/{linkId}</c> on the tenant's own address. Anonymous,
/// so every call must carry the link's secret in <c>X-Gitlab-Token</c> (<see cref="GitLabWebhookReceiver"/>); replays are ignored.
/// </summary>
public static class GitLabWebhookEndpoints
{
    public const string BasePath = "/webhooks/gitlab";

    public static IEndpointRouteBuilder MapWorkbenchGitLabWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost(BasePath + "/{linkId:guid}", ReceiveAsync).AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> ReceiveAsync(Guid linkId, HttpContext http, GitLabWebhookReceiver receiver)
    {
        var body = await ReadBodyAsync(http);
        if (body is null)
        {
            return Plain(StatusCodes.Status413PayloadTooLarge, "Too large.");
        }

        var headers = http.Request.Headers;
        var outcome = await receiver.ReceiveAsync(linkId, headers[GitLabWebhookReceiver.TokenHeader].FirstOrDefault(),
            headers[GitLabWebhookReceiver.IdempotencyHeader].FirstOrDefault(), headers[GitLabWebhookReceiver.EventUuidHeader].FirstOrDefault(), body, http.RequestAborted);
        return Plain(outcome.StatusCode, outcome.Message);
    }

    private static async Task<string?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > GitLabWebhookReceiver.MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > GitLabWebhookReceiver.MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IResult Plain(int status, string text) => Results.Text(text, "text/plain; charset=utf-8", statusCode: status);
}
