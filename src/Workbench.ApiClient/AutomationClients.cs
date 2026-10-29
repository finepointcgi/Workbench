using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Notifications;

namespace Workbench.ApiClient;

public static class AutomationClients
{
    /// <summary>Rules and notifications over HTTP, and the notification feed over the server's SignalR hub.</summary>
    public static IServiceCollection AddAutomationClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IRuleService, HttpRuleService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<INotificationService, HttpNotificationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<Contracts.Calendar.ICalendarFeedService, HttpCalendarFeedService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITeamsNotificationService, HttpTeamsNotificationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddSingleton<INotificationFeed>(_ => new SignalRNotificationFeed(serverBaseAddress));
        return services;
    }
}
