using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Calendar;

namespace Workbench.Application.Calendar;

public static class CalendarRegistration
{
    /// <summary>The per-person read-only calendar feed (<c>calendar/{token}.ics</c>; the endpoint lives in the web host).</summary>
    public static IServiceCollection AddWorkbenchCalendarFeed(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddScoped<ICalendarFeedService, CalendarFeedService>();
        services.AddScoped<CalendarFeedBuilder>();
        return services;
    }
}
