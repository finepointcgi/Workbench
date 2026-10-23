using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Workbench.Application.Forms;
using Workbench.Contracts;
using Workbench.Contracts.Forms;
using Workbench.Web.Forms.Pages;

namespace Workbench.Web.Forms;

/// <summary>
/// Public form links, <c>/f/{token}</c>: a plain server-rendered page (no Blazor circuit, no scripts) anyone with the link
/// can fill in without signing in. It shows only the form's own fields, never sheet data. Posts carry an antiforgery token
/// and a hidden honeypot field; <see cref="IPublicFormService"/> rate limits per client and per form.
/// </summary>
public static class PublicFormEndpoints
{
    public const string Base = "/f";

    /// <summary>The honeypot's name: people never see it, form-filling bots do.</summary>
    public const string Honeypot = "website";

    public static IEndpointRouteBuilder MapWorkbenchPublicForms(this IEndpointRouteBuilder app)
    {
        var forms = app.MapGroup(Base).AllowAnonymous().ExcludeFromDescription();

        forms.MapGet("/{token}", async (string token, HttpContext http, IAntiforgery af, IPublicFormService service) =>
            await service.GetAsync(token, http.RequestAborted) is { } form
                ? Page<PublicFormPage>(new { Form = form, Token = af.GetAndStoreTokens(http), Path = $"{Base}/{token}" })
                : NotFound(http, af));

        forms.MapPost("/{token}", async (string token, HttpContext http, IAntiforgery af, IPublicFormService service) =>
        {
            try
            {
                await af.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Page<PublicMessagePage>(new { Title = "Please try again", Text = "That form had expired. Go back, reload the page and send it again.", IsError = true }, 400);
            }

            var posted = await http.Request.ReadFormAsync(http.RequestAborted);
            var form = await service.GetAsync(token, http.RequestAborted);
            if (form is null)
            {
                return NotFound(http, af);
            }

            var answers = form.Fields.ToDictionary(f => f.Key, f => (string?)posted[f.Key].ToString());
            try
            {
                var done = await service.SubmitAsync(token, new SubmitFormRequest(answers), posted[Honeypot], http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
                return Page<PublicMessagePage>(new { Title = form.Title, Text = done.Message, Again = $"{Base}/{token}" });
            }
            catch (FormRateLimitException ex)
            {
                return Page<PublicMessagePage>(new { Title = "Please wait a moment", Text = ex.Message, IsError = true }, StatusCodes.Status429TooManyRequests);
            }
            catch (WorkbenchNotFoundException)
            {
                return NotFound(http, af);
            }
            catch (ArgumentException ex)
            {
                return Page<PublicFormPage>(new { Form = form, Token = af.GetAndStoreTokens(http), Path = $"{Base}/{token}", Error = ex.Message, Values = answers }, 400);
            }
        });

        return app;
    }

    private static IResult NotFound(HttpContext http, IAntiforgery af) =>
        Page<PublicMessagePage>(new { Title = "Form not found", Text = "This form isn't available. The link may have changed or the form may be closed to the public. Ask whoever sent it for a new link." }, 404);

    private static IResult Page<T>(object parameters, int status = 200) where T : Microsoft.AspNetCore.Components.IComponent =>
        new RazorComponentResult<T>(parameters) { StatusCode = status };
}
