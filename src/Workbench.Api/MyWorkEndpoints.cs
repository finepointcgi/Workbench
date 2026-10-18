using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.MyWork;

namespace Workbench.Api;

internal static class MyWorkEndpoints
{
    public static void MapMyWorkEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/my-work", (IMyWorkService service, CancellationToken ct) => service.GetAsync(ct)).WithTags("My Work");
    }
}
