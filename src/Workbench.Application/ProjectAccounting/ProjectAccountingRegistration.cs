using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

public static class ProjectAccountingRegistration
{
    public static IServiceCollection AddWorkbenchProjectAccounting(this IServiceCollection services)
    {
        services.AddScoped<AuditRecorder>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IPeopleService, PeopleService>();
        services.AddScoped<Payroll.LaborRateChanges>(); // Re-costs posted labor after a cost rate change (the workforce module's pay changes call it too).
        services.AddScoped<IContractService, ContractService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IFiscalCalendarService, FiscalCalendarService>();
        return services;
    }
}
