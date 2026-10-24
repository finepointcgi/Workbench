using Workbench.Application.Collaboration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Integrations.Connectors;
using Workbench.Contracts.Integrations.GitLab;

namespace Workbench.Application.Integrations.GitLab;

public static class GitLabRegistration
{
    /// <summary>
    /// The GitLab sync: connection, project links, the sync engine and its outbox connector, and the webhook receiver. Binds
    /// <see cref="GitLabOptions"/> from config <c>GitLab</c>. The HTTP handler is registered with TryAdd so tests can use a fake GitLab.
    /// </summary>
    public static IServiceCollection AddWorkbenchGitLab(this IServiceCollection services)
    {
        services.AddOptions<GitLabOptions>().BindConfiguration(GitLabOptions.Section);
        services.TryAddSingleton<GitLabSecrets>();
        services.TryAddSingleton<GitLabRateGate>();
        services.TryAddSingleton<IGitLabHandlerFactory, GuardedGitLabHandlerFactory>();
        services.TryAddSingleton<GitLabClientFactory>();
        services.TryAddSingleton<GitLabPeople>();
        services.TryAddSingleton<GitLabChangeListener>();
        services.AddScoped<GitLabSyncEngine>();
        services.AddScoped<GitLabWebhookReceiver>();
        services.AddScoped<IConnector, GitLabSyncConnector>();
        services.AddScoped<IGitLabConnectionService, GitLabConnectionService>();
        services.AddScoped<IGitLabProjectSyncService, GitLabProjectSyncService>();
        return services;
    }

    /// <summary>For the server host: plan changes become outbox events, and every linked project is reconciled on a timer.</summary>
    public static IServiceCollection AddWorkbenchGitLabHosting(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<GitLabChangeListener>());
        services.AddHostedService<GitLabReconcileBackgroundService>();
        return services;
    }

    /// <summary>Called from the integration permissions, inside AddWorkbenchIdentity.</summary>
    public static IServiceCollection AddWorkbenchGitLabPermissions(this IServiceCollection services)
    {
        services.Decorate<IGitLabConnectionService, PermissionCheckingGitLabConnectionService>();
        services.Decorate<IGitLabProjectSyncService, PermissionCheckingGitLabProjectSyncService>();
        return services;
    }
}
