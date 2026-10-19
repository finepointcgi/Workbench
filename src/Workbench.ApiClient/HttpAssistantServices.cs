using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Assistant;

namespace Workbench.ApiClient;

public sealed class HttpAssistantService(HttpClient http) : IAssistantService
{
    public async Task<AssistantStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/assistant/status", cancellationToken)).ReadAsync<AssistantStatusDto>(cancellationToken);

    public async IAsyncEnumerable<AssistantEvent> AskAsync(AskAssistantRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/assistant/ask") { Content = JsonContent.Create(request) };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var e in JsonSerializer.DeserializeAsyncEnumerable<AssistantEvent>(stream, JsonSerializerOptions.Web, cancellationToken))
        {
            if (e is not null)
            {
                yield return e;
            }
        }
    }
}

public sealed class HttpAssistantAdminService(HttpClient http) : IAssistantAdminService
{
    public async Task<AssistantSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/assistant/settings", cancellationToken)).ReadAsync<AssistantSettingsDto>(cancellationToken);

    public async Task<AssistantSettingsDto> SaveSettingsAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/assistant/settings", request, cancellationToken)).ReadAsync<AssistantSettingsDto>(cancellationToken);

    public async Task<AssistantUsageDto> GetUsageAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/assistant/usage?year={year}&month={month}", cancellationToken)).ReadAsync<AssistantUsageDto>(cancellationToken);

    public async Task<IReadOnlyList<AssistantInteractionDto>> ListAuditAsync(AssistantAuditQuery query, CancellationToken cancellationToken = default)
    {
        var url = $"api/v1/assistant/audit?take={query.Take}"
            + (query.From is { } f ? $"&from={f:yyyy-MM-dd}" : "")
            + (query.To is { } t ? $"&to={t:yyyy-MM-dd}" : "")
            + (string.IsNullOrWhiteSpace(query.UserEmail) ? "" : $"&user={Uri.EscapeDataString(query.UserEmail)}");
        return await http.GetAsync(url, cancellationToken).ReadListAsync<AssistantInteractionDto>(cancellationToken);
    }
}

public sealed class HttpProjectNarrativeService(HttpClient http) : IProjectNarrativeService
{
    private static string Period(string path, Guid? periodId) => periodId is { } p ? $"{path}?periodId={p}" : path;

    public async Task<IReadOnlyList<NarrativePeriodDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/assistant/projects/{projectId}/narrative/periods", cancellationToken).ReadListAsync<NarrativePeriodDto>(cancellationToken);

    public async Task<ProjectNarrativeDto?> GetAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(Period($"api/v1/assistant/projects/{projectId}/narrative", fiscalPeriodId), cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadAsync<ProjectNarrativeDto>(cancellationToken);
    }

    public async Task<ProjectNarrativeDto> DraftAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(Period($"api/v1/assistant/projects/{projectId}/narrative/draft", fiscalPeriodId), null, cancellationToken)).ReadAsync<ProjectNarrativeDto>(cancellationToken);

    public async Task<ProjectNarrativeDto> SaveAsync(Guid projectId, SaveProjectNarrativeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/assistant/projects/{projectId}/narrative", request, cancellationToken)).ReadAsync<ProjectNarrativeDto>(cancellationToken);
}

public sealed class HttpTimesheetFlagService(HttpClient http) : ITimesheetFlagService
{
    public async Task<IReadOnlyList<TimesheetFlagsDto>> ListForTimesheetsAsync(IReadOnlyCollection<Guid> timesheetIds, CancellationToken cancellationToken = default) =>
        await http.PostAsJsonAsync("api/v1/assistant/timesheet-flags", new { timesheetIds }, cancellationToken).ReadListAsync<TimesheetFlagsDto>(cancellationToken);

    public async Task<TimesheetFlagsDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/assistant/timesheet-flags/{timesheetId}", cancellationToken)).ReadAsync<TimesheetFlagsDto>(cancellationToken);

    public async Task<IReadOnlyList<TimesheetFlagsDto>> ReportAsync(TimesheetFlagReportQuery query, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/assistant/timesheet-flags/report?from={query.From:yyyy-MM-dd}&to={query.To:yyyy-MM-dd}"
            + (query.RuleKey is { } r ? $"&rule={Uri.EscapeDataString(r)}" : ""), cancellationToken).ReadListAsync<TimesheetFlagsDto>(cancellationToken);

    public async Task<TimesheetFlagExplanationDto> ExplainAsync(Guid timesheetId, string ruleKey, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/assistant/timesheet-flags/{timesheetId}/explain/{Uri.EscapeDataString(ruleKey)}", null, cancellationToken))
            .ReadAsync<TimesheetFlagExplanationDto>(cancellationToken);
}

public sealed class HttpTimesheetSubmissionService(HttpClient http) : ITimesheetSubmissionService
{
    public async Task<IReadOnlyList<TimesheetSubmissionRowDto>> GetStatusAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/assistant/timesheet-submissions?date={date:yyyy-MM-dd}", cancellationToken).ReadListAsync<TimesheetSubmissionRowDto>(cancellationToken);
}

public static class AssistantClientRegistration
{
    public static IServiceCollection AddAssistantClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IAssistantService, HttpAssistantService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAssistantAdminService, HttpAssistantAdminService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProjectNarrativeService, HttpProjectNarrativeService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITimesheetFlagService, HttpTimesheetFlagService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITimesheetSubmissionService, HttpTimesheetSubmissionService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
