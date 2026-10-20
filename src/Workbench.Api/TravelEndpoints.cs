using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Travel;

namespace Workbench.Api;

internal static class TravelEndpoints
{
    public static void MapTravelEndpoints(this RouteGroupBuilder v1)
    {
        var travel = v1.MapGroup("/travel").WithTags("Travel").AddEndpointFilter(MapStateErrors);
        travel.MapGet("/authorizations/mine", (ITravelAuthorizationService s, CancellationToken ct) => s.ListMineAsync(ct));
        travel.MapGet("/authorizations/awaiting-me", (ITravelAuthorizationService s, CancellationToken ct) => s.ListAwaitingMeAsync(ct));
        travel.MapGet("/authorizations", (TravelAuthorizationState? status, Guid? employeeId, Guid? projectId, int? take, ITravelAuthorizationService s, CancellationToken ct) =>
            s.ListAllAsync(new TravelAuthorizationQuery(status, employeeId, projectId, take ?? 500), ct));
        travel.MapPost("/authorizations", (SaveTravelAuthorizationRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.CreateAsync(r, ct));
        travel.MapPost("/estimate", (EstimateTravelRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.EstimateAsync(r, ct));
        travel.MapGet("/policy", (ITravelAuthorizationService s, CancellationToken ct) => s.GetPolicyAsync(ct));
        travel.MapGet("/authorizations/{id:guid}", (Guid id, ITravelAuthorizationService s, CancellationToken ct) => s.GetAsync(id, ct));
        travel.MapPut("/authorizations/{id:guid}", (Guid id, SaveTravelAuthorizationRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        travel.MapDelete("/authorizations/{id:guid}", async (Guid id, ITravelAuthorizationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        travel.MapPost("/authorizations/{id:guid}/submit", (Guid id, ITravelAuthorizationService s, CancellationToken ct) => s.SubmitAsync(id, ct));
        travel.MapPost("/authorizations/{id:guid}/approve", (Guid id, TravelDecisionRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        travel.MapPost("/authorizations/{id:guid}/reject", (Guid id, TravelDecisionRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        travel.MapPost("/authorizations/{id:guid}/cancel", (Guid id, TravelDecisionRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.CancelAsync(id, r, ct));
        travel.MapPost("/authorizations/{id:guid}/expense-report", (Guid id, ITravelAuthorizationService s, CancellationToken ct) => s.StartExpenseReportAsync(id, ct));
        travel.MapPut("/authorizations/{id:guid}/expense-report", (Guid id, LinkExpenseReportRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.LinkExpenseReportAsync(id, r, ct));
        travel.MapPost("/authorizations/{id:guid}/advance", (Guid id, RecordTravelAdvanceRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.RecordAdvanceAsync(id, r, ct));
        travel.MapGet("/expense-reports/{reportId:guid}/comparison", async (Guid reportId, ITravelAuthorizationService s, CancellationToken ct) =>
            await s.GetComparisonForReportAsync(reportId, ct) is { } c ? Results.Ok(c) : Results.NoContent());
        travel.MapPut("/expense-reports/{reportId:guid}/lines/{lineId:guid}/category",
            (Guid reportId, Guid lineId, SetTravelLineCategoryRequest r, ITravelAuthorizationService s, CancellationToken ct) => s.SetLineCategoryAsync(reportId, lineId, r, ct));

        var cards = v1.MapGroup("/cards").WithTags("Corporate cards").AddEndpointFilter(MapStateErrors);
        cards.MapGet("/programs", (ICardProgramService s, CancellationToken ct) => s.ListProgramsAsync(ct));
        cards.MapPost("/programs", (SaveCardProgramRequest r, ICardProgramService s, CancellationToken ct) => s.CreateProgramAsync(r, ct));
        cards.MapPut("/programs/{id:guid}", (Guid id, SaveCardProgramRequest r, ICardProgramService s, CancellationToken ct) => s.UpdateProgramAsync(id, r, ct));
        cards.MapGet("/cards", (Guid? programId, ICardProgramService s, CancellationToken ct) => s.ListCardsAsync(programId, ct));
        cards.MapPost("/cards", (SaveCorporateCardRequest r, ICardProgramService s, CancellationToken ct) => s.CreateCardAsync(r, ct));
        cards.MapPut("/cards/{id:guid}", (Guid id, SaveCorporateCardRequest r, ICardProgramService s, CancellationToken ct) => s.UpdateCardAsync(id, r, ct));
        cards.MapPost("/programs/{id:guid}/statements", async (Guid id, HttpRequest http, ICardProgramService s, CancellationToken ct) =>
            {
                var file = http.HasFormContentType ? (await http.ReadFormAsync(ct)).Files.GetFile("file") : null;
                if (file is null)
                {
                    return Results.Problem("Send the statement as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);
                }

                await using var stream = file.OpenReadStream();
                return Results.Ok(await s.ImportStatementAsync(id, file.FileName, stream, ct));
            })
            .DisableAntiforgery();
        cards.MapGet("/statements", (Guid? programId, ICardProgramService s, CancellationToken ct) => s.ListImportsAsync(programId, ct));
        cards.MapGet("/transactions", (Guid? programId, CardChargeState? state, Guid? employeeId, Guid? importId, bool? unmatched, int? take, ICardProgramService s, CancellationToken ct) =>
            s.ListTransactionsAsync(new CardTransactionQuery(programId, state, employeeId, importId, unmatched ?? false, take ?? 500), ct));
        cards.MapGet("/aging", (DateOnly? asOf, ICardProgramService s, CancellationToken ct) => s.GetAgingAsync(asOf, ct));
        cards.MapGet("/reconciliation", (ICardProgramService s, CancellationToken ct) => s.GetReconciliationAsync(ct));
        cards.MapPost("/transactions/{id:guid}/exclude", (Guid id, ExcludeCardTransactionRequest r, ICardProgramService s, CancellationToken ct) => s.ExcludeTransactionAsync(id, r, ct));
        cards.MapPost("/transactions/{id:guid}/restore", (Guid id, ICardProgramService s, CancellationToken ct) => s.RestoreTransactionAsync(id, ct));
        cards.MapPost("/transactions/{id:guid}/card", (Guid id, AssignCardRequest r, ICardProgramService s, CancellationToken ct) => s.AssignCardAsync(id, r, ct));

        cards.MapGet("/mine", (ICardChargeService s, CancellationToken ct) => s.ListMyCardsAsync(ct));
        cards.MapGet("/mine/charges", (bool? includeAssigned, ICardChargeService s, CancellationToken ct) => s.ListMyChargesAsync(includeAssigned ?? false, ct));
        cards.MapGet("/mine/summary", (ICardChargeService s, CancellationToken ct) => s.GetMySummaryAsync(ct));
        cards.MapPost("/mine/charges/{id:guid}/attach", (Guid id, AttachCardChargeRequest r, ICardChargeService s, CancellationToken ct) => s.AttachToReportAsync(id, r, ct));
        cards.MapPost("/mine/charges/{id:guid}/detach", async (Guid id, ICardChargeService s, CancellationToken ct) =>
        {
            await s.DetachAsync(id, ct);
            return Results.NoContent();
        });
        cards.MapGet("/expense-reports/{reportId:guid}/lines", (Guid reportId, ICardChargeService s, CancellationToken ct) => s.ListReportCardLinesAsync(reportId, ct));
    }

    /// <summary>Wrong state (locked, already decided) → 409; the expense report's own "not yours" → 403.</summary>
    private static async ValueTask<object?> MapStateErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
