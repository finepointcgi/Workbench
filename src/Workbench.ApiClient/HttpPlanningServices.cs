using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Planning;

namespace Workbench.ApiClient;

public sealed class HttpRecurringJournalService(HttpClient http) : IRecurringJournalService
{
    private const string Root = "api/v1/accounting/recurring-journals";

    public async Task<IReadOnlyList<RecurringTemplateDto>> ListTemplatesAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Root, ("includeInactive", includeInactive ? "true" : "false")), cancellationToken)).ReadLedgerAsync<List<RecurringTemplateDto>>(cancellationToken);

    public async Task<RecurringTemplateDto> GetTemplateAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{templateId}", cancellationToken)).ReadLedgerAsync<RecurringTemplateDto>(cancellationToken);

    public async Task<RecurringTemplateDto> CreateTemplateAsync(SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Root, request, cancellationToken)).ReadLedgerAsync<RecurringTemplateDto>(cancellationToken);

    public async Task<RecurringTemplateDto> UpdateTemplateAsync(Guid templateId, SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{templateId}", request, cancellationToken)).ReadLedgerAsync<RecurringTemplateDto>(cancellationToken);

    public async Task<RecurringTemplateDto> SetActiveAsync(Guid templateId, bool isActive, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{templateId}/active?value={(isActive ? "true" : "false")}", null, cancellationToken)).ReadLedgerAsync<RecurringTemplateDto>(cancellationToken);

    public async Task<IReadOnlyList<RecurringRunDto>> GetScheduleAsync(Guid templateId, int upcoming = 6, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{templateId}/schedule?upcoming={upcoming}", cancellationToken)).ReadLedgerAsync<List<RecurringRunDto>>(cancellationToken);

    public async Task<IReadOnlyList<RecurringRunDto>> ListHistoryAsync(Guid? templateId = null, int take = 200, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Root}/history", ("templateId", templateId), ("take", take)), cancellationToken)).ReadLedgerAsync<List<RecurringRunDto>>(cancellationToken);

    public async Task<RecurringGenerationResultDto> GenerateDueAsync(GenerateRecurringRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/generate", request, cancellationToken)).ReadLedgerAsync<RecurringGenerationResultDto>(cancellationToken);

    public async Task<RecurringSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/settings", cancellationToken)).ReadLedgerAsync<RecurringSettingsDto>(cancellationToken);

    public async Task<RecurringSettingsDto> SaveSettingsAsync(SaveRecurringSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/settings", request, cancellationToken)).ReadLedgerAsync<RecurringSettingsDto>(cancellationToken);
}

public sealed class HttpGlBudgetService(HttpClient http) : IGlBudgetService
{
    private const string Root = "api/v1/planning/budgets";

    public async Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Root, ("fiscalYear", fiscalYear)), cancellationToken)).ReadLedgerAsync<List<BudgetVersionDto>>(cancellationToken);

    public async Task<BudgetVersionDto> CreateVersionAsync(CreateBudgetVersionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Root, request, cancellationToken)).ReadLedgerAsync<BudgetVersionDto>(cancellationToken);

    public async Task<BudgetGridDto> GetGridAsync(Guid versionId, Guid? orgUnitId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Root}/{versionId}/grid", ("orgUnitId", orgUnitId)), cancellationToken)).ReadLedgerAsync<BudgetGridDto>(cancellationToken);

    public async Task<BudgetGridDto> SaveCellsAsync(Guid versionId, SaveBudgetCellsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{versionId}/cells", request, cancellationToken)).ReadLedgerAsync<BudgetGridDto>(cancellationToken);

    public async Task<BudgetGridDto> SpreadAsync(Guid versionId, SpreadBudgetRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{versionId}/spread", request, cancellationToken)).ReadLedgerAsync<BudgetGridDto>(cancellationToken);

    public async Task<BudgetImportResultDto> ImportCsvAsync(Guid versionId, ImportBudgetCsvRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{versionId}/import", request, cancellationToken)).ReadLedgerAsync<BudgetImportResultDto>(cancellationToken);

    public async Task<BudgetVersionDto> AdoptAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{versionId}/adopt", null, cancellationToken)).ReadLedgerAsync<BudgetVersionDto>(cancellationToken);
}

public sealed class HttpBudgetReportService(HttpClient http) : IBudgetReportService
{
    private const string Root = "api/v1/planning/reports";

    private static string Url(string path, BudgetVsActualQuery q) =>
        LedgerUrls.With($"{Root}/{path}", ("versionId", q.VersionId), ("periodId", q.FiscalPeriodId), ("orgUnitId", q.OrgUnitId));

    public async Task<BudgetVsActualDto> GetIncomeStatementAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url("income-statement", query), cancellationToken)).ReadLedgerAsync<BudgetVsActualDto>(cancellationToken);

    public async Task<BudgetVsActualDto> GetByOrgUnitAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url("by-org-unit", query), cancellationToken)).ReadLedgerAsync<BudgetVsActualDto>(cancellationToken);

    public async Task<BudgetVsActualDto> GetByPoolAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url("by-pool", query), cancellationToken)).ReadLedgerAsync<BudgetVsActualDto>(cancellationToken);

    public async Task<BudgetedRatesDto> GetBudgetedRatesAsync(int fiscalYear, Guid? versionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Root}/budgeted-rates/{fiscalYear}", ("versionId", versionId)), cancellationToken)).ReadLedgerAsync<BudgetedRatesDto>(cancellationToken);

    public async Task<AccountingExportFile> ExportCsvAsync(BudgetReport report, BudgetVsActualQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Root}/export", ("report", report), ("versionId", query.VersionId), ("periodId", query.FiscalPeriodId), ("orgUnitId", query.OrgUnitId)), cancellationToken))
            .ReadLedgerAsync<AccountingExportFile>(cancellationToken);
}

public sealed class HttpPlanningCycleService(HttpClient http) : IPlanningCycleService
{
    private const string Cycles = "api/v1/planning/cycles";
    private const string Submissions = "api/v1/planning/submissions";

    public async Task<IReadOnlyList<PlanningCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Cycles, cancellationToken)).ReadLedgerAsync<List<PlanningCycleDto>>(cancellationToken);

    public async Task<PlanningCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Cycles}/{cycleId}", cancellationToken)).ReadLedgerAsync<PlanningCycleDto>(cancellationToken);

    public async Task<PlanningCycleDto> OpenCycleAsync(OpenPlanningCycleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Cycles, request, cancellationToken)).ReadLedgerAsync<PlanningCycleDto>(cancellationToken);

    public async Task<PlanningCycleDto> AssignOwnerAsync(Guid cycleId, CycleAssignmentInput assignment, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Cycles}/{cycleId}/assignments", assignment, cancellationToken)).ReadLedgerAsync<PlanningCycleDto>(cancellationToken);

    public async Task<PlanningCycleDto> AdoptAsync(Guid cycleId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Cycles}/{cycleId}/adopt", null, cancellationToken)).ReadLedgerAsync<PlanningCycleDto>(cancellationToken);

    public async Task<IReadOnlyList<BudgetSubmissionSummaryDto>> ListMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Submissions}/mine", cancellationToken)).ReadLedgerAsync<List<BudgetSubmissionSummaryDto>>(cancellationToken);

    public async Task<BudgetSubmissionDto> GetSubmissionAsync(Guid submissionId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Submissions}/{submissionId}", cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);

    public async Task<BudgetSubmissionDto> SaveSubmissionCellsAsync(Guid submissionId, SaveSubmissionCellsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Submissions}/{submissionId}/cells", request, cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);

    public async Task<BudgetSubmissionDto> SpreadSubmissionAsync(Guid submissionId, SpreadSubmissionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Submissions}/{submissionId}/spread", request, cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);

    public async Task<BudgetSubmissionDto> SubmitAsync(Guid submissionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Submissions}/{submissionId}/submit", null, cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);

    public async Task<BudgetSubmissionDto> RecallAsync(Guid submissionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Submissions}/{submissionId}/recall", null, cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);

    public async Task<BudgetSubmissionDto> ApproveAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Submissions}/{submissionId}/approve", request, cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);

    public async Task<BudgetSubmissionDto> ReturnAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Submissions}/{submissionId}/return", request, cancellationToken)).ReadLedgerAsync<BudgetSubmissionDto>(cancellationToken);
}

public static class PlanningClientRegistration
{
    public static IServiceCollection AddPlanningClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IRecurringJournalService, HttpRecurringJournalService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IGlBudgetService, HttpGlBudgetService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBudgetReportService, HttpBudgetReportService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPlanningCycleService, HttpPlanningCycleService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
