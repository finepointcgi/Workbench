using System.Net.Http.Json;
using Workbench.Contracts.Time;

namespace Workbench.ApiClient;

public sealed class HttpOrganizationSettingsService(HttpClient http) : IOrganizationSettingsService
{
    public async Task<OrganizationSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/setup/organization", cancellationToken)).ReadAsync<OrganizationSettingsDto>(cancellationToken);

    public async Task<TimesheetCadencePreviewDto> PreviewTimesheetCadenceAsync(ChangeTimesheetCadenceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/setup/organization/timesheet-cadence/preview", request, cancellationToken)).ReadAsync<TimesheetCadencePreviewDto>(cancellationToken);

    public async Task<OrganizationSettingsDto> ChangeTimesheetCadenceAsync(ChangeTimesheetCadenceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/setup/organization/timesheet-cadence", request, cancellationToken)).ReadAsync<OrganizationSettingsDto>(cancellationToken);
}
