using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Cash;

namespace Workbench.Api;

internal static class CashEndpoints
{
    public static void MapCashEndpoints(this RouteGroupBuilder v1)
    {
        var c = v1.MapGroup("/cash").WithTags("Cash").AddEndpointFilter(MapCashErrors);

        // Bank accounts
        c.MapGet("/accounts", (bool? includeInactive, IBankAccountService s, CancellationToken ct) => s.ListAsync(includeInactive ?? true, ct));
        c.MapGet("/accounts/gl-accounts", (IBankAccountService s, CancellationToken ct) => s.ListCashGlAccountsAsync(ct));
        c.MapGet("/accounts/{id:guid}", (Guid id, IBankAccountService s, CancellationToken ct) => s.GetAsync(id, ct));
        c.MapPost("/accounts", (SaveBankAccountRequest r, IBankAccountService s, CancellationToken ct) => s.CreateAsync(r, ct));
        c.MapPut("/accounts/{id:guid}", (Guid id, SaveBankAccountRequest r, IBankAccountService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        c.MapPost("/accounts/{id:guid}/reveal", (Guid id, IBankAccountService s, CancellationToken ct) => s.RevealAccountNumberAsync(id, ct));
        c.MapPut("/accounts/{id:guid}/csv-mapping", (Guid id, CsvColumnMapping r, IBankStatementService s, CancellationToken ct) => s.SaveCsvMappingAsync(id, r, ct));

        // Statements
        c.MapPost("/statements", (ImportStatementRequest r, IBankStatementService s, CancellationToken ct) => s.ImportAsync(r, ct));
        c.MapGet("/statements", (Guid? bankAccountId, IBankStatementService s, CancellationToken ct) => s.ListImportsAsync(bankAccountId, ct));
        c.MapGet("/accounts/{id:guid}/items", (Guid id, DateOnly? from, DateOnly? to, IBankStatementService s, CancellationToken ct) => s.ListItemsAsync(id, from, to, ct));

        // Reconciliation
        c.MapGet("/reconciliations", (Guid? bankAccountId, IBankReconciliationService s, CancellationToken ct) => s.ListAsync(bankAccountId, ct));
        c.MapGet("/reconciliations/defaults/{bankAccountId:guid}", (Guid bankAccountId, IBankReconciliationService s, CancellationToken ct) => s.GetDefaultsAsync(bankAccountId, ct));
        c.MapPost("/reconciliations", (StartReconciliationRequest r, IBankReconciliationService s, CancellationToken ct) => s.StartAsync(r, ct));
        c.MapPut("/reconciliations/{id:guid}", (Guid id, UpdateReconciliationRequest r, IBankReconciliationService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        c.MapGet("/reconciliations/{id:guid}/workspace", (Guid id, IBankReconciliationService s, CancellationToken ct) => s.GetWorkspaceAsync(id, ct));
        c.MapGet("/reconciliations/{id:guid}/report", (Guid id, IBankReconciliationService s, CancellationToken ct) => s.GetReportAsync(id, ct));
        c.MapPost("/reconciliations/{id:guid}/complete", (Guid id, CompleteReconciliationRequest r, IBankReconciliationService s, CancellationToken ct) => s.CompleteAsync(id, r, ct));
        c.MapPost("/reconciliations/{id:guid}/reopen", (Guid id, ReconciliationReasonRequest r, IBankReconciliationService s, CancellationToken ct) => s.ReopenAsync(id, r, ct));
        c.MapPost("/matches/auto", (AutoMatchRequest r, IBankReconciliationService s, CancellationToken ct) => s.AutoMatchAsync(r, ct));
        c.MapPost("/matches", (MatchItemsRequest r, IBankReconciliationService s, CancellationToken ct) => s.MatchAsync(r, ct));
        c.MapDelete("/matches/{id:guid}", async (Guid id, IBankReconciliationService s, CancellationToken ct) =>
        {
            await s.UnmatchAsync(id, ct);
            return Results.NoContent();
        });
        c.MapGet("/booking-accounts", (IBankReconciliationService s, CancellationToken ct) => s.GetBookingAccountsAsync(ct));
        c.MapPost("/bank-items/book", (BookBankItemRequest r, IBankReconciliationService s, CancellationToken ct) => s.BookBankItemAsync(r, ct));
        c.MapGet("/close-waivers", (Guid? fiscalPeriodId, IBankReconciliationService s, CancellationToken ct) => s.ListCloseWaiversAsync(fiscalPeriodId, ct));
        c.MapPost("/close-waivers", (WaiveBankCloseCheckRequest r, IBankReconciliationService s, CancellationToken ct) => s.WaiveCloseCheckAsync(r, ct));

        // Position and forecast
        c.MapGet("/position", (ICashForecastService s, CancellationToken ct) => s.GetPositionAsync(ct));
        c.MapGet("/forecast", (int? weeks, ICashForecastService s, CancellationToken ct) => s.GetForecastAsync(weeks ?? 13, ct));
        c.MapGet("/forecast/items", (ICashForecastService s, CancellationToken ct) => s.ListItemsAsync(ct));
        c.MapPost("/forecast/items", (SaveForecastItemRequest r, ICashForecastService s, CancellationToken ct) => s.CreateItemAsync(r, ct));
        c.MapPut("/forecast/items/{id:guid}", (Guid id, SaveForecastItemRequest r, ICashForecastService s, CancellationToken ct) => s.UpdateItemAsync(id, r, ct));
        c.MapGet("/settings", (ICashForecastService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        c.MapPut("/settings", (CashSettingsDto r, ICashForecastService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct));
    }

    private static async ValueTask<object?> MapCashErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
