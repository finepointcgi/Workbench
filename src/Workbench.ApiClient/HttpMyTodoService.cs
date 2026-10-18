using Workbench.Contracts.MyTodo;

namespace Workbench.ApiClient;

public sealed class HttpMyTodoService(HttpClient http) : IMyTodoService
{
    public async Task<MyTodoDto> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/my-todo", cancellationToken);
        return await response.ReadAsync<MyTodoDto>(cancellationToken);
    }
}
