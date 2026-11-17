using System.Text;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Stripe;

namespace Workbench.LicenseServer.Endpoints;

public static class StripeEndpoints
{
    public static void MapStripeEndpoints(this WebApplication app)
    {
        app.MapPost("/stripe/webhook", async (HttpRequest request, StripeWebhookHandler handler, IOptions<StripeOptions> options, TimeProvider clock, ILogger<StripeWebhookHandler> log) =>
        {
            var secret = options.Value.WebhookSecret;
            if (string.IsNullOrEmpty(secret)) return Results.Problem("Stripe is not configured (Stripe:WebhookSecret).", statusCode: 503);

            // The signature is over the exact bytes Stripe sent, so read the raw body.
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var payload = await reader.ReadToEndAsync();
            if (!StripeSignature.Verify(payload, request.Headers["Stripe-Signature"].FirstOrDefault(), secret, clock.GetUtcNow(), options.Value.ToleranceSeconds))
                return Results.Problem("Bad Stripe signature.", statusCode: 400);

            try
            {
                var outcome = await handler.HandleAsync(payload);
                return Results.Ok(new { received = true, outcome });
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                // A payload we can't use is a 400; Stripe retries, and the failure is in the log. Anything else bubbles up as 500 (Stripe retries too).
                log.LogError(ex, "Could not handle a Stripe event");
                return Results.Problem("Could not handle the event.", statusCode: 400);
            }
        });
    }
}
