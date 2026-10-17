using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.Collaboration;

namespace Workbench.ApiClient;

public sealed class HttpCommentService(HttpClient http) : ICommentService
{
    public async Task<IReadOnlyList<CommentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/items/{itemId}/comments", cancellationToken);
        return await response.ReadAsync<List<CommentDto>>(cancellationToken);
    }

    public async Task<CommentDto> CreateAsync(Guid sheetId, Guid itemId, CreateCommentRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/items/{itemId}/comments", request, cancellationToken);
        return await response.ReadAsync<CommentDto>(cancellationToken);
    }

    public async Task<CommentDto> UpdateAsync(Guid sheetId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/comments/{commentId}", request, cancellationToken);
        await ThrowIfForbiddenAsync(response, cancellationToken);
        return await response.ReadAsync<CommentDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid commentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/comments/{commentId}", cancellationToken);
        await ThrowIfForbiddenAsync(response, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    private static async Task ThrowIfForbiddenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
