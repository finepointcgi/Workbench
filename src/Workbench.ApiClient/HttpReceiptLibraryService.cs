using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Receipts;

namespace Workbench.ApiClient;

public sealed class HttpReceiptLibraryService(HttpClient http) : IReceiptLibraryService
{
    private const string Base = "api/v1/receipts";

    public async Task<IReadOnlyList<ReceiptDto>> ListMineAsync(ReceiptQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ReceiptQuery();
        var url = $"{Base}/mine?unattached={query.UnattachedOnly}&take={query.Take}"
            + (string.IsNullOrWhiteSpace(query.Search) ? "" : $"&search={Uri.EscapeDataString(query.Search)}")
            + (query.From is { } from ? $"&from={from:yyyy-MM-dd}" : "")
            + (query.To is { } to ? $"&to={to:yyyy-MM-dd}" : "")
            + (query.MinAmount is { } min ? $"&minAmount={min.ToString(CultureInfo.InvariantCulture)}" : "")
            + (query.MaxAmount is { } max ? $"&maxAmount={max.ToString(CultureInfo.InvariantCulture)}" : "");
        return await (await http.GetAsync(url, cancellationToken)).ReadOdcAsync<List<ReceiptDto>>(cancellationToken);
    }

    public async Task<ReceiptDto> GetAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{fileId}", cancellationToken)).ReadOdcAsync<ReceiptDto>(cancellationToken);

    public async Task<ReceiptReadingDto> UploadAsync(string fileName, Stream content, Guid? expenseReportId = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(AttachmentRules.ContentTypeFor(fileName));
        form.Add(file, "file", fileName);
        var url = expenseReportId is { } report ? $"{Base}?expenseReportId={report}" : Base;
        return await (await http.PostAsync(url, form, cancellationToken)).ReadOdcAsync<ReceiptReadingDto>(cancellationToken);
    }

    public async Task<ReceiptReadingDto> ReadAsync(Guid fileId, Guid? expenseReportId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{fileId}/reading" + (expenseReportId is { } report ? $"?expenseReportId={report}" : ""), cancellationToken))
            .ReadOdcAsync<ReceiptReadingDto>(cancellationToken);

    public async Task<LodgingRateCheckDto> CheckLodgingAsync(LodgingRateCheckRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/lodging-check", request, cancellationToken)).ReadOdcAsync<LodgingRateCheckDto>(cancellationToken);

    public async Task<ReceiptDto> UpdateAsync(Guid fileId, UpdateReceiptRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{fileId}", request, cancellationToken)).ReadOdcAsync<ReceiptDto>(cancellationToken);

    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{fileId}", cancellationToken)).EnsureOdcOkAsync(cancellationToken);

    public async Task<ReceiptContent> OpenAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(GetContentUrl(fileId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await response.EnsureOdcOkAsync(cancellationToken);
            var headers = response.Content.Headers;
            var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? fileId.ToString();
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

    public string GetContentUrl(Guid fileId) => new Uri(http.BaseAddress!, $"{Base}/{fileId}/content").ToString();
}

public static class ReceiptClientRegistration
{
    public static IServiceCollection AddReceiptClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IReceiptLibraryService, HttpReceiptLibraryService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
