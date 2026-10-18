using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Payroll;

namespace Workbench.Application.Identity;

// Payroll is for Finance and administrators only: pay groups, pay rates, hours exports, registers and the payroll
// journal. (Leave checks its own permissions inside the services, because they depend on who supervises whom.)

public static class PayrollPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchPayrollPermissions(this IServiceCollection services)
    {
        services.Decorate<IPayGroupService, PermissionCheckingPayGroupService>();
        services.Decorate<IPayrollProfileService, PermissionCheckingPayrollProfileService>();
        services.Decorate<IPayrollProviderService, PermissionCheckingPayrollProviderService>();
        services.Decorate<IPayrollRunService, PermissionCheckingPayrollRunService>();
        return services;
    }
}

public sealed class PermissionCheckingPayGroupService(IPayGroupService inner, AccessControl access) : IPayGroupService
{
    private const string What = "manage payroll";

    public async Task<IReadOnlyList<PayGroupDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<PayGroupDto> GetAsync(Guid payGroupId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetAsync(payGroupId, cancellationToken);
    }

    public async Task<PayGroupDto> CreateAsync(SavePayGroupRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<PayGroupDto> UpdateAsync(Guid payGroupId, SavePayGroupRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.UpdateAsync(payGroupId, request, cancellationToken);
    }

    public async Task<int> GeneratePeriodsAsync(Guid payGroupId, int year, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GeneratePeriodsAsync(payGroupId, year, cancellationToken);
    }

    public async Task<IReadOnlyList<PayPeriodDto>> ListPeriodsAsync(Guid payGroupId, int? year = null, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListPeriodsAsync(payGroupId, year, cancellationToken);
    }
}

public sealed class PermissionCheckingPayrollProfileService(IPayrollProfileService inner, AccessControl access) : IPayrollProfileService
{
    private const string What = "see or change payroll profiles";

    public async Task<IReadOnlyList<PayProfileDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<PayProfileDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetAsync(employeeId, cancellationToken);
    }

    public async Task<PayProfileDto> SaveAsync(Guid employeeId, SavePayProfileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveAsync(employeeId, request, cancellationToken);
    }

    public async Task<PayProfileDto> RevealAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        // A refusal is recorded as access.denied by AccessControl; a reveal as payroll.pay_rate_revealed by the service.
        await access.RequireFinanceAsync("see pay rates", cancellationToken);
        return await inner.RevealAsync(employeeId, cancellationToken);
    }
}

public sealed class PermissionCheckingPayrollProviderService(IPayrollProviderService inner, AccessControl access) : IPayrollProviderService
{
    private const string What = "manage payroll providers";

    public async Task<IReadOnlyList<PayrollProviderDto>> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListProvidersAsync(cancellationToken);
    }

    public async Task<PayrollProviderSettingsDto> GetSettingsAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetSettingsAsync(providerKey, cancellationToken);
    }

    public async Task<PayrollProviderSettingsDto> SaveSettingsAsync(string providerKey, SavePayrollProviderSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveSettingsAsync(providerKey, request, cancellationToken);
    }

    public async Task<EmployeeSyncResultDto> SyncEmployeesAsync(Guid payGroupId, bool dryRun, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SyncEmployeesAsync(payGroupId, dryRun, cancellationToken);
    }
}

public sealed class PermissionCheckingPayrollRunService(IPayrollRunService inner, AccessControl access) : IPayrollRunService
{
    private const string What = "run payroll";

    public async Task<IReadOnlyList<PayrollRunDto>> ListAsync(Guid? payGroupId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListAsync(payGroupId, cancellationToken);
    }

    public async Task<PayrollRunDto> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetAsync(runId, cancellationToken);
    }

    public async Task<PayrollRunDto> StartAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.StartAsync(payGroupId, payPeriodId, cancellationToken);
    }

    public async Task<HoursPreviewDto> PreviewHoursAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PreviewHoursAsync(runId, cancellationToken);
    }

    public async Task<PayrollFileDto> ExportHoursAsync(Guid runId, ExportHoursRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ExportHoursAsync(runId, request, cancellationToken);
    }

    public async Task<RegisterImportResultDto> ImportRegisterAsync(Guid runId, ImportRegisterRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ImportRegisterAsync(runId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<RegisterLineDto>> GetRegisterAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetRegisterAsync(runId, cancellationToken);
    }

    public async Task<PayrollReconciliationDto> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetReconciliationAsync(runId, cancellationToken);
    }

    public async Task<PayrollRunDto> PostAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("post payroll", cancellationToken);
        return await inner.PostAsync(runId, cancellationToken);
    }

    public async Task<PayrollRunDto> VoidAsync(Guid runId, VoidPayrollRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("void payroll", cancellationToken);
        return await inner.VoidAsync(runId, request, cancellationToken);
    }
}
