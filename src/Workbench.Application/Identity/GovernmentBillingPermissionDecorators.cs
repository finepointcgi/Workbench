using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;

namespace Workbench.Application.Identity;

// Government billing follows billing's rules: Finance and administrators change everything; a project manager can read
// their contracts' financing, progress payment requests and events, and print their vouchers. PIEE files are Finance only.

public static class GovernmentBillingPermissionRegistration
{
    /// <summary>Called from <see cref="BillingPermissionRegistration.AddWorkbenchBillingPermissions"/>.</summary>
    public static IServiceCollection AddWorkbenchGovernmentBillingPermissions(this IServiceCollection services)
    {
        services.Decorate<IGovernmentBillingService, PermissionCheckingGovernmentBillingService>();
        services.Decorate<IProgressPaymentService, PermissionCheckingProgressPaymentService>();
        services.Decorate<IPerformanceBasedPaymentService, PermissionCheckingPerformanceBasedPaymentService>();
        return services;
    }
}

public sealed class PermissionCheckingGovernmentBillingService(IGovernmentBillingService inner, BillingAccess access) : IGovernmentBillingService
{
    public async Task<GovernmentBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.GetSettingsAsync(contractId, cancellationToken);
    }

    public async Task<GovernmentBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveGovernmentBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.SaveSettingsAsync(contractId, request, cancellationToken);
    }

    public async Task<FinancingPositionDto> GetFinancingAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.GetFinancingAsync(contractId, cancellationToken);
    }

    public async Task<VoucherDto> GetVoucherAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireInvoiceReadAsync(invoiceId, cancellationToken);
        return await inner.GetVoucherAsync(invoiceId, cancellationToken);
    }

    public async Task<PieeStatusDto> GetPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.GetPieeAsync(invoiceId, cancellationToken);
    }

    public async Task<BillingExportFile> ExportPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.ExportPieeAsync(invoiceId, cancellationToken);
    }

    public async Task<PieeSubmitResultDto> SubmitPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.SubmitPieeAsync(invoiceId, cancellationToken);
    }

    public async Task<PieeStatusDto> SetPieeStatusAsync(Guid invoiceId, SetPieeStatusRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.SetPieeStatusAsync(invoiceId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingProgressPaymentService(IProgressPaymentService inner, BillingAccess access, IWorkbenchDbContextFactory dbFactory) : IProgressPaymentService
{
    public async Task<IReadOnlyList<ProgressPaymentRequestDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.ListAsync(contractId, cancellationToken);
    }

    public async Task<ProgressPaymentRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var contractId = await db.Set<ProgressPaymentRequest>().AsNoTracking().Where(r => r.Id == requestId).Select(r => (Guid?)r.ContractId).FirstOrDefaultAsync(cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Progress payment request", requestId);
            await access.RequireContractReadAsync(contractId, cancellationToken);
        }

        return await inner.GetAsync(requestId, cancellationToken);
    }

    public async Task<Sf1443Dto> PreviewAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.PreviewAsync(request, cancellationToken);
    }

    public async Task<ProgressPaymentRequestDto> CreateAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task DeleteAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        await inner.DeleteAsync(requestId, cancellationToken);
    }
}

public sealed class PermissionCheckingPerformanceBasedPaymentService(IPerformanceBasedPaymentService inner, BillingAccess access) : IPerformanceBasedPaymentService
{
    public async Task<IReadOnlyList<PerformanceEventDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.ListAsync(contractId, cancellationToken);
    }

    public async Task<PerformanceEventDto> CreateAsync(Guid contractId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.CreateAsync(contractId, request, cancellationToken);
    }

    public async Task<PerformanceEventDto> UpdateAsync(Guid eventId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.UpdateAsync(eventId, request, cancellationToken);
    }

    public async Task<PerformanceEventDto> CompleteAsync(Guid eventId, CompletePerformanceEventRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.CompleteAsync(eventId, request, cancellationToken);
    }

    public async Task<PerformanceEventDto> ReopenAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        return await inner.ReopenAsync(eventId, cancellationToken);
    }

    public async Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(cancellationToken);
        await inner.DeleteAsync(eventId, cancellationToken);
    }
}
