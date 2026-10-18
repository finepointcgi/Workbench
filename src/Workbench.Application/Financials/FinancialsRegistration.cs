using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Financials;

namespace Workbench.Application.Financials;

public static class FinancialsRegistration
{
    public static IServiceCollection AddWorkbenchFinancials(this IServiceCollection services)
    {
        services.AddScoped<IBudgetService, BudgetService>();
        services.AddScoped<IProjectFinancialsService, ProjectFinancialsService>();
        services.AddScoped<IFinancialExportService, FinancialExportService>();
        return services;
    }
}
