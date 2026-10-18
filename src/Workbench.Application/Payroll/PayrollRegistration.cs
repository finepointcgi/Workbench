using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Payroll;

namespace Workbench.Application.Payroll;

public static class PayrollRegistration
{
    /// <summary>
    /// Payroll integration: pay groups, profiles, provider adapters (Paylocity; ADP, Paychex and Gusto coming soon) and
    /// payroll runs that post through the GL. Call after AddWorkbenchAccounting and above AddWorkbenchIdentity, whose
    /// <c>AddWorkbenchPayrollPermissions</c> makes every payroll service Finance/Admin only.
    /// </summary>
    public static IServiceCollection AddWorkbenchPayroll(this IServiceCollection services)
    {
        services.AddOptions<PaylocityOptions>().BindConfiguration("Payroll:Paylocity");
        services.AddHttpClient<PaylocityApiClient>();
        services.TryAddScoped<IPaylocityApiClient>(sp => sp.GetRequiredService<PaylocityApiClient>());

        services.AddScoped<IPayrollProvider, PaylocityPayrollProvider>();
        services.AddScoped<IPayrollProvider, AdpPayrollProvider>();
        services.AddScoped<IPayrollProvider, PaychexPayrollProvider>();
        services.AddScoped<IPayrollProvider, GustoPayrollProvider>();
        services.AddScoped<PayrollProviders>();

        services.AddScoped<IPayGroupService, PayGroupService>();
        services.AddScoped<IPayrollProfileService, PayrollProfileService>();
        services.AddScoped<IPayrollProviderService, PayrollProviderService>();
        services.AddScoped<IPayrollRunService, PayrollRunService>();
        return services;
    }
}
