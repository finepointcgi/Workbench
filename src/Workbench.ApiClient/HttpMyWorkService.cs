using Workbench.Contracts.MyWork;

namespace Workbench.ApiClient;

public sealed class HttpMyWorkService(HttpClient http) : IMyWorkService
{
    public async Task<MyWorkDto> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/my-work", cancellationToken);
        return await response.ReadAsync<MyWorkDto>(cancellationToken);
    }
}
