using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Compliance;

namespace Workbench.Application.Compliance;

public static class ComplianceRegistration
{
    /// <summary>
    /// Purchasing system review (CPSR) files and readiness, and CAS disclosure statements with the 401/402 consistency
    /// check. Binds <see cref="CpsrOptions"/> from config <c>Compliance:Cpsr</c>. RFQ competition evidence is a no-op until
    /// the procurement module registers its <see cref="IProcurementCompetitionSource"/> (a later <c>AddScoped</c> wins over
    /// this <c>TryAdd</c>). Register above <c>AddWorkbenchIdentity</c>, which adds the permission decorators.
    /// </summary>
    public static IServiceCollection AddWorkbenchCompliance(this IServiceCollection services)
    {
        services.AddOptions<CpsrOptions>().BindConfiguration(CpsrOptions.Section);
        services.TryAddScoped<IProcurementCompetitionSource, NoCompetitionSource>();
        services.AddScoped<ICpsrService, CpsrService>();
        services.AddScoped<ICasDisclosureService, CasDisclosureService>();
        return services;
    }
}
