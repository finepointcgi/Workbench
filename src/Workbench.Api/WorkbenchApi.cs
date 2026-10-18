using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts;

namespace Workbench.Api;

/// <summary>REST endpoints used by the desktop app; opened to customers in v1.1.</summary>
public static class WorkbenchApi
{
    public static IEndpointRouteBuilder MapWorkbenchApi(this IEndpointRouteBuilder app)
    {
        // TODO: require OIDC bearer auth once identity is wired up.
        var v1 = app.MapGroup("/api/v1").AddEndpointFilter(MapErrors);

        v1.MapSheetEndpoints();
        v1.MapColumnEndpoints();
        v1.MapItemEndpoints();
        v1.MapSchedulingEndpoints();
        v1.MapCollaborationEndpoints();
        v1.MapCostpointEndpoints();
        v1.MapProjectAccountingEndpoints();

        return app;
    }

    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (WorkbenchNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
