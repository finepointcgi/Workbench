using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Currencies;

namespace Workbench.Api;

internal static class CurrencyEndpoints
{
    public static void MapCurrencyEndpoints(this RouteGroupBuilder v1)
    {
        var c = v1.MapGroup("/currencies").WithTags("Currencies").AddEndpointFilter(MapConflicts);

        c.MapGet("", (bool? includeInactive, ICurrencyService s, CancellationToken ct) => s.ListCurrenciesAsync(includeInactive ?? false, ct));
        c.MapPut("", (SaveCurrencyRequest r, ICurrencyService s, CancellationToken ct) => s.SaveCurrencyAsync(r, ct));
        c.MapGet("/rates", (string? currency, RateKind? rateType, DateOnly? from, DateOnly? to, int? take, ICurrencyService s, CancellationToken ct) =>
            s.ListRatesAsync(new ExchangeRateQuery(currency, rateType, from, to, take ?? 500), ct));
        c.MapPost("/rates", (SaveExchangeRateRequest r, ICurrencyService s, CancellationToken ct) => s.SaveRateAsync(r, ct));
        c.MapGet("/rates/lookup", (string from, string to, DateOnly date, RateKind? rateType, ICurrencyService s, CancellationToken ct) =>
            s.GetRateAsync(from, to, date, rateType ?? RateKind.Spot, ct));
        c.MapPost("/rates/import", (ImportExchangeRatesRequest r, ICurrencyService s, CancellationToken ct) => s.ImportRatesAsync(r, ct));
        c.MapGet("/rate-providers", (ICurrencyService s, CancellationToken ct) => s.ListRateProvidersAsync(ct));
        c.MapPost("/rates/fetch", (FetchExchangeRatesRequest r, ICurrencyService s, CancellationToken ct) => s.FetchRatesAsync(r, ct));
        c.MapGet("/settings", (ICurrencyService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        c.MapPut("/settings", (SaveCurrencySettingsRequest r, ICurrencyService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct));
        c.MapGet("/parties", (ICurrencyService s, CancellationToken ct) => s.ListPartyCurrenciesAsync(ct));
        c.MapPut("/parties", (SetPartyCurrencyRequest r, ICurrencyService s, CancellationToken ct) => s.SetPartyCurrencyAsync(r, ct));
        c.MapPut("/invoices/{id:guid}", async (Guid id, SetInvoiceCurrencyRequest r, ICurrencyService s, CancellationToken ct) =>
        {
            await s.SetInvoiceCurrencyAsync(id, r, ct);
            return Results.NoContent();
        });

        c.MapGet("/revaluations", (IRevaluationService s, CancellationToken ct) => s.ListAsync(ct));
        c.MapGet("/revaluations/{fiscalPeriodId:guid}", (Guid fiscalPeriodId, IRevaluationService s, CancellationToken ct) => s.PreviewAsync(fiscalPeriodId, ct));
        c.MapPost("/revaluations/{fiscalPeriodId:guid}", (Guid fiscalPeriodId, IRevaluationService s, CancellationToken ct) => s.PostAsync(fiscalPeriodId, ct));
        c.MapGet("/report", (DateOnly asOf, DateOnly? from, IRevaluationService s, CancellationToken ct) => s.GetReportAsync(asOf, from, ct));
    }

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
