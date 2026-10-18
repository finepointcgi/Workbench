using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Application.Notifications;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Automation;

public static class AutomationRegistration
{
    /// <summary>
    /// Registers rules and notifications, and decorates <see cref="IItemService"/> and <see cref="ICommentService"/>.
    /// Call it after <c>AddWorkbenchCollaboration</c> (it wraps the activity-recording decorator; see
    /// <see cref="AutomationItemService"/> for the ordering). Infrastructure supplies the real <see cref="IEmailSender"/>.
    /// </summary>
    public static IServiceCollection AddWorkbenchAutomation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new NotificationOptions());
        services.TryAddSingleton<IEmailSender, NullEmailSender>();
        services.AddSingleton<NotificationHub>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<DigestSender>();
        services.AddSingleton<RuleEngine>();
        services.AddScoped<INotificationFeed, InProcessNotificationFeed>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IRuleService, RuleService>();
        services.Decorate<IItemService, AutomationItemService>();
        services.Decorate<ICommentService, MentionNotifyingCommentService>();
        services.Decorate<ISheetChangePublisher, AutomationNeutralSheetChangePublisher>();
        return services;
    }

    /// <summary>
    /// For the server host, after <see cref="ICurrentUser"/> is registered: attributes rule actions to
    /// "Workbench Automation" and starts the hourly date-trigger and daily-digest services.
    /// </summary>
    public static IServiceCollection AddWorkbenchAutomationHosting(this IServiceCollection services)
    {
        services.Decorate<ICurrentUser, AutomationAwareCurrentUser>();
        services.AddHostedService<DateTriggerBackgroundService>();
        services.AddHostedService<DigestBackgroundService>();
        return services;
    }
}
