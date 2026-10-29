using Microsoft.Extensions.DependencyInjection;

namespace Workbench.Application.Imports.Unanet;

public static class UnanetRegistration
{
    /// <summary>The Unanet file importers (people, customers, projects and tasks, budgets, assignments, approved time, open AP/AR).</summary>
    public static IServiceCollection AddUnanetImporters(this IServiceCollection services)
    {
        services.AddScoped<IImporter, UnanetOrgUnitImporter>();
        services.AddScoped<IImporter, UnanetLaborCategoryImporter>();
        services.AddScoped<IImporter, UnanetEmployeeImporter>();
        services.AddScoped<IImporter, UnanetCostRateImporter>();
        services.AddScoped<IImporter, UnanetCustomerImporter>();
        services.AddScoped<IImporter, UnanetContractImporter>();
        services.AddScoped<IImporter, UnanetClinImporter>();
        services.AddScoped<IImporter, UnanetProjectImporter>();
        services.AddScoped<IImporter, UnanetBillRateImporter>();
        services.AddScoped<IImporter, UnanetBudgetImporter>();
        services.AddScoped<IImporter, UnanetAssignmentImporter>();
        services.AddScoped<IImporter, UnanetTimesheetImporter>();
        services.AddScoped<IImporter, UnanetVendorImporter>();
        services.AddScoped<IImporter, UnanetOpenApImporter>();
        services.AddScoped<IImporter, UnanetOpenArImporter>();
        return services;
    }
}
