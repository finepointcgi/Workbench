using Microsoft.AspNetCore.Http.HttpResults;
using Workbench.Contracts.Billing;
using Workbench.Web.Customer.Pages;

namespace Workbench.Web.Customer;

/// <summary>
/// The customer's link, <c>/c/{token}</c>: a plain server-rendered, read-only page of one contract's funding and invoice status
/// that anyone holding the link can open without signing in. The token is the only key, so the response is never cached or
/// indexed (the page itself also asks browsers for no referrer), and says the same thing ("isn't available") for a wrong, ended or expired link.
/// </summary>
public static class CustomerViewEndpoints
{
    public const string Base = "/c";

    public static IEndpointRouteBuilder MapWorkbenchCustomerView(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Base).AllowAnonymous().ExcludeFromDescription();

        group.MapGet("/{token}", async (string token, HttpContext http, ICustomerViewService service) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return await service.GetAsync(token, http.RequestAborted) is { } view
                ? Page<CustomerViewPage>(new { View = view })
                : Page<CustomerMessagePage>(new
                {
                    Title = "This link isn't available",
                    Text = "The link may have ended or expired, or it may have been mistyped. Ask the person who sent it for a new one.",
                }, StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static IResult Page<T>(object parameters, int status = 200) where T : Microsoft.AspNetCore.Components.IComponent =>
        new RazorComponentResult<T>(parameters) { StatusCode = status };
}
