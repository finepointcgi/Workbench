using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.MyTodo;

namespace Workbench.Api;

internal static class MyTodoEndpoints
{
    public static void MapMyTodoEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/my-todo", (IMyTodoService service, CancellationToken ct) => service.GetAsync(ct)).WithTags("My Work");
    }
}
