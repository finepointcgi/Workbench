using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Support;

namespace Workbench.Application.Support;

public static class SupportRegistration
{
    /// <summary>In-app support requests (config <c>Support:Email</c>), the host's cross-tenant view and per-user help state.</summary>
    public static IServiceCollection AddWorkbenchSupport(this IServiceCollection services)
    {
        services.AddOptions<SupportOptions>().BindConfiguration(SupportOptions.Section);
        services.AddScoped<ISupportService, SupportService>();
        services.AddScoped<IHostSupportService, HostSupportService>();
        services.AddScoped<IHelpStateService, HelpStateService>();
        return services;
    }
}
