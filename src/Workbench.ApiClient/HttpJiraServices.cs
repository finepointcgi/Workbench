using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.Integrations.Jira;

namespace Workbench.ApiClient;

public sealed class HttpJiraConnectionService(HttpClient http) : IJiraConnectionService
{
    private const string Base = "api/v1/integrations/jira";

    public async Task<JiraConnectionDto> GetAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/connection", cancellationToken)).ReadLedgerAsync<JiraConnectionDto>(cancellationToken);

    public async Task<JiraConnectionDto> SaveAsync(SaveJiraConnectionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/connection", request, cancellationToken)).ReadLedgerAsync<JiraConnectionDto>(cancellationToken);

    public async Task<JiraTestResult> TestAsync(SaveJiraConnectionRequest? unsaved = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/connection/test", unsaved, cancellationToken)).ReadLedgerAsync<JiraTestResult>(cancellationToken);

    public async Task DeleteAsync(CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/connection", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public sealed class HttpJiraProjectSyncService(HttpClient http) : IJiraProjectSyncService
{
    private const string Base = "api/v1/integrations/jira";

    public async Task<JiraProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}", cancellationToken)).ReadLedgerAsync<JiraProjectSyncDto>(cancellationToken);

    public async Task<IReadOnlyList<JiraProjectOptionDto>> SearchJiraProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}/jira-projects?q={Uri.EscapeDataString(query ?? "")}", cancellationToken)).ReadLedgerAsync<List<JiraProjectOptionDto>>(cancellationToken);

    public async Task<JiraProjectSyncDto> LinkAsync(Guid projectId, LinkJiraProjectRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/projects/{projectId}/link", request, cancellationToken)).ReadLedgerAsync<JiraProjectSyncDto>(cancellationToken);

    public async Task<JiraProjectSyncDto> UpdateLinkAsync(Guid projectId, UpdateJiraLinkRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/projects/{projectId}/link", request, cancellationToken)).ReadLedgerAsync<JiraProjectSyncDto>(cancellationToken);

    public async Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/projects/{projectId}/link", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<JiraSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/projects/{projectId}/sync", null, cancellationToken)).ReadLedgerAsync<JiraSyncNowResult>(cancellationToken);

    public async Task<JiraWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}/webhook", cancellationToken)).ReadLedgerAsync<JiraWebhookSetupDto>(cancellationToken);

    public async Task<JiraProjectSyncDto> DismissNoteAsync(Guid projectId, Guid noteId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/projects/{projectId}/notes/{noteId}/dismiss", null, cancellationToken)).ReadLedgerAsync<JiraProjectSyncDto>(cancellationToken);

    public async Task<JiraTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Base}/sheets/{sheetId}/items/{itemId}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadLedgerAsync<JiraTaskStatusDto>(cancellationToken);
    }
}
