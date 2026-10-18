using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Assets;

namespace Workbench.ApiClient;

public sealed class HttpAssetService(HttpClient http) : IAssetService
{
    private const string Base = "api/v1/assets";

    public async Task<IReadOnlyList<AssetSummaryDto>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("categoryId", query.CategoryId), ("search", query.Search),
            ("governmentPropertyOnly", query.GovernmentPropertyOnly)), cancellationToken)).ReadLedgerAsync<List<AssetSummaryDto>>(cancellationToken);

    public async Task<AssetDto> GetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{assetId}", cancellationToken)).ReadLedgerAsync<AssetDto>(cancellationToken);

    public async Task<AssetSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/settings", cancellationToken)).ReadLedgerAsync<AssetSettingsDto>(cancellationToken);

    public async Task<AssetDto> CreateAsync(SaveAssetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<AssetDto>(cancellationToken);

    public async Task<AssetDto> UpdateAsync(Guid assetId, SaveAssetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{assetId}", request, cancellationToken)).ReadLedgerAsync<AssetDto>(cancellationToken);

    public async Task<IReadOnlyList<CapitalizableBillLineDto>> ListCapitalizableBillLinesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/capitalizable", cancellationToken)).ReadLedgerAsync<List<CapitalizableBillLineDto>>(cancellationToken);

    public async Task<AssetDto> CapitalizeAsync(CapitalizeBillLineRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/capitalize", request, cancellationToken)).ReadLedgerAsync<AssetDto>(cancellationToken);

    public async Task<AssetDto> TransferAsync(Guid assetId, TransferAssetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{assetId}/transfer", request, cancellationToken)).ReadLedgerAsync<AssetDto>(cancellationToken);

    public async Task<AssetDto> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{assetId}/dispose", request, cancellationToken)).ReadLedgerAsync<AssetDto>(cancellationToken);

    public async Task<IReadOnlyList<AssetCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/categories", cancellationToken)).ReadLedgerAsync<List<AssetCategoryDto>>(cancellationToken);

    public async Task<AssetCategoryDto> SaveCategoryAsync(Guid? categoryId, SaveAssetCategoryRequest request, CancellationToken cancellationToken = default) =>
        await (categoryId is { } id
                ? await http.PutAsJsonAsync($"{Base}/categories/{id}", request, cancellationToken)
                : await http.PostAsJsonAsync($"{Base}/categories", request, cancellationToken))
            .ReadLedgerAsync<AssetCategoryDto>(cancellationToken);
}

public sealed class HttpDepreciationService(HttpClient http) : IDepreciationService
{
    private const string Base = "api/v1/assets/depreciation";

    public async Task<DepreciationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<DepreciationPreviewDto>(cancellationToken);

    public async Task<DepreciationRunDto?> RunAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsync($"{Base}/{fiscalPeriodId}/run", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            response.Dispose();
            return null;
        }

        return await response.ReadLedgerAsync<DepreciationRunDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<DepreciationRunDto>> ListRunsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs", cancellationToken)).ReadLedgerAsync<List<DepreciationRunDto>>(cancellationToken);
}

public sealed class HttpAssetReportService(HttpClient http) : IAssetReportService
{
    private const string Base = "api/v1/assets/reports";

    public async Task<IReadOnlyList<AssetSummaryDto>> GetRegisterAsync(AssetQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/register", ("status", query.Status), ("categoryId", query.CategoryId), ("search", query.Search),
            ("governmentPropertyOnly", query.GovernmentPropertyOnly)), cancellationToken)).ReadLedgerAsync<List<AssetSummaryDto>>(cancellationToken);

    public async Task<DepreciationScheduleDto> GetScheduleAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/schedule/{assetId}?book={book}", cancellationToken)).ReadLedgerAsync<DepreciationScheduleDto>(cancellationToken);

    public async Task<RollForwardDto> GetRollForwardAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/roll-forward", ("from", from), ("to", to)), cancellationToken)).ReadLedgerAsync<RollForwardDto>(cancellationToken);

    public async Task<IReadOnlyList<GovernmentPropertyRowDto>> GetGovernmentPropertyAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/government-property", cancellationToken)).ReadLedgerAsync<List<GovernmentPropertyRowDto>>(cancellationToken);

    public async Task<AssetExportFile> ExportRegisterCsvAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/register/export", cancellationToken)).ReadLedgerAsync<AssetExportFile>(cancellationToken);

    public async Task<AssetExportFile> ExportScheduleCsvAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/schedule/{assetId}/export?book={book}", cancellationToken)).ReadLedgerAsync<AssetExportFile>(cancellationToken);

    public async Task<AssetExportFile> ExportRollForwardCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/roll-forward/export", ("from", from), ("to", to)), cancellationToken)).ReadLedgerAsync<AssetExportFile>(cancellationToken);

    public async Task<AssetExportFile> ExportGovernmentPropertyCsvAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/government-property/export", cancellationToken)).ReadLedgerAsync<AssetExportFile>(cancellationToken);
}

public static class AssetClientRegistration
{
    public static IServiceCollection AddAssetClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IAssetService, HttpAssetService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IDepreciationService, HttpDepreciationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAssetReportService, HttpAssetReportService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
