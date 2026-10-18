using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Imports;

namespace Workbench.ApiClient;

public sealed class HttpImportService(HttpClient http) : IImportService
{
    public async Task<IReadOnlyList<ImportKindDto>> ListKindsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/imports/kinds", cancellationToken).ReadListAsync<ImportKindDto>(cancellationToken);

    public async Task<IReadOnlyList<ImportJobDto>> ListJobsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/imports", cancellationToken).ReadListAsync<ImportJobDto>(cancellationToken);

    public Task<ImportJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<ImportJobDetailDto>($"api/v1/imports/{jobId}", cancellationToken);

    public async Task<ImportJobDetailDto> UploadAsync(UploadImportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/imports", request, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<ImportJobDetailDto> ConnectSmartsheetAsync(ConnectSmartsheetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/imports/smartsheet/connect", request, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<ImportJobDetailDto> SelectSmartsheetSheetsAsync(Guid jobId, SelectSmartsheetSheetsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/imports/{jobId}/smartsheet/sheets", request, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<ImportJobDetailDto> SaveMappingAsync(Guid jobId, SaveImportMappingRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/imports/{jobId}/mapping", request, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<ImportJobDetailDto> ValidateAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/imports/{jobId}/validate", null, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<ImportJobDetailDto> DryRunAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/imports/{jobId}/dry-run", null, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<ImportJobDetailDto> ImportAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/imports/{jobId}/import", null, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task<IReadOnlyList<ImportRecordDto>> ListRecordsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/imports/{jobId}/records", cancellationToken).ReadListAsync<ImportRecordDto>(cancellationToken);

    public async Task<RollbackCheckDto> CheckRollbackAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/imports/{jobId}/rollback", cancellationToken)).ReadAsync<RollbackCheckDto>(cancellationToken);

    public async Task<ImportJobDetailDto> RollBackAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/imports/{jobId}/rollback", null, cancellationToken)).ReadAsync<ImportJobDetailDto>(cancellationToken);

    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/imports/{jobId}", cancellationToken)).EnsureOkAsync(cancellationToken);
}

public static class ImportClientRegistration
{
    public static IServiceCollection AddImportClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IImportService, HttpImportService>(c =>
        {
            c.BaseAddress = serverBaseAddress;
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        return services;
    }
}
