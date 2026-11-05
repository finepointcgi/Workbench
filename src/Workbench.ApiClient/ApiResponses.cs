using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts;

namespace Workbench.ApiClient;

internal static class ApiResponses
{
    /// <summary>
    /// Maps 402 to <see cref="WorkbenchEditionException"/>, 404 to <see cref="WorkbenchNotFoundException"/>, 403 to <see cref="WorkbenchForbiddenException"/> and 400 to
    /// <see cref="ArgumentException"/>, like the in-process services.
    /// </summary>
    public static async Task EnsureOkAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw response.StatusCode switch
        {
            HttpStatusCode.PaymentRequired => new WorkbenchEditionException(detail),
            HttpStatusCode.NotFound => new WorkbenchNotFoundException(detail),
            HttpStatusCode.Forbidden => new WorkbenchForbiddenException(string.IsNullOrWhiteSpace(detail) ? "You don't have permission to do that." : detail),
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
