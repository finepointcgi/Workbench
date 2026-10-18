using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Financials;

namespace Workbench.ApiClient;

public sealed class HttpBudgetService(HttpClient http) : IBudgetService
{
    public async Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/projects/{projectId}/budgets", cancellationToken).ReadListAsync<BudgetVersionDto>(cancellationToken);

    public Task<BudgetVersionDetailDto?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<BudgetVersionDetailDto>($"api/v1/budgets/{versionId}", cancellationToken);

    public async Task<BudgetVersionDetailDto> CreateVersionAsync(Guid projectId, CreateBudgetVersionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/budgets", request, cancellationToken)).ReadAsync<BudgetVersionDetailDto>(cancellationToken);

    public async Task<BudgetVersionDetailDto> SaveLinesAsync(Guid versionId, IReadOnlyList<SaveBudgetLineRequest> lines, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/budgets/{versionId}/lines", lines, cancellationToken)).ReadAsync<BudgetVersionDetailDto>(cancellationToken);

    public async Task<BudgetVersionDto> LockBaselineAsync(Guid versionId, string? reason = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/budgets/{versionId}/lock", new LockBudgetRequest(reason), cancellationToken)).ReadAsync<BudgetVersionDto>(cancellationToken);

    public async Task<BudgetVersionDto> PromoteToCurrentAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/budgets/{versionId}/promote", null, cancellationToken)).ReadAsync<BudgetVersionDto>(cancellationToken);

    public async Task<BudgetVersionDetailDto> RepriceAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/budgets/{versionId}/reprice", null, cancellationToken)).ReadAsync<BudgetVersionDetailDto>(cancellationToken);
}

public sealed class HttpProjectFinancialsService(HttpClient http) : IProjectFinancialsService
{
    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(FinancialsUrls.With($"api/v1/projects/{projectId}/financials/summary", budgetVersionId, forecastVersionId), cancellationToken))
            .ReadAsync<ProjectFinancialSummaryDto>(cancellationToken);

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default) =>
        await http.GetAsync(FinancialsUrls.With($"api/v1/projects/{projectId}/financials/wbs", budgetVersionId, forecastVersionId), cancellationToken)
            .ReadListAsync<WbsFinancialRowDto>(cancellationToken);

    public async Task<TimePhasedBurnDto> GetTimePhasedBurnAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(FinancialsUrls.With($"api/v1/projects/{projectId}/financials/time-phased", budgetVersionId, null), cancellationToken))
            .ReadAsync<TimePhasedBurnDto>(cancellationToken);

    public async Task<ContractFundingDto> GetContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/contracts/{contractId}/funding", cancellationToken)).ReadAsync<ContractFundingDto>(cancellationToken);

    public async Task<IReadOnlyList<ContractFundingDto>> ListContractFundingAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/contracts/funding", cancellationToken).ReadListAsync<ContractFundingDto>(cancellationToken);
}

public sealed class HttpFinancialExportService(HttpClient http) : IFinancialExportService
{
    public async Task<FinancialExportFile> ExportWbsCsvAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(FinancialsUrls.With($"api/v1/projects/{projectId}/financials/export/wbs", budgetVersionId, forecastVersionId), cancellationToken))
            .ReadAsync<FinancialExportFile>(cancellationToken);

    public async Task<FinancialExportFile> ExportTimePhasedCsvAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(FinancialsUrls.With($"api/v1/projects/{projectId}/financials/export/time-phased", budgetVersionId, null), cancellationToken))
            .ReadAsync<FinancialExportFile>(cancellationToken);
}

internal static class FinancialsUrls
{
    public static string With(string path, Guid? budgetVersionId, Guid? forecastVersionId)
    {
        var query = new List<string>();
        if (budgetVersionId is { } b)
        {
            query.Add($"budgetVersionId={b}");
        }

        if (forecastVersionId is { } f)
        {
            query.Add($"forecastVersionId={f}");
        }

        return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
    }
}

public static class FinancialsClientRegistration
{
    public static IServiceCollection AddFinancialsClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IBudgetService, HttpBudgetService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProjectFinancialsService, HttpProjectFinancialsService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IFinancialExportService, HttpFinancialExportService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
