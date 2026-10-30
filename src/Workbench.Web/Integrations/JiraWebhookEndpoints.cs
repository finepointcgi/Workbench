using System.Text;
using Workbench.Application.Integrations.Jira;

namespace Workbench.Web.Integrations;

/// <summary>
/// Jira's webhook calls for a linked project: <c>POST /webhooks/jira/{linkId}</c> on the tenant's own address. Anonymous, so
/// every call must carry a signature or token made from the link's secret (<see cref="JiraWebhookReceiver"/>); replays are ignored.
/// </summary>
public static class JiraWebhookEndpoints
{
    public const string BasePath = "/webhooks/jira";

    public static IEndpointRouteBuilder MapWorkbenchJiraWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost(BasePath + "/{linkId:guid}", ReceiveAsync).AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> ReceiveAsync(Guid linkId, HttpContext http, JiraWebhookReceiver receiver)
    {
        var body = await ReadBodyAsync(http);
        if (body is null)
        {
            return Results.Text("Too large.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var headers = http.Request.Headers;
        var outcome = await receiver.ReceiveAsync(linkId, headers[JiraWebhookReceiver.SignatureHeader].FirstOrDefault(), headers[JiraWebhookReceiver.TokenHeader].FirstOrDefault(),
            headers[JiraWebhookReceiver.IdentifierHeader].FirstOrDefault(), body, http.RequestAborted);
        return Results.Text(outcome.Message, "text/plain; charset=utf-8", statusCode: outcome.StatusCode);
    }

    private static async Task<string?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > JiraWebhookReceiver.MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > JiraWebhookReceiver.MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
