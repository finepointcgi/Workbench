using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.Integrations.GitLab;

namespace Workbench.ApiClient;

public sealed class HttpGitLabConnectionService(HttpClient http) : IGitLabConnectionService
{
    private const string Base = "api/v1/integrations/gitlab";

    public async Task<GitLabConnectionDto> GetAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/connection", cancellationToken)).ReadLedgerAsync<GitLabConnectionDto>(cancellationToken);

    public async Task<GitLabConnectionDto> SaveAsync(SaveGitLabConnectionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/connection", request, cancellationToken)).ReadLedgerAsync<GitLabConnectionDto>(cancellationToken);

    public async Task<GitLabTestResult> TestAsync(SaveGitLabConnectionRequest? unsaved = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/connection/test", unsaved, cancellationToken)).ReadLedgerAsync<GitLabTestResult>(cancellationToken);

    public async Task DeleteAsync(CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/connection", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<GitLabUserMapDto>> ListPeopleAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/people", cancellationToken)).ReadLedgerAsync<List<GitLabUserMapDto>>(cancellationToken);

    public async Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/users?q={Uri.EscapeDataString(query)}", cancellationToken)).ReadLedgerAsync<List<GitLabUserOptionDto>>(cancellationToken);

    public async Task<GitLabUserMapDto> MatchPersonAsync(MatchGitLabPersonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/people", request, cancellationToken)).ReadLedgerAsync<GitLabUserMapDto>(cancellationToken);

    public async Task UnmatchPersonAsync(string email, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/people/{Uri.EscapeDataString(email)}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public sealed class HttpGitLabProjectSyncService(HttpClient http) : IGitLabProjectSyncService
{
    private const string Base = "api/v1/integrations/gitlab";

    public async Task<GitLabProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}", cancellationToken)).ReadLedgerAsync<GitLabProjectSyncDto>(cancellationToken);

    public async Task<IReadOnlyList<GitLabProjectOptionDto>> SearchGitLabProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}/gitlab-projects?q={Uri.EscapeDataString(query ?? "")}", cancellationToken))
            .ReadLedgerAsync<List<GitLabProjectOptionDto>>(cancellationToken);

    public async Task<GitLabProjectSyncDto> LinkAsync(Guid projectId, LinkGitLabProjectRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/projects/{projectId}/link", request, cancellationToken)).ReadLedgerAsync<GitLabProjectSyncDto>(cancellationToken);

    public async Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/projects/{projectId}/link", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<GitLabSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/projects/{projectId}/sync", null, cancellationToken)).ReadLedgerAsync<GitLabSyncNowResult>(cancellationToken);

    public async Task<GitLabWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}/webhook", cancellationToken)).ReadLedgerAsync<GitLabWebhookSetupDto>(cancellationToken);

    public async Task<GitLabProjectSyncDto> ResolveConflictAsync(Guid projectId, Guid conflictId, ResolveGitLabConflictRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/projects/{projectId}/conflicts/{conflictId}", request, cancellationToken)).ReadLedgerAsync<GitLabProjectSyncDto>(cancellationToken);

    public async Task<GitLabProjectSyncDto> DecidePendingAsync(Guid projectId, Guid pendingId, bool approve, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/projects/{projectId}/pending/{pendingId}/{(approve ? "approve" : "reject")}", null, cancellationToken))
            .ReadLedgerAsync<GitLabProjectSyncDto>(cancellationToken);

    public async Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(Guid projectId, string query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}/users?q={Uri.EscapeDataString(query)}", cancellationToken)).ReadLedgerAsync<List<GitLabUserOptionDto>>(cancellationToken);

    public async Task<GitLabProjectSyncDto> MatchPersonAsync(Guid projectId, MatchGitLabPersonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/projects/{projectId}/people", request, cancellationToken)).ReadLedgerAsync<GitLabProjectSyncDto>(cancellationToken);

    public async Task<GitLabTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Base}/sheets/{sheetId}/items/{itemId}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadLedgerAsync<GitLabTaskStatusDto>(cancellationToken);
    }
}
