using System.Net.Http.Json;
using Workbench.Contracts.Payroll;

namespace Workbench.ApiClient;

public sealed class HttpPaylocityService(HttpClient http) : IPaylocityService
{
    private const string Base = "api/v1/payroll/paylocity";

    public async Task<PaylocityMappingDto> GetMappingAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/mapping", cancellationToken)).ReadAsync<PaylocityMappingDto>(cancellationToken);

    public async Task<PaylocityMappingDto> SaveCostCentersAsync(IReadOnlyList<SaveCostCenterMapping> mappings, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/cost-centers", mappings, cancellationToken)).ReadAsync<PaylocityMappingDto>(cancellationToken);

    public async Task<IReadOnlyList<PayrollExportRecordDto>> ListExportsAsync(Guid? payGroupId = null, Guid? payPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/exports", ("payGroupId", payGroupId), ("payPeriodId", payPeriodId)), cancellationToken)).ReadAsync<List<PayrollExportRecordDto>>(cancellationToken);

    public async Task<PayrollExportFileDto> GetExportFileAsync(Guid exportId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/exports/{exportId}/file", cancellationToken)).ReadAsync<PayrollExportFileDto>(cancellationToken);

    public async Task<PaylocityRosterSettingsDto> GetRosterSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/roster/settings", cancellationToken)).ReadAsync<PaylocityRosterSettingsDto>(cancellationToken);

    public async Task<PaylocityRosterSettingsDto> SaveRosterSettingsAsync(SavePaylocityRosterSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/roster/settings", request, cancellationToken)).ReadAsync<PaylocityRosterSettingsDto>(cancellationToken);

    public async Task<PaylocityRosterResultDto> ImportRosterAsync(PaylocityRosterRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/roster/import", request, cancellationToken)).ReadAsync<PaylocityRosterResultDto>(cancellationToken);
}
