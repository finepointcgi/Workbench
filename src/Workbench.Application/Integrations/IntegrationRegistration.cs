using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Integrations.Connectors;
using Workbench.Contracts.Integrations;

namespace Workbench.Application.Integrations;

public static class IntegrationRegistration
{
    /// <summary>
    /// Webhooks (outbox, dispatcher, delivery log), API keys and connectors. Binds <see cref="IntegrationOptions"/> from config
    /// <c>Integrations</c>. Above AddWorkbenchIdentity, whose <c>AddWorkbenchIntegrationPermissions</c> makes every service Admin-only.
    /// The real HTTP transport and DNS are registered with TryAdd, so tests (and hosts) can register fakes first.
    /// </summary>
    public static IServiceCollection AddWorkbenchIntegrations(this IServiceCollection services)
    {
        services.AddOptions<IntegrationOptions>().BindConfiguration(IntegrationOptions.Section);
        services.AddDataProtection();
        services.TryAddSingleton<IntegrationSecrets>();
        services.TryAddSingleton<IOutboundDns, SystemOutboundDns>();
        services.TryAddSingleton<OutboundUrlGuard>();
        services.TryAddSingleton<IOutboundTransport, GuardedHttpTransport>();
        services.TryAddSingleton<ISftpUploader, UnavailableSftpUploader>();

        services.AddScoped<IConnector, TeamsConnector>();
        services.AddScoped<IConnector, SlackConnector>();
        services.AddScoped<IConnector, GlExportConnector>();
        services.AddScoped<IConnector, ReportExportConnector>();
        services.AddScoped<ConnectorRunner>();
        services.AddScoped<IntegrationDispatcher>();
        services.AddScoped<ApiKeyAuthenticator>();

        services.AddScoped<IWebhookService, WebhookService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<IConnectorService, ConnectorService>();
        services.TryAddSingleton<Notifications.ChatRateLimiter>();
        services.AddSingleton<Notifications.IChatChannelFormat, Notifications.TeamsChannelFormat>(); // Notifications also posted to Teams and Slack; another product is one more IChatChannelFormat.
        services.AddSingleton<Notifications.IChatChannelFormat, Notifications.SlackChannelFormat>();
        services.TryAddSingleton<Notifications.ChatNotificationSender>();
        services.AddScoped<Workbench.Contracts.Notifications.ITeamsNotificationService, Notifications.TeamsNotificationService>();
        services.AddScoped<Workbench.Contracts.Notifications.ISlackNotificationService, Notifications.SlackNotificationService>();
        GitLab.GitLabRegistration.AddWorkbenchGitLab(services); // GitLab sync of project plans (issues, epics, milestones).
        Jira.JiraRegistration.AddWorkbenchJira(services); // Jira sync of project plans (issues <-> tasks).
        return services;
    }

    /// <summary>For the server host: the dispatcher that sends webhooks and runs connectors, in every tenant.</summary>
    public static IServiceCollection AddWorkbenchIntegrationsHosting(this IServiceCollection services)
    {
        services.AddHostedService<IntegrationDispatcherBackgroundService>();
        GitLab.GitLabRegistration.AddWorkbenchGitLabHosting(services);
        Jira.JiraRegistration.AddWorkbenchJiraHosting(services);
        return services;
    }
}
