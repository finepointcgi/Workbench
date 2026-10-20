using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Quality;

namespace Workbench.ApiClient;

public sealed class HttpInspectionService(HttpClient http) : IInspectionService
{
    private const string Base = "api/v1/quality";

    public async Task<IReadOnlyList<InspectionPlanDto>> ListPlansAsync(Guid? partId = null, bool activeOnly = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/plans", ("partId", partId), ("activeOnly", activeOnly)), cancellationToken)).ReadLedgerAsync<List<InspectionPlanDto>>(cancellationToken);

    public async Task<InspectionPlanDto> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/plans/{planId}", cancellationToken)).ReadLedgerAsync<InspectionPlanDto>(cancellationToken);

    public async Task<InspectionPlanDto> SavePlanAsync(Guid? planId, SaveInspectionPlanRequest request, CancellationToken cancellationToken = default) =>
        await (planId is { } id
            ? await http.PutAsJsonAsync($"{Base}/plans/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/plans", request, cancellationToken)).ReadLedgerAsync<InspectionPlanDto>(cancellationToken);

    public async Task<IReadOnlyList<InspectionSummaryDto>> ListAsync(InspectionQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/inspections", ("status", query.Status), ("all", query.Status is null ? true : null), ("kind", query.Kind),
            ("partId", query.PartId), ("vendorId", query.VendorId), ("take", query.Take)), cancellationToken)).ReadLedgerAsync<List<InspectionSummaryDto>>(cancellationToken);

    public async Task<InspectionDto> GetAsync(Guid inspectionId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/inspections/{inspectionId}", cancellationToken)).ReadLedgerAsync<InspectionDto>(cancellationToken);

    public async Task<InspectionDto> CompleteAsync(Guid inspectionId, CompleteInspectionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inspections/{inspectionId}/complete", request, cancellationToken)).ReadLedgerAsync<InspectionDto>(cancellationToken);

    public async Task<IReadOnlyList<DefectCodeDto>> ListDefectCodesAsync(bool activeOnly = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/defect-codes", ("activeOnly", activeOnly)), cancellationToken)).ReadLedgerAsync<List<DefectCodeDto>>(cancellationToken);

    public async Task<DefectCodeDto> SaveDefectCodeAsync(Guid? defectCodeId, SaveDefectCodeRequest request, CancellationToken cancellationToken = default) =>
        await (defectCodeId is { } id
            ? await http.PutAsJsonAsync($"{Base}/defect-codes/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/defect-codes", request, cancellationToken)).ReadLedgerAsync<DefectCodeDto>(cancellationToken);
}

public sealed class HttpNonconformanceService(HttpClient http) : INonconformanceService
{
    private const string Base = "api/v1/quality";

    public async Task<IReadOnlyList<NcrSummaryDto>> ListAsync(NcrQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/ncrs", ("status", query.Status), ("vendorId", query.VendorId), ("partId", query.PartId), ("openOnly", query.OpenOnly),
            ("take", query.Take)), cancellationToken)).ReadLedgerAsync<List<NcrSummaryDto>>(cancellationToken);

    public async Task<NcrDto> GetAsync(Guid ncrId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/ncrs/{ncrId}", cancellationToken)).ReadLedgerAsync<NcrDto>(cancellationToken);

    public async Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/ncrs", request, cancellationToken)).ReadLedgerAsync<NcrDto>(cancellationToken);

    public async Task<NcrDto> ProposeDispositionAsync(Guid ncrId, ProposeDispositionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/ncrs/{ncrId}/disposition", request, cancellationToken)).ReadLedgerAsync<NcrDto>(cancellationToken);

    public async Task<NcrDto> ApproveAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/ncrs/{ncrId}/approve", request, cancellationToken)).ReadLedgerAsync<NcrDto>(cancellationToken);

    public async Task<NcrDto> RejectAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/ncrs/{ncrId}/reject", request, cancellationToken)).ReadLedgerAsync<NcrDto>(cancellationToken);

    public async Task<NcrDto> WithdrawDispositionAsync(Guid ncrId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/ncrs/{ncrId}/withdraw", null, cancellationToken)).ReadLedgerAsync<NcrDto>(cancellationToken);

    public async Task<IReadOnlyList<NcrSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/ncrs/waiting-on-me", cancellationToken)).ReadLedgerAsync<List<NcrSummaryDto>>(cancellationToken);

    public async Task<IReadOnlyList<DebitMemoDto>> ListDebitMemosAsync(Guid? vendorId = null, bool openOnly = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/debit-memos", ("vendorId", vendorId), ("openOnly", openOnly)), cancellationToken)).ReadLedgerAsync<List<DebitMemoDto>>(cancellationToken);

    public async Task<DebitMemoDto> ApplyDebitMemoAsync(Guid debitMemoId, ApplyDebitMemoRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/debit-memos/{debitMemoId}/apply", request, cancellationToken)).ReadLedgerAsync<DebitMemoDto>(cancellationToken);
}

public sealed class HttpCorrectiveActionService(HttpClient http) : ICorrectiveActionService
{
    private const string Base = "api/v1/quality";

    public async Task<IReadOnlyList<CarSummaryDto>> ListAsync(CarQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/cars", ("status", query.Status), ("vendorId", query.VendorId), ("openOnly", query.OpenOnly)), cancellationToken))
            .ReadLedgerAsync<List<CarSummaryDto>>(cancellationToken);

    public async Task<CarDto> GetAsync(Guid carId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/cars/{carId}", cancellationToken)).ReadLedgerAsync<CarDto>(cancellationToken);

    public async Task<CarDto> SaveAsync(Guid? carId, SaveCarRequest request, CancellationToken cancellationToken = default) =>
        await (carId is { } id
            ? await http.PutAsJsonAsync($"{Base}/cars/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/cars", request, cancellationToken)).ReadLedgerAsync<CarDto>(cancellationToken);

    public async Task<CarDto> MarkImplementedAsync(Guid carId, CompleteCarRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/cars/{carId}/implemented", request, cancellationToken)).ReadLedgerAsync<CarDto>(cancellationToken);

    public async Task<CarDto> VerifyAsync(Guid carId, VerifyCarRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/cars/{carId}/verify", request, cancellationToken)).ReadLedgerAsync<CarDto>(cancellationToken);

    public async Task<CarDto> CancelAsync(Guid carId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/cars/{carId}/cancel", null, cancellationToken)).ReadLedgerAsync<CarDto>(cancellationToken);

    public async Task<SupplierQualityDto> SupplierQualityAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/supplier-quality", ("from", from), ("to", to)), cancellationToken)).ReadLedgerAsync<SupplierQualityDto>(cancellationToken);
}

public sealed class HttpQualityFileService(HttpClient http) : IQualityFileService
{
    private const string Base = "api/v1/quality/files";

    public async Task<QualityFileDto> AttachAsync(QualityFileOwner owner, Guid ownerId, string kind, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(AttachmentRules.ContentTypeFor(fileName));
        form.Add(file, "file", fileName);
        form.Add(new StringContent(kind ?? ""), "kind");
        return await (await http.PostAsync($"{Base}/{owner}/{ownerId}", form, cancellationToken)).ReadLedgerAsync<QualityFileDto>(cancellationToken);
    }

    public async Task<ReceiptContent> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(GetUrl(attachmentId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        var headers = response.Content.Headers;
        var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? $"file-{attachmentId}";
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return new ReceiptContent(fileName, headers.ContentType?.MediaType ?? AttachmentRules.ContentTypeFor(fileName), buffer);
    }

    public string GetUrl(Guid attachmentId) => new Uri(http.BaseAddress!, $"{Base}/{attachmentId}").ToString();
}

public sealed class HttpEngineeringChangeService(HttpClient http) : IEngineeringChangeService
{
    private const string Base = "api/v1/engineering-changes";

    public async Task<IReadOnlyList<EcoSummaryDto>> ListAsync(EcoQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("partId", query.PartId), ("openOnly", query.OpenOnly)), cancellationToken))
            .ReadLedgerAsync<List<EcoSummaryDto>>(cancellationToken);

    public async Task<EcoDto> GetAsync(Guid ecoId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{ecoId}", cancellationToken)).ReadLedgerAsync<EcoDto>(cancellationToken);

    public async Task<EcoDto> SaveAsync(Guid? ecoId, SaveEcoRequest request, CancellationToken cancellationToken = default) =>
        await (ecoId is { } id
            ? await http.PutAsJsonAsync($"{Base}/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<EcoDto>(cancellationToken);

    public async Task<EcoDto> SubmitAsync(Guid ecoId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{ecoId}/submit", null, cancellationToken)).ReadLedgerAsync<EcoDto>(cancellationToken);

    public async Task<EcoDto> ApproveAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{ecoId}/approve", request, cancellationToken)).ReadLedgerAsync<EcoDto>(cancellationToken);

    public async Task<EcoDto> RejectAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{ecoId}/reject", request, cancellationToken)).ReadLedgerAsync<EcoDto>(cancellationToken);

    public async Task<EcoDto> CancelAsync(Guid ecoId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{ecoId}/cancel", null, cancellationToken)).ReadLedgerAsync<EcoDto>(cancellationToken);

    public async Task<int> ApplyDueAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        (await (await http.PostAsync(LedgerUrls.With($"{Base}/apply-due", ("asOf", asOf)), null, cancellationToken)).ReadLedgerAsync<AppliedDto>(cancellationToken)).Applied;

    private sealed record AppliedDto(int Applied);

    public async Task<IReadOnlyList<WorkOrderReviewDto>> ListOpenWorkOrderReviewsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/work-order-reviews", cancellationToken)).ReadLedgerAsync<List<WorkOrderReviewDto>>(cancellationToken);

    public async Task<WorkOrderReviewDto> MarkWorkOrderReviewedAsync(Guid reviewId, ReviewWorkOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-order-reviews/{reviewId}/reviewed", request, cancellationToken)).ReadLedgerAsync<WorkOrderReviewDto>(cancellationToken);

    public async Task<IReadOnlyList<EcoSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/waiting-on-me", cancellationToken)).ReadLedgerAsync<List<EcoSummaryDto>>(cancellationToken);

    public async Task<RevisionHistoryDto> RevisionHistoryAsync(Guid partId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/parts/{partId}/history", cancellationToken)).ReadLedgerAsync<RevisionHistoryDto>(cancellationToken);

    public async Task<IReadOnlyList<ConfigurationUnitDto>> ListUnitsAsync(Guid partId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/parts/{partId}/units", cancellationToken)).ReadLedgerAsync<List<ConfigurationUnitDto>>(cancellationToken);

    public async Task<ConfigurationDto> ConfigurationAsync(Guid partId, string? serial = null, string? lot = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/parts/{partId}/configuration", ("serial", serial), ("lot", lot)), cancellationToken))
            .ReadLedgerAsync<ConfigurationDto>(cancellationToken);
}

public static class QualityClientRegistration
{
    public static IServiceCollection AddQualityClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IInspectionService, HttpInspectionService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<INonconformanceService, HttpNonconformanceService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICorrectiveActionService, HttpCorrectiveActionService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IQualityFileService, HttpQualityFileService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IEngineeringChangeService, HttpEngineeringChangeService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
