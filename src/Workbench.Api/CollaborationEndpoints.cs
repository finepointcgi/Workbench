using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Collaboration;

namespace Workbench.Api;

internal static class CollaborationEndpoints
{
    public static void MapCollaborationEndpoints(this RouteGroupBuilder v1)
    {
        var sheet = v1.MapGroup("/sheets/{sheetId:guid}").AddEndpointFilter(MapForbidden);

        var comments = sheet.MapGroup("").WithTags("Comments");
        comments.MapGet("/items/{itemId:guid}/comments", (Guid sheetId, Guid itemId, ICommentService service, CancellationToken ct) =>
            service.ListAsync(sheetId, itemId, ct));

        comments.MapPost("/items/{itemId:guid}/comments", async (Guid sheetId, Guid itemId, CreateCommentRequest request, ICommentService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(sheetId, itemId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/comments/{created.Id}", created);
        });

        comments.MapPut("/comments/{commentId:guid}", (Guid sheetId, Guid commentId, UpdateCommentRequest request, ICommentService service, CancellationToken ct) =>
            service.UpdateAsync(sheetId, commentId, request, ct));

        comments.MapDelete("/comments/{commentId:guid}", async (Guid sheetId, Guid commentId, ICommentService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sheetId, commentId, ct);
            return Results.NoContent();
        });

        var attachments = sheet.MapGroup("").WithTags("Attachments");
        attachments.MapGet("/items/{itemId:guid}/attachments", (Guid sheetId, Guid itemId, IAttachmentService service, CancellationToken ct) =>
            service.ListAsync(sheetId, itemId, ct));

        // Multipart upload with a single "file" field. Called by the desktop app with no browser cookies,
        // so antiforgery doesn't apply (revisit when cookie auth is added).
        attachments.MapPost("/items/{itemId:guid}/attachments", async (Guid sheetId, Guid itemId, HttpRequest http, IAttachmentService service, CancellationToken ct) =>
            {
                if (!http.HasFormContentType)
                {
                    return Results.Problem("Send the file as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);
                }

                var form = await http.ReadFormAsync(ct);
                if (form.Files.GetFile("file") is not { } file)
                {
                    return Results.Problem("Send the file as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);
                }

                if (file.Length > AttachmentRules.MaxSizeBytes)
                {
                    return Results.Problem($"Attachments can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status400BadRequest);
                }

                await using var stream = file.OpenReadStream();
                var created = await service.UploadAsync(sheetId, itemId, file.FileName, stream, ct);
                return Results.Created($"/api/v1/sheets/{sheetId}/attachments/{created.Id}/content", created);
            })
            .DisableAntiforgery();

        attachments.MapGet("/attachments/{attachmentId:guid}/content", async (Guid sheetId, Guid attachmentId, IAttachmentService service, CancellationToken ct) =>
        {
            var content = await service.DownloadAsync(sheetId, attachmentId, ct);

            // Always a download, never rendered inline, so an uploaded file can't run script on this origin.
            return Results.File(content.Content, content.Attachment.ContentType, content.Attachment.FileName);
        });

        attachments.MapDelete("/attachments/{attachmentId:guid}", async (Guid sheetId, Guid attachmentId, IAttachmentService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sheetId, attachmentId, ct);
            return Results.NoContent();
        });

        sheet.MapGet("/activity", (Guid sheetId, Guid? itemId, int? limit, IActivityService service, CancellationToken ct) =>
            service.ListAsync(sheetId, itemId, limit ?? 100, ct)).WithTags("Activity");
    }

    private static async ValueTask<object?> MapForbidden(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
