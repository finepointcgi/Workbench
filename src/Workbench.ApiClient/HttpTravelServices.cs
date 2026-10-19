using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Travel;

namespace Workbench.ApiClient;

public sealed class HttpTravelAuthorizationService(HttpClient http) : ITravelAuthorizationService
{
    private const string Base = "api/v1/travel";

    public Task<IReadOnlyList<TravelAuthorizationDto>> ListMineAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/authorizations/mine", cancellationToken).ReadListAsync<TravelAuthorizationDto>(cancellationToken);

    public Task<IReadOnlyList<TravelAuthorizationDto>> ListAwaitingMeAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/authorizations/awaiting-me", cancellationToken).ReadListAsync<TravelAuthorizationDto>(cancellationToken);

    public Task<IReadOnlyList<TravelAuthorizationDto>> ListAllAsync(TravelAuthorizationQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new TravelAuthorizationQuery();
        var url = $"{Base}/authorizations?take={query.Take}"
            + (query.Status is { } s ? $"&status={(int)s}" : "")
            + (query.EmployeeId is { } e ? $"&employeeId={e}" : "")
            + (query.ProjectId is { } p ? $"&projectId={p}" : "");
        return http.GetAsync(url, cancellationToken).ReadListAsync<TravelAuthorizationDto>(cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> GetAsync(Guid authorizationId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/authorizations/{authorizationId}", cancellationToken)).ReadTravelAsync<TravelAuthorizationDetailDto>(cancellationToken);

    public async Task<TravelEstimateDto> EstimateAsync(EstimateTravelRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/estimate", request, cancellationToken)).ReadTravelAsync<TravelEstimateDto>(cancellationToken);

    public async Task<TravelAuthorizationDetailDto> CreateAsync(SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/authorizations", request, cancellationToken)).ReadTravelAsync<TravelAuthorizationDetailDto>(cancellationToken);

    public async Task<TravelAuthorizationDetailDto> UpdateAsync(Guid authorizationId, SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/authorizations/{authorizationId}", request, cancellationToken)).ReadTravelAsync<TravelAuthorizationDetailDto>(cancellationToken);

    public async Task DeleteAsync(Guid authorizationId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/authorizations/{authorizationId}", cancellationToken)).EnsureTravelOkAsync(cancellationToken);

    public Task<TravelAuthorizationDetailDto> SubmitAsync(Guid authorizationId, CancellationToken cancellationToken = default) =>
        PostAsync<TravelAuthorizationDetailDto>($"{Base}/authorizations/{authorizationId}/submit", new { }, cancellationToken);

    public Task<TravelAuthorizationDetailDto> ApproveAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<TravelAuthorizationDetailDto>($"{Base}/authorizations/{authorizationId}/approve", request, cancellationToken);

    public Task<TravelAuthorizationDetailDto> RejectAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<TravelAuthorizationDetailDto>($"{Base}/authorizations/{authorizationId}/reject", request, cancellationToken);

    public Task<TravelAuthorizationDetailDto> CancelAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<TravelAuthorizationDetailDto>($"{Base}/authorizations/{authorizationId}/cancel", request, cancellationToken);

    public Task<ExpenseReportDto> StartExpenseReportAsync(Guid authorizationId, CancellationToken cancellationToken = default) =>
        PostAsync<ExpenseReportDto>($"{Base}/authorizations/{authorizationId}/expense-report", new { }, cancellationToken);

    public async Task<TravelAuthorizationDetailDto> LinkExpenseReportAsync(Guid authorizationId, LinkExpenseReportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/authorizations/{authorizationId}/expense-report", request, cancellationToken)).ReadTravelAsync<TravelAuthorizationDetailDto>(cancellationToken);

    public async Task<TravelComparisonDto?> GetComparisonForReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Base}/expense-reports/{expenseReportId}/comparison", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadTravelAsync<TravelComparisonDto>(cancellationToken);
    }

    public async Task<TravelComparisonDto> SetLineCategoryAsync(Guid expenseReportId, Guid lineId, SetTravelLineCategoryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/expense-reports/{expenseReportId}/lines/{lineId}/category", request, cancellationToken)).ReadTravelAsync<TravelComparisonDto>(cancellationToken);

    public Task<TravelAuthorizationDetailDto> RecordAdvanceAsync(Guid authorizationId, RecordTravelAdvanceRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<TravelAuthorizationDetailDto>($"{Base}/authorizations/{authorizationId}/advance", request, cancellationToken);

    public async Task<TravelPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/policy", cancellationToken)).ReadTravelAsync<TravelPolicyDto>(cancellationToken);

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct) =>
        await (await http.PostAsJsonAsync(url, body, ct)).ReadTravelAsync<T>(ct);
}

public sealed class HttpCardProgramService(HttpClient http) : ICardProgramService
{
    private const string Base = "api/v1/cards";

    public Task<IReadOnlyList<CardProgramDto>> ListProgramsAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/programs", cancellationToken).ReadListAsync<CardProgramDto>(cancellationToken);

    public async Task<CardProgramDto> CreateProgramAsync(SaveCardProgramRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/programs", request, cancellationToken)).ReadTravelAsync<CardProgramDto>(cancellationToken);

    public async Task<CardProgramDto> UpdateProgramAsync(Guid programId, SaveCardProgramRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/programs/{programId}", request, cancellationToken)).ReadTravelAsync<CardProgramDto>(cancellationToken);

    public Task<IReadOnlyList<CorporateCardDto>> ListCardsAsync(Guid? programId = null, CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/cards" + (programId is { } p ? $"?programId={p}" : ""), cancellationToken).ReadListAsync<CorporateCardDto>(cancellationToken);

    public async Task<CorporateCardDto> CreateCardAsync(SaveCorporateCardRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/cards", request, cancellationToken)).ReadTravelAsync<CorporateCardDto>(cancellationToken);

    public async Task<CorporateCardDto> UpdateCardAsync(Guid cardId, SaveCorporateCardRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/cards/{cardId}", request, cancellationToken)).ReadTravelAsync<CorporateCardDto>(cancellationToken);

    public async Task<CardImportResultDto> ImportStatementAsync(Guid programId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "application/octet-stream");
        form.Add(file, "file", fileName);
        return await (await http.PostAsync($"{Base}/programs/{programId}/statements", form, cancellationToken)).ReadTravelAsync<CardImportResultDto>(cancellationToken);
    }

    public Task<IReadOnlyList<CardStatementImportDto>> ListImportsAsync(Guid? programId = null, CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/statements" + (programId is { } p ? $"?programId={p}" : ""), cancellationToken).ReadListAsync<CardStatementImportDto>(cancellationToken);

    public Task<IReadOnlyList<CardTransactionDto>> ListTransactionsAsync(CardTransactionQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new CardTransactionQuery();
        var url = $"{Base}/transactions?take={query.Take}&unmatched={(query.UnmatchedOnly ? "true" : "false")}"
            + (query.ProgramId is { } p ? $"&programId={p}" : "")
            + (query.State is { } s ? $"&state={(int)s}" : "")
            + (query.EmployeeId is { } e ? $"&employeeId={e}" : "")
            + (query.ImportId is { } i ? $"&importId={i}" : "");
        return http.GetAsync(url, cancellationToken).ReadListAsync<CardTransactionDto>(cancellationToken);
    }

    public async Task<CardAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/aging" + (asOf is { } d ? $"?asOf={d:yyyy-MM-dd}" : ""), cancellationToken)).ReadTravelAsync<CardAgingDto>(cancellationToken);

    public Task<IReadOnlyList<CardReconciliationDto>> GetReconciliationAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/reconciliation", cancellationToken).ReadListAsync<CardReconciliationDto>(cancellationToken);

    public async Task<CardTransactionDto> ExcludeTransactionAsync(Guid transactionId, ExcludeCardTransactionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/transactions/{transactionId}/exclude", request, cancellationToken)).ReadTravelAsync<CardTransactionDto>(cancellationToken);

    public async Task<CardTransactionDto> RestoreTransactionAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/transactions/{transactionId}/restore", new { }, cancellationToken)).ReadTravelAsync<CardTransactionDto>(cancellationToken);

    public async Task<CardTransactionDto> AssignCardAsync(Guid transactionId, AssignCardRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/transactions/{transactionId}/card", request, cancellationToken)).ReadTravelAsync<CardTransactionDto>(cancellationToken);
}

public sealed class HttpCardChargeService(HttpClient http) : ICardChargeService
{
    private const string Base = "api/v1/cards";

    public Task<IReadOnlyList<CorporateCardDto>> ListMyCardsAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/mine", cancellationToken).ReadListAsync<CorporateCardDto>(cancellationToken);

    public Task<IReadOnlyList<CardTransactionDto>> ListMyChargesAsync(bool includeAssigned = false, CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/mine/charges?includeAssigned={(includeAssigned ? "true" : "false")}", cancellationToken).ReadListAsync<CardTransactionDto>(cancellationToken);

    public async Task<CardChargeSummaryDto> GetMySummaryAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/mine/summary", cancellationToken)).ReadTravelAsync<CardChargeSummaryDto>(cancellationToken);

    public async Task<OdcEntryDto> AttachToReportAsync(Guid transactionId, AttachCardChargeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/mine/charges/{transactionId}/attach", request, cancellationToken)).ReadTravelAsync<OdcEntryDto>(cancellationToken);

    public async Task DetachAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/mine/charges/{transactionId}/detach", new { }, cancellationToken)).EnsureTravelOkAsync(cancellationToken);

    public Task<IReadOnlyList<CardLineDto>> ListReportCardLinesAsync(Guid expenseReportId, CancellationToken cancellationToken = default) =>
        http.GetAsync($"{Base}/expense-reports/{expenseReportId}/lines", cancellationToken).ReadListAsync<CardLineDto>(cancellationToken);
}

public static class TravelClientRegistration
{
    public static IServiceCollection AddTravelClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ITravelAuthorizationService, HttpTravelAuthorizationService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICardProgramService, HttpCardProgramService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICardChargeService, HttpCardChargeService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

internal static class TravelResponses
{
    /// <summary>Like <c>EnsureOkAsync</c>, plus 409 (wrong state) → <see cref="InvalidOperationException"/>, as in-process.</summary>
    public static async Task EnsureTravelOkAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        await response.EnsureOkAsync(cancellationToken);
    }

    public static async Task<T> ReadTravelAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.EnsureTravelOkAsync(cancellationToken);
        return await response.ReadAsync<T>(cancellationToken);
    }
}
