using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        v1.MapSheetViewEndpoints();
        v1.MapItemEndpoints();
        v1.MapSchedulingEndpoints();
        v1.MapCollaborationEndpoints();
        v1.MapProjectAccountingEndpoints();
        v1.MapIdentityEndpoints();
        v1.MapAutomationEndpoints();
        v1.MapNotificationEndpoints();
        v1.MapCalendarChatEndpoints();
        v1.MapEmailDeliverabilityEndpoints();
        v1.MapMyWorkEndpoints();
        v1.MapDashboardEndpoints();
        v1.MapTimeEndpoints();
        v1.MapOdcEndpoints();
        v1.MapFinancialsEndpoints();
        v1.MapSetupEndpoints();
        v1.MapAccountingEndpoints();
        v1.MapSecurityAdminEndpoints();
        v1.MapContractDetailEndpoints();
        v1.MapIndirectRateEndpoints();
        v1.MapBillingEndpoints();
        v1.MapPurchasingEndpoints();
        v1.MapHostAdminEndpoints();
        v1.MapOrganizationSettingsEndpoints();
        v1.MapCompanyProfileEndpoints();
        v1.MapImportEndpoints();
        v1.MapProcurementEndpoints();
        v1.MapLeaveEndpoints();
        v1.MapPerDiemResourcingEndpoints();
        v1.MapReportEndpoints();
        v1.MapApprovalEndpoints();
        v1.MapProjectHubEndpoints();
        v1.MapMyTodoEndpoints();
        v1.MapSignInSettingsEndpoints();
        v1.MapLicenseEndpoints();
        v1.MapSupportEndpoints();
        v1.MapMigrationEndpoints();
        v1.MapTaskEndpoints();
        v1.MapAssistantEndpoints();
        v1.MapMcpEndpoints(); // MCP for outside AI clients, API keys only (docs/integrations/mcp.md).
        v1.MapCurrencyEndpoints();
        v1.MapCompanyEndpoints();
        v1.MapCommercialBillingEndpoints();
        v1.MapTravelEndpoints();
        v1.MapSourcingEndpoints();
        v1.MapAccountingExportEndpoints();
        v1.MapIntegrationEndpoints();
        v1.MapReceiptEndpoints();
        v1.MapCustomizationEndpoints();
        v1.MapRoleEndpoints();
        v1.MapAutomationBuilderEndpoints();
        v1.MapLayoutEndpoints();
        v1.MapSheetExtrasEndpoints();

        // License modules loaded from the modules folder map theirs on the same group: authorized, with the same error mapping.
        app.ServiceProvider.GetService<Workbench.Plugins.ModuleHost>()?.MapEndpoints(v1);

        return app;
    }

    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (WorkbenchEditionException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status402PaymentRequired);
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
