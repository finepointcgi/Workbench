using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Support;

namespace Workbench.Api;

internal static class SupportEndpoints
{
    public static void MapSupportEndpoints(this RouteGroupBuilder v1)
    {
        var support = v1.MapGroup("/support").WithTags("Support");
        support.MapGet("/requests/mine", (ISupportService s, CancellationToken ct) => s.ListMineAsync(ct));
        support.MapGet("/requests", (SupportStatus? status, ISupportService s, CancellationToken ct) => s.ListAllAsync(status, ct));
        support.MapGet("/requests/{id:guid}", (Guid id, ISupportService s, CancellationToken ct) => s.GetAsync(id, ct));
        support.MapPut("/requests/{id:guid}", (Guid id, UpdateSupportRequest request, ISupportService s, CancellationToken ct) => s.UpdateAsync(id, request, ct));

        // Multipart: a "request" field with the JSON SubmitSupportRequest and an optional "screenshot" image.
        support.MapPost("/requests", async (HttpRequest http, ISupportService s, CancellationToken ct) =>
            {
                if (!http.HasFormContentType)
                {
                    return Results.Problem("Send multipart/form-data with a \"request\" field (JSON) and an optional \"screenshot\" file.", statusCode: StatusCodes.Status400BadRequest);
                }

                var form = await http.ReadFormAsync(ct);
                var request = System.Text.Json.JsonSerializer.Deserialize<SubmitSupportRequest>(form["request"].ToString(), System.Text.Json.JsonSerializerOptions.Web);
                if (request is null)
                {
                    return Results.Problem("The \"request\" field is missing.", statusCode: StatusCodes.Status400BadRequest);
                }

                SupportRequestDto created;
                if (form.Files.GetFile("screenshot") is { } file)
                {
                    if (file.Length > SupportRules.MaxScreenshotBytes)
                    {
                        return Results.Problem($"A screenshot can be at most {SupportRules.MaxScreenshotBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status400BadRequest);
                    }

                    await using var stream = file.OpenReadStream();
                    created = await s.SubmitAsync(request, new SupportScreenshot(file.FileName, stream), ct);
                }
                else
                {
                    created = await s.SubmitAsync(request, null, ct);
                }

                return Results.Created($"/api/v1/support/requests/{created.Id}", created);
            })
            .DisableAntiforgery();

        support.MapGet("/requests/{id:guid}/screenshot", async (Guid id, ISupportService s, CancellationToken ct) =>
        {
            var content = await s.OpenScreenshotAsync(id, ct);

            // Always a download with the server's own content type, never rendered inline from the uploader's.
            return Results.File(content.Content, content.ContentType, content.FileName);
        });

        v1.MapGet("/host/support", (SupportStatus? status, IHostSupportService s, CancellationToken ct) => s.ListAsync(status, ct)).WithTags("Support");

        var help = v1.MapGroup("/help/state").WithTags("Help");
        help.MapGet("", (IHelpStateService s, CancellationToken ct) => s.GetAsync(ct));
        help.MapPost("/tours/{tourKey}", (string tourKey, IHelpStateService s, CancellationToken ct) => s.CompleteTourAsync(tourKey, ct));
        help.MapDelete("/tours", (IHelpStateService s, CancellationToken ct) => s.RestartToursAsync(ct));
        help.MapPost("/whats-new/{version}", (string version, IHelpStateService s, CancellationToken ct) => s.MarkWhatsNewSeenAsync(version, ct));
    }
}
