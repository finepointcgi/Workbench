using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Contracts.Cash;

namespace Workbench.Application.Cash;

public static class CashRegistration
{
    /// <summary>
    /// Bank accounts, statement import, matching and reconciliation, the cash position and forecast, and the "bank accounts
    /// reconciled" period-close check. Call after AddWorkbenchAccounting and AddWorkbenchSecurity (it books bank items
    /// through the journal service and logs account-number reveals) and before AddWorkbenchIdentity, which adds the
    /// Finance-only permission decorators (<c>AddWorkbenchCashPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchCash(this IServiceCollection services)
    {
        // Full account numbers are encrypted with the host's Data Protection keys (idempotent: the web host registers it too).
        services.AddDataProtection();
        services.AddSingleton<BankAccountNumberProtector>();
        services.AddScoped<IBankAccountService, BankAccountService>();
        services.AddScoped<IBankStatementService, BankStatementService>();
        services.AddScoped<IBankReconciliationService, BankReconciliationService>();
        services.AddScoped<ICashForecastService, CashForecastService>();
        services.AddScoped<IPeriodCloseCheck, BankReconciliationCloseCheck>();
        return services;
    }
}
