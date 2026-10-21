using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Receipts;

namespace Workbench.Api;

internal static class ReceiptEndpoints
{
    public static void MapReceiptEndpoints(this RouteGroupBuilder v1)
    {
        var receipts = v1.MapGroup("/receipts").WithTags("Receipts").AddEndpointFilter(MapErrors);
        receipts.MapGet("/mine", (string? search, DateOnly? from, DateOnly? to, decimal? minAmount, decimal? maxAmount, bool? unattached, int? take,
            IReceiptLibraryService s, CancellationToken ct) =>
            s.ListMineAsync(new ReceiptQuery(search, from, to, minAmount, maxAmount, unattached ?? false, take ?? 200), ct));
        receipts.MapGet("/{id:guid}", (Guid id, IReceiptLibraryService s, CancellationToken ct) => s.GetAsync(id, ct));

        // Multipart upload with a single "file" field, like expense receipts. Returns what was read from it.
        receipts.MapPost("", async (Guid? expenseReportId, HttpRequest http, IReceiptLibraryService s, CancellationToken ct) =>
        {
            var file = http.HasFormContentType ? (await http.ReadFormAsync(ct)).Files.GetFile("file") : null;
            if (file is null)
            {
                return Results.Problem("Send the file as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > AttachmentRules.MaxSizeBytes)
            {
                throw new ArgumentException($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
            }

            await using var stream = file.OpenReadStream();
            return Results.Ok(await s.UploadAsync(file.FileName, stream, expenseReportId, ct));
        }).DisableAntiforgery();
        receipts.MapGet("/{id:guid}/reading", (Guid id, Guid? expenseReportId, IReceiptLibraryService s, CancellationToken ct) => s.ReadAsync(id, expenseReportId, ct));
        receipts.MapPost("/lodging-check", (LodgingRateCheckRequest r, IReceiptLibraryService s, CancellationToken ct) => s.CheckLodgingAsync(r, ct));
        receipts.MapPut("/{id:guid}", (Guid id, UpdateReceiptRequest r, IReceiptLibraryService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        receipts.MapDelete("/{id:guid}", async (Guid id, IReceiptLibraryService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        receipts.MapGet("/{id:guid}/content", async (Guid id, IReceiptLibraryService s, CancellationToken ct) =>
        {
            var receipt = await s.OpenAsync(id, ct);

            // Always a download, never rendered inline, so an uploaded file can't run script on this origin.
            return Results.File(receipt.Content, receipt.ContentType, receipt.FileName);
        });
    }

    /// <summary>A file still in use → 409 (the group filter already maps not-found, forbidden and invalid input).</summary>
    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
