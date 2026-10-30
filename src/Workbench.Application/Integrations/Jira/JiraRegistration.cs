using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Application.Integrations.Connectors;
using Workbench.Contracts.Integrations.Jira;

namespace Workbench.Application.Integrations.Jira;

public static class JiraRegistration
{
    /// <summary>
    /// The Jira sync: connection, project links, the sync engine and its outbox connector, and the webhook receiver. Binds
    /// <see cref="JiraOptions"/> from config <c>Jira</c>. The HTTP handler is registered with TryAdd so tests can use a fake Jira.
    /// </summary>
    public static IServiceCollection AddWorkbenchJira(this IServiceCollection services)
    {
        services.AddOptions<JiraOptions>().BindConfiguration(JiraOptions.Section);
        services.TryAddSingleton<JiraSecrets>();
        services.TryAddSingleton<IJiraHandlerFactory, GuardedJiraHandlerFactory>();
        services.TryAddSingleton<JiraClientFactory>();
        services.TryAddSingleton<JiraChangeListener>();
        services.AddScoped<JiraSyncEngine>();
        services.AddScoped<JiraWebhookReceiver>();
        services.AddScoped<IConnector, JiraSyncConnector>();
        services.AddScoped<IJiraConnectionService, JiraConnectionService>();
        services.AddScoped<IJiraProjectSyncService, JiraProjectSyncService>();
        return services;
    }

    /// <summary>For the server host: plan changes become outbox events, and every linked project is polled on a timer.</summary>
    public static IServiceCollection AddWorkbenchJiraHosting(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<JiraChangeListener>());
        services.AddHostedService<JiraReconcileBackgroundService>();
        return services;
    }

    /// <summary>Called from the integration permissions, inside AddWorkbenchIdentity.</summary>
    public static IServiceCollection AddWorkbenchJiraPermissions(this IServiceCollection services)
    {
        services.Decorate<IJiraConnectionService, PermissionCheckingJiraConnectionService>();
        services.Decorate<IJiraProjectSyncService, PermissionCheckingJiraProjectSyncService>();
        return services;
    }
}
