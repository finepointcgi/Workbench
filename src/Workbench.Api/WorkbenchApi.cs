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
        // Signed-in users only: the web app's auth cookie or a bearer token from the OIDC authority (desktop app).
        var v1 = app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter(MapErrors);

        v1.MapSheetEndpoints();
        v1.MapColumnEndpoints();
        v1.MapItemEndpoints();
        v1.MapSchedulingEndpoints();
        v1.MapCollaborationEndpoints();
        v1.MapCostpointEndpoints();
        v1.MapProjectAccountingEndpoints();
        v1.MapIdentityEndpoints();
        v1.MapAutomationEndpoints();
        v1.MapNotificationEndpoints();
        v1.MapMyWorkEndpoints();
        v1.MapDashboardEndpoints();
        v1.MapTimeEndpoints();
        v1.MapOdcEndpoints();
        v1.MapFinancialsEndpoints();
        v1.MapSetupEndpoints();
        v1.MapSecurityAdminEndpoints();

        return app;
    }

    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (WorkbenchForbiddenException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
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
