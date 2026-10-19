using System.Net.Http.Headers;
using System.Net.Http.Json;
using Workbench.Contracts.Support;

namespace Workbench.ApiClient;

public sealed class HttpSupportService(HttpClient http) : ISupportService
{
    public async Task<SupportRequestDto> SubmitAsync(SubmitSupportRequest request, SupportScreenshot? screenshot = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(request, System.Text.Json.JsonSerializerOptions.Web)), "request");
        if (screenshot is not null)
        {
            var file = new StreamContent(screenshot.Content);
            file.Headers.ContentType = new MediaTypeHeaderValue(SupportRules.ScreenshotContentType(screenshot.FileName));
            form.Add(file, "screenshot", screenshot.FileName);
        }

        using var response = await http.PostAsync("api/v1/support/requests", form, cancellationToken);
        return await response.ReadAsync<SupportRequestDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<SupportRequestDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/support/requests/mine", cancellationToken);
        return await response.ReadAsync<List<SupportRequestDto>>(cancellationToken);
    }

    public async Task<IReadOnlyList<SupportRequestDto>> ListAllAsync(SupportStatus? status = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(status is null ? "api/v1/support/requests" : $"api/v1/support/requests?status={status}", cancellationToken);
        return await response.ReadAsync<List<SupportRequestDto>>(cancellationToken);
    }

    public async Task<SupportRequestDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/support/requests/{id}", cancellationToken);
        return await response.ReadAsync<SupportRequestDto>(cancellationToken);
    }

    public async Task<SupportRequestDto> UpdateAsync(Guid id, UpdateSupportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/support/requests/{id}", request, cancellationToken);
        return await response.ReadAsync<SupportRequestDto>(cancellationToken);
    }

    public async Task<SupportScreenshotContent> OpenScreenshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/support/requests/{id}/screenshot", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        var headers = response.Content.Headers;
        var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? "screenshot.png";
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new SupportScreenshotContent(fileName, headers.ContentType?.MediaType ?? SupportRules.ScreenshotContentType(fileName), new MemoryStream(bytes));
    }

    public string GetScreenshotUrl(Guid id) => new Uri(http.BaseAddress!, $"api/v1/support/requests/{id}/screenshot").ToString();
}

public sealed class HttpHostSupportService(HttpClient http) : IHostSupportService
{
    public async Task<IReadOnlyList<HostSupportRequestDto>> ListAsync(SupportStatus? status = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(status is null ? "api/v1/host/support" : $"api/v1/host/support?status={status}", cancellationToken);
        return await response.ReadAsync<List<HostSupportRequestDto>>(cancellationToken);
    }
}

public sealed class HttpHelpStateService(HttpClient http) : IHelpStateService
{
    public async Task<HelpStateDto> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/help/state", cancellationToken);
        return await response.ReadAsync<HelpStateDto>(cancellationToken);
    }

    public async Task<HelpStateDto> CompleteTourAsync(string tourKey, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/help/state/tours/{Uri.EscapeDataString(tourKey)}", null, cancellationToken);
        return await response.ReadAsync<HelpStateDto>(cancellationToken);
    }

    public async Task<HelpStateDto> RestartToursAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync("api/v1/help/state/tours", cancellationToken);
        return await response.ReadAsync<HelpStateDto>(cancellationToken);
    }

    public async Task<HelpStateDto> MarkWhatsNewSeenAsync(string version, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/help/state/whats-new/{Uri.EscapeDataString(version)}", null, cancellationToken);
        return await response.ReadAsync<HelpStateDto>(cancellationToken);
    }
}
