using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Companies;

namespace Workbench.Application.Companies;

public static class CompanyRegistration
{
    /// <summary>
    /// Companies, intercompany accounts and consolidation. Above AddWorkbenchIdentity, whose
    /// <c>AddWorkbenchCompanyPermissions</c> adds the Finance/Admin checks and the company-access filters.
    /// </summary>
    public static IServiceCollection AddWorkbenchCompanies(this IServiceCollection services)
    {
        services.AddScoped<CompanyScope>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IIntercompanyService, IntercompanyService>();
        services.AddScoped<IConsolidationService, ConsolidationService>();
        return services;
    }
}
