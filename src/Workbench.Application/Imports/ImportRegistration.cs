using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Imports.CaptureExec;
using Workbench.Application.Imports.Costpoint;
using Workbench.Application.Imports.Smartsheet;
using Workbench.Application.Imports.Unanet;
using Workbench.Contracts.Imports;

namespace Workbench.Application.Imports;

public static class ImportRegistration
{
    /// <summary>
    /// Costpoint and Smartsheet importers behind <see cref="IImportService"/>. Needs the project-accounting, contract
    /// detail, financials, sheet and collaboration services (it imports through them). Permissions: see
    /// <c>ImportPermissionDecorators</c>, registered by <c>AddWorkbenchIdentity</c>.
    /// </summary>
    public static IServiceCollection AddWorkbenchImports(this IServiceCollection services)
    {
        // The Smartsheet token is encrypted with the host's Data Protection keys (idempotent: the web host registers it too).
        services.AddDataProtection();
        services.AddHttpClient<ISmartsheetClient, SmartsheetApiClient>(http =>
        {
            http.BaseAddress = SmartsheetApiClient.DefaultBaseAddress;
            http.Timeout = TimeSpan.FromMinutes(2);
        });
        services.AddScoped<IImporter, OrgUnitImporter>();
        services.AddScoped<IImporter, LaborCategoryImporter>();
        services.AddScoped<IImporter, EmployeeImporter>();
        services.AddScoped<IImporter, CostRateImporter>();
        services.AddScoped<IImporter, CustomerImporter>();
        services.AddScoped<IImporter, ContractImporter>();
        services.AddScoped<IImporter, ClinImporter>();
        services.AddScoped<IImporter, ProjectImporter>();
        services.AddScoped<IImporter, BillRateImporter>();
        services.AddScoped<IImporter, BudgetImporter>();
        services.AddScoped<IImporter, SmartsheetApiSheetImporter>();
        services.AddScoped<IImporter, SmartsheetExcelImporter>();
        services.AddScoped<IImporter, FixedAssetImporter>();
        services.AddScoped<IImporter, AccountImporter>();
        services.AddScoped<IImporter, VendorImporter>();
        services.AddScoped<IImporter, OpeningBalanceImporter>();
        services.AddScoped<IImporter, OpenArInvoiceImporter>();
        services.AddScoped<IImporter, OpenApBillImporter>();
        services.AddScoped<IImporter, FiscalCalendarImporter>();
        services.AddScoped<IImporter, IndirectRateImporter>();
        services.AddScoped<IImporter, IndirectPoolImporter>();
        services.AddUnanetImporters();
        services.AddScoped<IImporter, CaptureExecAccountImporter>();
        services.AddScoped<IImporter, CaptureExecContactImporter>();
        services.AddScoped<IImporter, CaptureExecOpportunityImporter>();
        services.AddScoped<OpenItemConversion>();
        services.AddScoped<ImportRollback>();
        services.AddScoped<IImportService, ImportService>();
        return services;
    }
}
