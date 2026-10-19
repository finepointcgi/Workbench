using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Contracts.Currencies;

namespace Workbench.Application.Currencies;

public static class CurrencyRegistration
{
    /// <summary>
    /// Currencies and exchange rates, the period-end revaluation and its close check, and the foreign currency report. Call
    /// after AddWorkbenchAccounting and before AddWorkbenchIdentity, which adds the permission decorators
    /// (<c>AddWorkbenchCurrencyPermissions</c>). No <see cref="IExchangeRateProvider"/> is registered: rates are entered by
    /// hand or imported until a host adds one.
    /// </summary>
    public static IServiceCollection AddWorkbenchCurrencies(this IServiceCollection services)
    {
        services.AddScoped<ICurrencyService, CurrencyService>();
        services.AddScoped<IRevaluationService, RevaluationService>();
        services.AddScoped<IPeriodCloseCheck, CurrencyRevaluationCloseCheck>();
        return services;
    }
}
