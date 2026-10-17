using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts;

namespace Workbench.ApiClient;

internal static class ApiResponses
{
    /// <summary>Maps 404 to <see cref="WorkbenchNotFoundException"/> and 400 to <see cref="ArgumentException"/>, like the in-process services.</summary>
    public static async Task EnsureOkAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new WorkbenchNotFoundException(detail),
            HttpStatusCode.BadRequest => new ArgumentException(detail),
            _ => new HttpRequestException($"Server returned {(int)response.StatusCode}: {detail}", null, response.StatusCode),
        };
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.EnsureOkAsync(cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new HttpRequestException("Server returned an empty response.");
    }
}
