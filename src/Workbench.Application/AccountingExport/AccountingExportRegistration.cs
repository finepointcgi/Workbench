using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.AccountingExport.Formats;
using Workbench.Contracts.AccountingExport;

namespace Workbench.Application.AccountingExport;

public static class AccountingExportRegistration
{
    /// <summary>
    /// Accounting exports (QuickBooks Online CSV, QuickBooks Desktop IIF, Xero CSV, generic CSV and Excel), account mapping and the run
    /// history. A format is one <see cref="IAccountingExporter"/>; add another with one line here. Call above AddWorkbenchIdentity,
    /// which adds the permission decorators (<c>AddWorkbenchAccountingExportPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchAccountingExport(this IServiceCollection services)
    {
        services.AddScoped<IAccountingExporter, QuickBooksOnlineCsvExporter>();
        services.AddScoped<IAccountingExporter, QuickBooksIifExporter>();
        services.AddScoped<IAccountingExporter, XeroCsvExporter>();
        services.AddScoped<IAccountingExporter, GenericCsvExporter>();
        services.AddScoped<IAccountingExporter, GenericXlsxExporter>();
        services.AddScoped<IAccountingExportService, AccountingExportService>();
        services.AddScoped<IAccountingMappingService, AccountingMappingService>();
        return services;
    }
}
