using System.Text.Json;
using Workbench.Infrastructure.Notifications;

namespace Workbench.Web.Notifications;

/// <summary>
/// The mail provider's bounce and complaint webhooks (docs/operations/email.md): <c>POST /webhooks/email/sendgrid</c>
/// (SendGrid's signed Event Webhook) and <c>POST /webhooks/email/ses</c> (SES events through an SNS topic). Anonymous,
/// so every request must carry a valid provider signature; an endpoint that isn't configured answers 404. In
/// MultiTenant mode they live on the base domain and route each event by its tenant tag.
/// </summary>
public static class EmailEventEndpoints
{
    public const string BasePath = "/webhooks/email";
    public const int MaxBodyBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWorkbenchEmailEvents(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup(BasePath).AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        hooks.MapPost("/sendgrid", SendGridAsync);
        hooks.MapPost("/ses", SesAsync);
        return app;
    }

    private static async Task<IResult> SendGridAsync(HttpContext http, EmailSettings settings, EmailEventRecorder recorder, TimeProvider clock, ILoggerFactory logs)
    {
        if (string.IsNullOrWhiteSpace(settings.SendGridWebhookVerificationKey))
        {
            return Plain(StatusCodes.Status404NotFound, "Not found.");
        }

        var body = await ReadBodyAsync(http);
        if (body is null)
        {
            return Plain(StatusCodes.Status413PayloadTooLarge, "Too large.");
        }

        if (!SendGridEventWebhook.VerifySignature(settings.SendGridWebhookVerificationKey,
                http.Request.Headers[SendGridEventWebhook.SignatureHeader], http.Request.Headers[SendGridEventWebhook.TimestampHeader], body))
        {
            logs.CreateLogger(typeof(EmailEventEndpoints)).LogWarning("Rejected a SendGrid event webhook call with a missing or invalid signature.");
            return Plain(StatusCodes.Status401Unauthorized, "Invalid signature.");
        }

        IReadOnlyList<TaggedEmailEvent> events;
        try
        {
            events = SendGridEventWebhook.Parse(body, clock.GetUtcNow().UtcDateTime);
        }
        catch (JsonException)
        {
            return Plain(StatusCodes.Status400BadRequest, "Expected a JSON array of events.");
        }

        await recorder.RecordAsync(events, http.RequestAborted);
        return Plain(StatusCodes.Status200OK, "OK");
    }

    private static async Task<IResult> SesAsync(
        HttpContext http, EmailSettings settings, SnsMessageVerifier verifier, EmailEventRecorder recorder, TimeProvider clock, IHttpClientFactory? clients, ILoggerFactory logs)
    {
        if (settings.SesBounceTopicArns.Count == 0)
        {
            return Plain(StatusCodes.Status404NotFound, "Not found.");
        }

        var logger = logs.CreateLogger(typeof(EmailEventEndpoints));
        var body = await ReadBodyAsync(http);
        if (body is null)
        {
            return Plain(StatusCodes.Status413PayloadTooLarge, "Too large.");
        }

        SnsMessage message;
        try
        {
            message = SnsMessage.Parse(body);
        }
        catch (JsonException)
        {
            return Plain(StatusCodes.Status400BadRequest, "Expected an SNS message.");
        }

        if (!settings.SesBounceTopicArns.Contains(message.TopicArn, StringComparer.Ordinal))
        {
            logger.LogWarning("Rejected an SNS message from a topic that isn't in Email:Ses:BounceTopicArns.");
            return Plain(StatusCodes.Status403Forbidden, "Unknown topic.");
        }

        if (!await verifier.VerifyAsync(message, http.RequestAborted))
        {
            logger.LogWarning("Rejected an SNS message with an invalid signature.");
            return Plain(StatusCodes.Status401Unauthorized, "Invalid signature.");
        }

        switch (message.Type)
        {
            case "SubscriptionConfirmation" when SnsMessageVerifier.IsSnsUrl(message.SubscribeUrl):
                // Visiting SubscribeURL confirms the subscription; the URL is SNS's own (checked above) and signed.
                using (var client = clients?.CreateClient(nameof(EmailEventEndpoints)) ?? new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    using var response = await client.GetAsync(message.SubscribeUrl, http.RequestAborted);
                    logger.LogInformation("Confirmed the SNS subscription to the bounce topic: {Status}.", (int)response.StatusCode);
                }

                return Plain(StatusCodes.Status200OK, "Subscribed.");
            case "Notification":
                IReadOnlyList<TaggedEmailEvent> events;
                try
                {
                    events = SesEventParser.Parse(message.Message, clock.GetUtcNow().UtcDateTime);
                }
                catch (JsonException)
                {
                    // Not an SES event (e.g. a test message published by hand): acknowledge it so SNS doesn't retry.
                    return Plain(StatusCodes.Status200OK, "Ignored.");
                }

                await recorder.RecordAsync(events, http.RequestAborted);
                return Plain(StatusCodes.Status200OK, "OK");
            default:
                return Plain(StatusCodes.Status200OK, "Ignored.");
        }
    }

    /// <summary>The raw body (signatures cover the exact bytes), or null when it's larger than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // A body keeps the status-code pages from turning the status into an HTML page.
    private static IResult Plain(int status, string text) => Results.Text(text, "text/plain; charset=utf-8", statusCode: status);
}
