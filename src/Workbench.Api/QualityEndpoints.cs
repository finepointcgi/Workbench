using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Quality;

namespace Workbench.Api;

internal static class QualityEndpoints
{
    public static void MapQualityEndpoints(this RouteGroupBuilder v1)
    {
        var q = v1.MapGroup("/quality").WithTags("Quality").AddEndpointFilter(MapConflicts);

        // Inspection plans, the inspection queue and defect codes
        q.MapGet("/plans", (Guid? partId, bool? activeOnly, IInspectionService s, CancellationToken ct) => s.ListPlansAsync(partId, activeOnly ?? false, ct));
        q.MapGet("/plans/{id:guid}", (Guid id, IInspectionService s, CancellationToken ct) => s.GetPlanAsync(id, ct));
        q.MapPost("/plans", (SaveInspectionPlanRequest r, IInspectionService s, CancellationToken ct) => s.SavePlanAsync(null, r, ct));
        q.MapPut("/plans/{id:guid}", (Guid id, SaveInspectionPlanRequest r, IInspectionService s, CancellationToken ct) => s.SavePlanAsync(id, r, ct));
        q.MapGet("/inspections", (InspectionState? status, bool? all, InspectionType? kind, Guid? partId, Guid? vendorId, int? take, IInspectionService s, CancellationToken ct) =>
            s.ListAsync(new InspectionQuery(all == true ? null : status ?? InspectionState.Pending, kind, partId, vendorId, take ?? 200), ct));
        q.MapGet("/inspections/{id:guid}", (Guid id, IInspectionService s, CancellationToken ct) => s.GetAsync(id, ct));
        q.MapPost("/inspections/{id:guid}/complete", (Guid id, CompleteInspectionRequest r, IInspectionService s, CancellationToken ct) => s.CompleteAsync(id, r, ct));
        q.MapGet("/defect-codes", (bool? activeOnly, IInspectionService s, CancellationToken ct) => s.ListDefectCodesAsync(activeOnly ?? true, ct));
        q.MapPost("/defect-codes", (SaveDefectCodeRequest r, IInspectionService s, CancellationToken ct) => s.SaveDefectCodeAsync(null, r, ct));
        q.MapPut("/defect-codes/{id:guid}", (Guid id, SaveDefectCodeRequest r, IInspectionService s, CancellationToken ct) => s.SaveDefectCodeAsync(id, r, ct));

        // Nonconformances, dispositions and debit memos
        q.MapGet("/ncrs", (NcrState? status, Guid? vendorId, Guid? partId, bool? openOnly, int? take, INonconformanceService s, CancellationToken ct) =>
            s.ListAsync(new NcrQuery(status, vendorId, partId, openOnly ?? false, take ?? 200), ct));
        q.MapGet("/ncrs/waiting-on-me", (INonconformanceService s, CancellationToken ct) => s.ListWaitingOnMeAsync(ct));
        q.MapGet("/ncrs/{id:guid}", (Guid id, INonconformanceService s, CancellationToken ct) => s.GetAsync(id, ct));
        q.MapPost("/ncrs", (CreateNcrRequest r, INonconformanceService s, CancellationToken ct) => s.CreateAsync(r, ct));
        q.MapPost("/ncrs/{id:guid}/disposition", (Guid id, ProposeDispositionRequest r, INonconformanceService s, CancellationToken ct) => s.ProposeDispositionAsync(id, r, ct));
        q.MapPost("/ncrs/{id:guid}/approve", (Guid id, DecideNcrRequest r, INonconformanceService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        q.MapPost("/ncrs/{id:guid}/reject", (Guid id, DecideNcrRequest r, INonconformanceService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        q.MapPost("/ncrs/{id:guid}/withdraw", (Guid id, INonconformanceService s, CancellationToken ct) => s.WithdrawDispositionAsync(id, ct));
        q.MapGet("/debit-memos", (Guid? vendorId, bool? openOnly, INonconformanceService s, CancellationToken ct) => s.ListDebitMemosAsync(vendorId, openOnly ?? false, ct));
        q.MapPost("/debit-memos/{id:guid}/apply", (Guid id, ApplyDebitMemoRequest r, INonconformanceService s, CancellationToken ct) => s.ApplyDebitMemoAsync(id, r, ct));

        // Corrective actions and supplier quality
        q.MapGet("/cars", (CarState? status, Guid? vendorId, bool? openOnly, ICorrectiveActionService s, CancellationToken ct) =>
            s.ListAsync(new CarQuery(status, vendorId, openOnly ?? false), ct));
        q.MapGet("/cars/{id:guid}", (Guid id, ICorrectiveActionService s, CancellationToken ct) => s.GetAsync(id, ct));
        q.MapPost("/cars", (SaveCarRequest r, ICorrectiveActionService s, CancellationToken ct) => s.SaveAsync(null, r, ct));
        q.MapPut("/cars/{id:guid}", (Guid id, SaveCarRequest r, ICorrectiveActionService s, CancellationToken ct) => s.SaveAsync(id, r, ct));
        q.MapPost("/cars/{id:guid}/implemented", (Guid id, CompleteCarRequest r, ICorrectiveActionService s, CancellationToken ct) => s.MarkImplementedAsync(id, r, ct));
        q.MapPost("/cars/{id:guid}/verify", (Guid id, VerifyCarRequest r, ICorrectiveActionService s, CancellationToken ct) => s.VerifyAsync(id, r, ct));
        q.MapPost("/cars/{id:guid}/cancel", (Guid id, ICorrectiveActionService s, CancellationToken ct) => s.CancelAsync(id, ct));
        q.MapGet("/supplier-quality", (DateOnly? from, DateOnly? to, ICorrectiveActionService s, CancellationToken ct) => s.SupplierQualityAsync(from, to, ct));

        // Files on inspections, NCRs and corrective actions
        q.MapPost("/files/{owner}/{ownerId:guid}", async (QualityFileOwner owner, Guid ownerId, HttpRequest http, IQualityFileService s, CancellationToken ct) =>
            {
                var form = http.HasFormContentType ? await http.ReadFormAsync(ct) : null;
                var file = form?.Files.GetFile("file");
                if (file is null)
                {
                    return Results.Problem("Send the file as multipart/form-data in a field named \"file\" (and what it is in \"kind\").", statusCode: StatusCodes.Status400BadRequest);
                }

                if (file.Length > AttachmentRules.MaxSizeBytes)
                {
                    throw new ArgumentException($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
                }

                await using var stream = file.OpenReadStream();
                return Results.Ok(await s.AttachAsync(owner, ownerId, form!["kind"].ToString(), file.FileName, stream, ct));
            })
            .DisableAntiforgery();
        q.MapGet("/files/{id:guid}", async (Guid id, IQualityFileService s, CancellationToken ct) =>
        {
            var file = await s.OpenAsync(id, ct);

            // Always a download, never rendered inline.
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        // Engineering changes, revision history and configuration
        var e = v1.MapGroup("/engineering-changes").WithTags("Engineering changes").AddEndpointFilter(MapConflicts);
        e.MapGet("", (EcoState? status, Guid? partId, bool? openOnly, IEngineeringChangeService s, CancellationToken ct) => s.ListAsync(new EcoQuery(status, partId, openOnly ?? false), ct));
        e.MapGet("/waiting-on-me", (IEngineeringChangeService s, CancellationToken ct) => s.ListWaitingOnMeAsync(ct));
        e.MapGet("/work-order-reviews", (IEngineeringChangeService s, CancellationToken ct) => s.ListOpenWorkOrderReviewsAsync(ct));
        e.MapPost("/work-order-reviews/{id:guid}/reviewed", (Guid id, ReviewWorkOrderRequest r, IEngineeringChangeService s, CancellationToken ct) => s.MarkWorkOrderReviewedAsync(id, r, ct));
        e.MapPost("/apply-due", async (DateOnly? asOf, IEngineeringChangeService s, CancellationToken ct) => Results.Ok(new { Applied = await s.ApplyDueAsync(asOf, ct) }));
        e.MapGet("/parts/{partId:guid}/history", (Guid partId, IEngineeringChangeService s, CancellationToken ct) => s.RevisionHistoryAsync(partId, ct));
        e.MapGet("/parts/{partId:guid}/units", (Guid partId, IEngineeringChangeService s, CancellationToken ct) => s.ListUnitsAsync(partId, ct));
        e.MapGet("/parts/{partId:guid}/configuration", (Guid partId, string? serial, string? lot, IEngineeringChangeService s, CancellationToken ct) => s.ConfigurationAsync(partId, serial, lot, ct));
        e.MapGet("/{id:guid}", (Guid id, IEngineeringChangeService s, CancellationToken ct) => s.GetAsync(id, ct));
        e.MapPost("", (SaveEcoRequest r, IEngineeringChangeService s, CancellationToken ct) => s.SaveAsync(null, r, ct));
        e.MapPut("/{id:guid}", (Guid id, SaveEcoRequest r, IEngineeringChangeService s, CancellationToken ct) => s.SaveAsync(id, r, ct));
        e.MapPost("/{id:guid}/submit", (Guid id, IEngineeringChangeService s, CancellationToken ct) => s.SubmitAsync(id, ct));
        e.MapPost("/{id:guid}/approve", (Guid id, DecideEcoRequest r, IEngineeringChangeService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        e.MapPost("/{id:guid}/reject", (Guid id, DecideEcoRequest r, IEngineeringChangeService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        e.MapPost("/{id:guid}/cancel", (Guid id, IEngineeringChangeService s, CancellationToken ct) => s.CancelAsync(id, ct));
    }

    /// <summary>Wrong state (inspection already done, NCR not waiting for approval) → 409.</summary>
    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
