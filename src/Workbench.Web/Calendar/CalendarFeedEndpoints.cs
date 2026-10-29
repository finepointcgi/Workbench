using Workbench.Application.Calendar;

namespace Workbench.Web.Calendar;

/// <summary>
/// The calendar feed, <c>GET /calendar/{token}.ics</c>. Anonymous because Outlook, Google and Apple Calendar can't sign in: the token is the
/// credential (256 random bits, revocable). Unknown, revoked and malformed tokens all answer the same 404, and the answer is never cached
/// publicly. The global rate limiter applies.
/// </summary>
public static class CalendarFeedEndpoints
{
    public static IEndpointRouteBuilder MapWorkbenchCalendarFeed(this IEndpointRouteBuilder app)
    {
        app.MapGet("/calendar/{token}.ics", async (string token, HttpContext http, CalendarFeedBuilder builder) =>
        {
            var ics = await builder.BuildAsync(token, http.RequestAborted);
            if (ics is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=300";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Text(ics, CalendarContentType);
        }).AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    private const string CalendarContentType = IcsWriter.ContentType;
}
