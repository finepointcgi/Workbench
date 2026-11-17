using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Payroll;

namespace Workbench.Application.Identity;

// The Paylocity file exchange. Mapping and the export history are payroll (setup to change the mapping, run to read the
// history and download an export); the roster import creates and changes personnel records, so it is HR's.

public static class PaylocityPermissionRegistration
{
    public static IServiceCollection AddWorkbenchPaylocityPermissions(this IServiceCollection services)
    {
        services.Decorate<IPaylocityService, PermissionCheckingPaylocityService>();
        return services;
    }
}

public sealed class PermissionCheckingPaylocityService(IPaylocityService inner, AccessControl access) : IPaylocityService
{
    public async Task<PaylocityMappingDto> GetMappingAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.PayrollSetup, "see the Paylocity mapping", cancellationToken);
        return await inner.GetMappingAsync(cancellationToken);
    }

    public async Task<PaylocityMappingDto> SaveCostCentersAsync(IReadOnlyList<SaveCostCenterMapping> mappings, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.PayrollSetup, "change the Paylocity mapping", cancellationToken);
        return await inner.SaveCostCentersAsync(mappings, cancellationToken);
    }

    public async Task<IReadOnlyList<PayrollExportRecordDto>> ListExportsAsync(Guid? payGroupId = null, Guid? payPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.PayrollRun, "see payroll exports", cancellationToken);
        return await inner.ListExportsAsync(payGroupId, payPeriodId, cancellationToken);
    }

    public async Task<PayrollExportFileDto> GetExportFileAsync(Guid exportId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.PayrollRun, "download a payroll export", cancellationToken);
        return await inner.GetExportFileAsync(exportId, cancellationToken);
    }

    public async Task<PaylocityRosterSettingsDto> GetRosterSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.HrRecordsManage, "see the roster import settings", cancellationToken);
        return await inner.GetRosterSettingsAsync(cancellationToken);
    }

    public async Task<PaylocityRosterSettingsDto> SaveRosterSettingsAsync(SavePaylocityRosterSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.HrRecordsManage, "change the roster import settings", cancellationToken);
        return await inner.SaveRosterSettingsAsync(request, cancellationToken);
    }

    public async Task<PaylocityRosterResultDto> ImportRosterAsync(PaylocityRosterRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.HrRecordsManage, "import employees from Paylocity", cancellationToken);
        return await inner.ImportRosterAsync(request, cancellationToken);
    }
}
