using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.AccountingLive;

namespace Workbench.Application.AccountingLive;

public static class AccountingLiveRegistration
{
    /// <summary>
    /// Live push to QuickBooks Online and Xero: connections (OAuth 2.0), the pushers and the push service. Binds <see cref="AccountingProviderOptions"/> from
    /// <c>Accounting</c>. The HTTP client and the delay are registered with TryAdd so tests register fakes first. Call above AddWorkbenchIdentity, which
    /// decorates the services with the permission checks.
    /// </summary>
    public static IServiceCollection AddWorkbenchAccountingLive(this IServiceCollection services)
    {
        services.AddOptions<AccountingProviderOptions>().BindConfiguration(AccountingProviderOptions.Section);
        services.TryAddSingleton<IAccountingHttp, AccountingHttp>();
        services.TryAddSingleton<IAccountingDelay, RealAccountingDelay>();
        services.TryAddSingleton<AccountingTokenLocks>();
        services.TryAddSingleton<AccountingSecrets>();
        services.AddScoped<IAccountingOAuth, QuickBooksOAuth>();
        services.AddScoped<IAccountingOAuth, XeroOAuth>();
        services.AddScoped<IAccountingPublisher, QuickBooksPublisher>();
        services.AddScoped<IAccountingPublisher, XeroPublisher>();
        services.AddScoped<AccountingTokenService>();
        services.AddScoped<IAccountingConnectionService, AccountingConnectionService>();
        services.AddScoped<IAccountingPushService, AccountingPushService>();
        return services;
    }
}
