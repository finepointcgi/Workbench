using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Customization;

namespace Workbench.ApiClient;

public sealed class HttpCustomFieldService(HttpClient http) : ICustomFieldService
{
    private const string Base = "api/v1/customization";

    private static string E(string value) => Uri.EscapeDataString(value);

    public async Task<IReadOnlyList<CustomizableEntityDto>> ListEntityTypesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/entity-types", cancellationToken)).ReadLedgerAsync<List<CustomizableEntityDto>>(cancellationToken);

    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ListDefinitionsAsync(string entityType, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/fields/{E(entityType)}?includeInactive={includeInactive}", cancellationToken)).ReadLedgerAsync<List<CustomFieldDefinitionDto>>(cancellationToken);

    public async Task<CustomFieldDefinitionDto> SaveDefinitionAsync(Guid? id, SaveCustomFieldRequest request, CancellationToken cancellationToken = default) =>
        await (id is { } existing
            ? await http.PutAsJsonAsync($"{Base}/fields/{existing}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/fields", request, cancellationToken)).ReadLedgerAsync<CustomFieldDefinitionDto>(cancellationToken);

    public async Task<CustomFieldDefinitionDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default) =>
        await (await http.PutAsync($"{Base}/fields/{id}/active?isActive={isActive}", null, cancellationToken)).ReadLedgerAsync<CustomFieldDefinitionDto>(cancellationToken);

    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReorderAsync(string entityType, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/fields/{E(entityType)}/order", orderedIds, cancellationToken)).ReadLedgerAsync<List<CustomFieldDefinitionDto>>(cancellationToken);

    public async Task<RecordCustomFieldsDto> GetValuesAsync(string entityType, Guid? recordId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/values/{E(entityType)}/{(recordId is { } id ? id.ToString() : "new")}", cancellationToken)).ReadLedgerAsync<RecordCustomFieldsDto>(cancellationToken);

    public async Task<RecordCustomFieldsDto> SaveValuesAsync(string entityType, Guid recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/values/{E(entityType)}/{recordId}", request, cancellationToken)).ReadLedgerAsync<RecordCustomFieldsDto>(cancellationToken);

    public async Task<IReadOnlyList<string>> ValidateAsync(string entityType, Guid? recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(LedgerUrls.With($"{Base}/values/{E(entityType)}/validate", ("recordId", recordId)), request, cancellationToken)).ReadLedgerAsync<List<string>>(cancellationToken);

    public async Task<IReadOnlyList<Guid>> FilterAsync(string entityType, IReadOnlyList<CustomFieldFilter> filters, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/values/{E(entityType)}/filter", filters, cancellationToken)).ReadLedgerAsync<List<Guid>>(cancellationToken);

    public async Task<IReadOnlyList<CustomFieldSearchHitDto>> SearchAsync(string text, string? entityType = null, int limit = 50, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/search", ("text", text), ("entityType", entityType), ("limit", limit)), cancellationToken)).ReadLedgerAsync<List<CustomFieldSearchHitDto>>(cancellationToken);

    public async Task<CustomFieldTableDto> GetTableAsync(string entityType, IReadOnlyList<Guid>? recordIds = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/values/{E(entityType)}/table", new CustomFieldTableRequest(recordIds), cancellationToken)).ReadLedgerAsync<CustomFieldTableDto>(cancellationToken);

    public async Task<CustomFieldExportDto> ExportCsvAsync(string entityType, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/values/{E(entityType)}/export", cancellationToken)).ReadLedgerAsync<CustomFieldExportDto>(cancellationToken);

    public async Task<IReadOnlyList<CustomFieldOptionDto>> LookupRecordsAsync(string entityType, string? text, int limit = 20, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/records/{E(entityType)}", ("text", text), ("limit", limit)), cancellationToken)).ReadLedgerAsync<List<CustomFieldOptionDto>>(cancellationToken);

    public async Task CopyValuesAsync(string entityType, Guid fromRecordId, Guid toRecordId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/values/{E(entityType)}/copy?from={fromRecordId}&to={toRecordId}", null, cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public sealed class HttpCustomListService(HttpClient http) : ICustomListService
{
    private const string Base = "api/v1/customization";

    private static string E(string value) => Uri.EscapeDataString(value);

    public async Task<IReadOnlyList<ValueListDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/lists", cancellationToken)).ReadLedgerAsync<List<ValueListDto>>(cancellationToken);

    public async Task<ValueListDto> GetAsync(string key, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/lists/{E(key)}", cancellationToken)).ReadLedgerAsync<ValueListDto>(cancellationToken);

    public async Task<ValueListDto> SaveAsync(SaveValueListRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/lists", request, cancellationToken)).ReadLedgerAsync<ValueListDto>(cancellationToken);

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/lists/{E(key)}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<StatusSetDto>> ListStatusSetsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/statuses", cancellationToken)).ReadLedgerAsync<List<StatusSetDto>>(cancellationToken);

    public async Task<StatusSetDto> SaveStatusSetAsync(string setKey, SaveStatusSetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/statuses/{E(setKey)}", request, cancellationToken)).ReadLedgerAsync<StatusSetDto>(cancellationToken);

    public async Task<RecordSubStatusDto> GetSubStatusAsync(string setKey, Guid recordId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/statuses/{E(setKey)}/{recordId}", cancellationToken)).ReadLedgerAsync<RecordSubStatusDto>(cancellationToken);

    public async Task<RecordSubStatusDto> SetSubStatusAsync(string setKey, Guid recordId, string? code, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/statuses/{E(setKey)}/{recordId}", new SetSubStatusRequest(code), cancellationToken)).ReadLedgerAsync<RecordSubStatusDto>(cancellationToken);
}

public sealed class HttpNumberingService(HttpClient http) : INumberingService
{
    private const string Base = "api/v1/customization/numbering";

    public async Task<IReadOnlyList<NumberingSchemeDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadLedgerAsync<List<NumberingSchemeDto>>(cancellationToken);

    public async Task<NumberingSchemeDto> SaveAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{Uri.EscapeDataString(documentType)}", request, cancellationToken)).ReadLedgerAsync<NumberingSchemeDto>(cancellationToken);

    public async Task<NumberingSchemeDto> ResetAsync(string documentType, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{Uri.EscapeDataString(documentType)}", cancellationToken)).ReadLedgerAsync<NumberingSchemeDto>(cancellationToken);

    public async Task<NumberingPreviewDto> PreviewAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{Uri.EscapeDataString(documentType)}/preview", request, cancellationToken)).ReadLedgerAsync<NumberingPreviewDto>(cancellationToken);
}

public static class CustomizationClientRegistration
{
    public static IServiceCollection AddCustomizationClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICustomFieldService, HttpCustomFieldService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICustomListService, HttpCustomListService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<INumberingService, HttpNumberingService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
