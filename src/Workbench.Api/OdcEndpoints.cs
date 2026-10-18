using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;

namespace Workbench.Api;

internal static class OdcEndpoints
{
    public static void MapOdcEndpoints(this RouteGroupBuilder v1)
    {
        var odcs = v1.MapGroup("").WithTags("ODCs").AddEndpointFilter(MapWorkflowErrors);
        odcs.MapGet("/projects/{projectId:guid}/odcs", (Guid projectId, DateOnly? from, DateOnly? to, OdcCostType? type, OdcApprovalState? status, bool? includeExpenses,
            IOdcService s, CancellationToken ct) => s.ListProjectOdcsAsync(projectId, new OdcListQuery(from, to, type, status, includeExpenses ?? false), ct));
        odcs.MapPost("/projects/{projectId:guid}/odcs", (Guid projectId, SaveVendorOdcRequest r, IOdcService s, CancellationToken ct) => s.CreateVendorOdcAsync(projectId, r, ct));
        odcs.MapGet("/odcs/policy", (IOdcService s, CancellationToken ct) => s.GetPolicyAsync(ct));
        odcs.MapGet("/odcs/{id:guid}", async (Guid id, IOdcService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } o ? Results.Ok(o) : Results.NotFound());
        odcs.MapPut("/odcs/{id:guid}", (Guid id, SaveVendorOdcRequest r, IOdcService s, CancellationToken ct) => s.UpdateVendorOdcAsync(id, r, ct));
        odcs.MapDelete("/odcs/{id:guid}", async (Guid id, IOdcService s, CancellationToken ct) =>
        {
            await s.DeleteVendorOdcAsync(id, ct);
            return Results.NoContent();
        });
        odcs.MapPost("/odcs/{id:guid}/submit", (Guid id, IOdcService s, CancellationToken ct) => s.SubmitAsync(id, ct));

        // Multipart upload with a single "file" field, like sheet attachments.
        odcs.MapPost("/odcs/{id:guid}/receipt", async (Guid id, HttpRequest http, IOdcService s, CancellationToken ct) =>
            await ReadFileAsync(http, ct) is { } file
                ? Results.Ok(await UploadAsync(file, stream => s.AttachReceiptAsync(id, file.FileName, stream, ct)))
                : MissingFile())
            .DisableAntiforgery();
        odcs.MapGet("/odcs/{id:guid}/receipt", async (Guid id, IOdcService s, CancellationToken ct) =>
        {
            var receipt = await s.OpenReceiptAsync(id, ct);

            // Always a download, never rendered inline, so an uploaded file can't run script on this origin.
            return Results.File(receipt.Content, receipt.ContentType, receipt.FileName);
        });

        var expenses = v1.MapGroup("/expense-reports").WithTags("Expense reports").AddEndpointFilter(MapWorkflowErrors);
        expenses.MapGet("/mine", (IExpenseReportService s, CancellationToken ct) => s.ListMyReportsAsync(ct));
        expenses.MapGet("/{id:guid}", (Guid id, IExpenseReportService s, CancellationToken ct) => s.GetReportAsync(id, ct));
        expenses.MapPost("", (SaveExpenseReportRequest r, IExpenseReportService s, CancellationToken ct) => s.CreateReportAsync(r, ct));
        expenses.MapPut("/{id:guid}", (Guid id, SaveExpenseReportRequest r, IExpenseReportService s, CancellationToken ct) => s.UpdateReportAsync(id, r, ct));
        expenses.MapDelete("/{id:guid}", async (Guid id, IExpenseReportService s, CancellationToken ct) =>
        {
            await s.DeleteReportAsync(id, ct);
            return Results.NoContent();
        });
        expenses.MapPost("/{id:guid}/lines", (Guid id, SaveExpenseLineRequest r, IExpenseReportService s, CancellationToken ct) => s.AddLineAsync(id, r, ct));
        expenses.MapPut("/{id:guid}/lines/{lineId:guid}", (Guid id, Guid lineId, SaveExpenseLineRequest r, IExpenseReportService s, CancellationToken ct) =>
            s.UpdateLineAsync(id, lineId, r, ct));
        expenses.MapDelete("/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, IExpenseReportService s, CancellationToken ct) =>
        {
            await s.RemoveLineAsync(id, lineId, ct);
            return Results.NoContent();
        });
        expenses.MapPost("/{id:guid}/lines/{lineId:guid}/receipt", async (Guid id, Guid lineId, HttpRequest http, IExpenseReportService s, CancellationToken ct) =>
            await ReadFileAsync(http, ct) is { } file
                ? Results.Ok(await UploadAsync(file, stream => s.AttachReceiptAsync(id, lineId, file.FileName, stream, ct)))
                : MissingFile())
            .DisableAntiforgery();
        expenses.MapPost("/{id:guid}/submit", (Guid id, IExpenseReportService s, CancellationToken ct) => s.SubmitAsync(id, ct));

        var approvals = v1.MapGroup("/odc-approvals").WithTags("ODC approvals").AddEndpointFilter(MapWorkflowErrors);
        approvals.MapGet("/queue", (IOdcApprovalService s, CancellationToken ct) => s.GetMyQueueAsync(ct));
        approvals.MapGet("/approved", (IOdcApprovalService s, CancellationToken ct) => s.ListMyApprovedAsync(ct));
        approvals.MapPost("/reports/{id:guid}/approve", (Guid id, OdcDecisionRequest r, IOdcApprovalService s, CancellationToken ct) => s.ApproveReportAsync(id, r.Reason, ct));
        approvals.MapPost("/reports/{id:guid}/reject", (Guid id, OdcDecisionRequest r, IOdcApprovalService s, CancellationToken ct) => s.RejectReportAsync(id, r.Reason ?? "", ct));
        approvals.MapPost("/reports/{id:guid}/reopen", (Guid id, OdcDecisionRequest r, IOdcApprovalService s, CancellationToken ct) => s.ReopenReportAsync(id, r.Reason ?? "", ct));
        approvals.MapPost("/odcs/{id:guid}/approve", (Guid id, OdcDecisionRequest r, IOdcApprovalService s, CancellationToken ct) => s.ApproveOdcAsync(id, r.Reason, ct));
        approvals.MapPost("/odcs/{id:guid}/reject", (Guid id, OdcDecisionRequest r, IOdcApprovalService s, CancellationToken ct) => s.RejectOdcAsync(id, r.Reason ?? "", ct));
        approvals.MapPost("/odcs/{id:guid}/reopen", (Guid id, OdcDecisionRequest r, IOdcApprovalService s, CancellationToken ct) => s.ReopenOdcAsync(id, r.Reason ?? "", ct));
        approvals.MapPut("/odcs/{id:guid}/unallowable", (Guid id, SetUnallowableRequest r, IOdcApprovalService s, CancellationToken ct) =>
            s.SetUnallowableAsync(id, r.IsUnallowable, r.Reason, ct));
    }

    private static async Task<IFormFile?> ReadFileAsync(HttpRequest http, CancellationToken ct) =>
        http.HasFormContentType ? (await http.ReadFormAsync(ct)).Files.GetFile("file") : null;

    private static async Task<OdcEntryDto> UploadAsync(IFormFile file, Func<Stream, Task<OdcEntryDto>> upload)
    {
        if (file.Length > AttachmentRules.MaxSizeBytes)
        {
            throw new ArgumentException($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
        }

        await using var stream = file.OpenReadStream();
        return await upload(stream);
    }

    private static IResult MissingFile() =>
        Results.Problem("Send the file as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Not your item (or your own submission) → 403; wrong state (locked, already decided) → 409.</summary>
    private static async ValueTask<object?> MapWorkflowErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
