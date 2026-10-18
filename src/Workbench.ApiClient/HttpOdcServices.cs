using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;

namespace Workbench.ApiClient;

public sealed class HttpOdcService(HttpClient http) : IOdcService
{
    public async Task<OdcListDto> ListProjectOdcsAsync(Guid projectId, OdcListQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new OdcListQuery();
        var url = $"api/v1/projects/{projectId}/odcs?includeExpenses={query.IncludeExpenses}"
            + (query.From is { } from ? $"&from={from:yyyy-MM-dd}" : "")
            + (query.To is { } to ? $"&to={to:yyyy-MM-dd}" : "")
            + (query.Type is { } type ? $"&type={type}" : "")
            + (query.Status is { } status ? $"&status={status}" : "");
        return await (await http.GetAsync(url, cancellationToken)).ReadOdcAsync<OdcListDto>(cancellationToken);
    }

    public Task<OdcEntryDto?> GetAsync(Guid odcId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<OdcEntryDto>($"api/v1/odcs/{odcId}", cancellationToken);

    public async Task<OdcEntryDto> CreateVendorOdcAsync(Guid projectId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/odcs", request, cancellationToken)).ReadOdcAsync<OdcEntryDto>(cancellationToken);

    public async Task<OdcEntryDto> UpdateVendorOdcAsync(Guid odcId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/odcs/{odcId}", request, cancellationToken)).ReadOdcAsync<OdcEntryDto>(cancellationToken);

    public async Task DeleteVendorOdcAsync(Guid odcId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/odcs/{odcId}", cancellationToken)).EnsureOdcOkAsync(cancellationToken);

    public Task<OdcEntryDto> AttachReceiptAsync(Guid odcId, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        http.UploadReceiptAsync($"api/v1/odcs/{odcId}/receipt", fileName, content, cancellationToken);

    public async Task<ReceiptContent> OpenReceiptAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(GetReceiptUrl(odcId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await response.EnsureOdcOkAsync(cancellationToken);
            var headers = response.Content.Headers;
            var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? odcId.ToString();
            var buffer = new MemoryStream();
            await response.Content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return new ReceiptContent(fileName, headers.ContentType?.MediaType ?? AttachmentRules.ContentTypeFor(fileName), buffer);
        }
        finally
        {
            response.Dispose();
        }
    }

    public string GetReceiptUrl(Guid odcId) => new Uri(http.BaseAddress!, $"api/v1/odcs/{odcId}/receipt").ToString();

    public async Task<OdcEntryDto> SubmitAsync(Guid odcId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/odcs/{odcId}/submit", null, cancellationToken)).ReadOdcAsync<OdcEntryDto>(cancellationToken);

    public async Task<ExpensePolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/odcs/policy", cancellationToken)).ReadOdcAsync<ExpensePolicyDto>(cancellationToken);
}

public sealed class HttpExpenseReportService(HttpClient http) : IExpenseReportService
{
    private const string Base = "api/v1/expense-reports";

    public async Task<IReadOnlyList<ExpenseReportDto>> ListMyReportsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/mine", cancellationToken)).ReadOdcAsync<List<ExpenseReportDto>>(cancellationToken);

    public async Task<ExpenseReportDetailDto> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{reportId}", cancellationToken)).ReadOdcAsync<ExpenseReportDetailDto>(cancellationToken);

    public async Task<ExpenseReportDto> CreateReportAsync(SaveExpenseReportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadOdcAsync<ExpenseReportDto>(cancellationToken);

    public async Task<ExpenseReportDto> UpdateReportAsync(Guid reportId, SaveExpenseReportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{reportId}", request, cancellationToken)).ReadOdcAsync<ExpenseReportDto>(cancellationToken);

    public async Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{reportId}", cancellationToken)).EnsureOdcOkAsync(cancellationToken);

    public async Task<OdcEntryDto> AddLineAsync(Guid reportId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{reportId}/lines", request, cancellationToken)).ReadOdcAsync<OdcEntryDto>(cancellationToken);

    public async Task<OdcEntryDto> UpdateLineAsync(Guid reportId, Guid lineId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{reportId}/lines/{lineId}", request, cancellationToken)).ReadOdcAsync<OdcEntryDto>(cancellationToken);

    public async Task RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{reportId}/lines/{lineId}", cancellationToken)).EnsureOdcOkAsync(cancellationToken);

    public Task<OdcEntryDto> AttachReceiptAsync(Guid reportId, Guid lineId, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        http.UploadReceiptAsync($"{Base}/{reportId}/lines/{lineId}/receipt", fileName, content, cancellationToken);

    public async Task<ExpenseReportDto> SubmitAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{reportId}/submit", null, cancellationToken)).ReadOdcAsync<ExpenseReportDto>(cancellationToken);
}

public sealed class HttpOdcApprovalService(HttpClient http) : IOdcApprovalService
{
    private const string Base = "api/v1/odc-approvals";

    public async Task<OdcApprovalQueueDto> GetMyQueueAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/queue", cancellationToken)).ReadOdcAsync<OdcApprovalQueueDto>(cancellationToken);

    public async Task<OdcApprovalQueueDto> ListMyApprovedAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/approved", cancellationToken)).ReadOdcAsync<OdcApprovalQueueDto>(cancellationToken);

    public Task<ExpenseReportDto> ApproveReportAsync(Guid reportId, string? comment = null, CancellationToken cancellationToken = default) =>
        PostAsync<ExpenseReportDto>($"reports/{reportId}/approve", comment, cancellationToken);

    public Task<ExpenseReportDto> RejectReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default) =>
        PostAsync<ExpenseReportDto>($"reports/{reportId}/reject", reason, cancellationToken);

    public Task<ExpenseReportDto> ReopenReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default) =>
        PostAsync<ExpenseReportDto>($"reports/{reportId}/reopen", reason, cancellationToken);

    public Task<OdcEntryDto> ApproveOdcAsync(Guid odcId, string? comment = null, CancellationToken cancellationToken = default) =>
        PostAsync<OdcEntryDto>($"odcs/{odcId}/approve", comment, cancellationToken);

    public Task<OdcEntryDto> RejectOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default) =>
        PostAsync<OdcEntryDto>($"odcs/{odcId}/reject", reason, cancellationToken);

    public Task<OdcEntryDto> ReopenOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default) =>
        PostAsync<OdcEntryDto>($"odcs/{odcId}/reopen", reason, cancellationToken);

    public async Task<OdcEntryDto> SetUnallowableAsync(Guid odcId, bool isUnallowable, string? reason, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/odcs/{odcId}/unallowable", new SetUnallowableRequest(isUnallowable, reason), cancellationToken))
            .ReadOdcAsync<OdcEntryDto>(cancellationToken);

    private async Task<T> PostAsync<T>(string path, string? reason, CancellationToken cancellationToken) =>
        await (await http.PostAsJsonAsync($"{Base}/{path}", new OdcDecisionRequest(reason), cancellationToken)).ReadOdcAsync<T>(cancellationToken);
}

internal static class HttpOdcExtensions
{
    /// <summary>Like <see cref="ApiResponses.EnsureOkAsync"/>, plus 403 → <see cref="UnauthorizedAccessException"/> and 409 → <see cref="InvalidOperationException"/>.</summary>
    public static async Task EnsureOdcOkAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            await ThrowWorkflowErrorsAsync(response, cancellationToken);
            await response.EnsureOkAsync(cancellationToken);
        }
    }

    public static async Task<T> ReadOdcAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            await ThrowWorkflowErrorsAsync(response, cancellationToken);
            return await response.ReadAsync<T>(cancellationToken);
        }
    }

    public static async Task<OdcEntryDto> UploadReceiptAsync(this HttpClient http, string url, string fileName, Stream content, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(AttachmentRules.ContentTypeFor(fileName));
        form.Add(file, "file", fileName);
        return await (await http.PostAsync(url, form, cancellationToken)).ReadOdcAsync<OdcEntryDto>(cancellationToken);
    }

    private static async Task ThrowWorkflowErrorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}

public static class OdcClientRegistration
{
    public static IServiceCollection AddOdcClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IOdcService, HttpOdcService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IExpenseReportService, HttpExpenseReportService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IOdcApprovalService, HttpOdcApprovalService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
