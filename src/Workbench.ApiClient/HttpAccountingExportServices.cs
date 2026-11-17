using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.AccountingExport;

namespace Workbench.ApiClient;

public sealed class HttpAccountingExportService(HttpClient http) : IAccountingExportService
{
    private const string Base = "api/v1/accounting-export";

    public async Task<IReadOnlyList<AccountingFormatDto>> ListFormatsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/formats", cancellationToken)).ReadLedgerAsync<List<AccountingFormatDto>>(cancellationToken);

    public async Task<AccountingExportPreviewDto> PreviewAsync(AccountingFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/preview", request, cancellationToken)).ReadLedgerAsync<AccountingExportPreviewDto>(cancellationToken);

    public async Task<AccountingExportResultDto> ExportAsync(AccountingFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/export", request, cancellationToken)).ReadLedgerAsync<AccountingExportResultDto>(cancellationToken);

    public async Task<IReadOnlyList<AccountingExportRunDto>> ListRunsAsync(AccountingExportKind? kind = null, int limit = 100, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs?limit={limit}" + (kind is { } k ? $"&kind={k}" : ""), cancellationToken)).ReadLedgerAsync<List<AccountingExportRunDto>>(cancellationToken);

    public async Task<AccountingExportResultDto> DownloadRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}/file", cancellationToken)).ReadLedgerAsync<AccountingExportResultDto>(cancellationToken);
}

public sealed class HttpAccountingMappingService(HttpClient http) : IAccountingMappingService
{
    private const string Base = "api/v1/accounting-export/mappings";

    public async Task<AccountingMappingSetDto> GetAsync(AccountingTarget target, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{target}", cancellationToken)).ReadLedgerAsync<AccountingMappingSetDto>(cancellationToken);

    public async Task<AccountingMappingSetDto> SaveAsync(AccountingTarget target, SaveAccountingMappingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{target}", request, cancellationToken)).ReadLedgerAsync<AccountingMappingSetDto>(cancellationToken);
}

public static class AccountingExportClientRegistration
{
    public static IServiceCollection AddAccountingExportClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IAccountingExportService, HttpAccountingExportService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAccountingMappingService, HttpAccountingMappingService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
