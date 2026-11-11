using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Compliance;

namespace Workbench.ApiClient;

public sealed class HttpCpsrService(HttpClient http) : ICpsrService
{
    private const string Base = "api/v1/compliance/cpsr";

    private static string Review(string path, CpsrReviewRequest r) =>
        LedgerUrls.With($"{Base}/{path}", ("from", r.From), ("to", r.To), ("sampleSize", r.SampleSize), ("seed", r.Seed));

    public async Task<CpsrReadinessDto> GetReadinessAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Review("readiness", request), cancellationToken)).ReadLedgerAsync<CpsrReadinessDto>(cancellationToken);

    public async Task<ProcurementFileDto> GetFileAsync(ProcurementDocumentKind kind, Guid documentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/files/{kind}/{documentId}", cancellationToken)).ReadLedgerAsync<ProcurementFileDto>(cancellationToken);

    public async Task<ProcurementFileDto> SaveFileAsync(ProcurementDocumentKind kind, Guid documentId, SaveProcurementFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/files/{kind}/{documentId}", request, cancellationToken)).ReadLedgerAsync<ProcurementFileDto>(cancellationToken);

    public async Task<ProcurementFileDto> ApproveAsync(ProcurementDocumentKind kind, Guid documentId, ApproveProcurementFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/files/{kind}/{documentId}/approve", request, cancellationToken)).ReadLedgerAsync<ProcurementFileDto>(cancellationToken);

    public async Task<CpsrSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/settings", cancellationToken)).ReadLedgerAsync<CpsrSettingsDto>(cancellationToken);

    public async Task<ComplianceExportFile> ExportReviewAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Review("export", request), cancellationToken)).ReadLedgerAsync<ComplianceExportFile>(cancellationToken);
}

public sealed class HttpCasDisclosureService(HttpClient http) : ICasDisclosureService
{
    private const string Base = "api/v1/compliance/disclosure";

    public async Task<IReadOnlyList<DisclosureVersionDto>> ListVersionsAsync(DisclosureFormType? form = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("form", form)), cancellationToken)).ReadLedgerAsync<List<DisclosureVersionDto>>(cancellationToken);

    public async Task<DisclosureStatementDto> GetAsync(Guid statementId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{statementId}", cancellationToken)).ReadLedgerAsync<DisclosureStatementDto>(cancellationToken);

    public async Task<DisclosureStatementDto> CreateVersionAsync(CreateDisclosureVersionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<DisclosureStatementDto>(cancellationToken);

    public async Task<DisclosureStatementDto> SaveAnswersAsync(Guid statementId, IReadOnlyList<SaveDisclosureAnswerRequest> answers, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{statementId}/answers", answers, cancellationToken)).ReadLedgerAsync<DisclosureStatementDto>(cancellationToken);

    public async Task<DisclosureStatementDto> FillFromConfigurationAsync(Guid statementId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{statementId}/fill", null, cancellationToken)).ReadLedgerAsync<DisclosureStatementDto>(cancellationToken);

    public async Task<DisclosureStatementDto> MakeEffectiveAsync(Guid statementId, MakeDisclosureEffectiveRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{statementId}/effective", request, cancellationToken)).ReadLedgerAsync<DisclosureStatementDto>(cancellationToken);

    public async Task DeleteDraftAsync(Guid statementId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{statementId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<CasConsistencyDto> CheckConsistencyAsync(Guid? statementId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/consistency", ("statementId", statementId)), cancellationToken)).ReadLedgerAsync<CasConsistencyDto>(cancellationToken);

    public async Task<ComplianceExportFile> ExportAsync(Guid statementId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{statementId}/export", cancellationToken)).ReadLedgerAsync<ComplianceExportFile>(cancellationToken);
}

public sealed class HttpCmmcService(HttpClient http) : ICmmcService
{
    private const string Base = "api/v1/compliance/cmmc";

    public async Task<CmmcAssessmentDto> GetAssessmentAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadLedgerAsync<CmmcAssessmentDto>(cancellationToken);

    public async Task<CmmcPracticeDto> SavePracticeAsync(string practiceId, SaveCmmcPracticeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/practices/{Uri.EscapeDataString(practiceId)}", request, cancellationToken)).ReadLedgerAsync<CmmcPracticeDto>(cancellationToken);

    public async Task<CmmcPoamItemDto> AddPoamItemAsync(SaveCmmcPoamRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/poam", request, cancellationToken)).ReadLedgerAsync<CmmcPoamItemDto>(cancellationToken);

    public async Task<CmmcPoamItemDto> UpdatePoamItemAsync(Guid itemId, SaveCmmcPoamRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/poam/{itemId}", request, cancellationToken)).ReadLedgerAsync<CmmcPoamItemDto>(cancellationToken);

    public async Task DeletePoamItemAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/poam/{itemId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public static class ComplianceClientRegistration
{
    public static IServiceCollection AddComplianceClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICpsrService, HttpCpsrService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICasDisclosureService, HttpCasDisclosureService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICmmcService, HttpCmmcService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
