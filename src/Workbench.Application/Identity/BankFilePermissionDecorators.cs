using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.BankFiles;

namespace Workbench.Application.Identity;

// Bank payment files and vendor bank details: Finance or Admin only. The vendor bank service itself makes sure nobody
// approves the change they asked for.

public static class BankFilePermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchBankFilePermissions(this IServiceCollection services)
    {
        services.Decorate<IVendorBankService, PermissionCheckingVendorBankService>();
        services.Decorate<IPaymentFileService, PermissionCheckingPaymentFileService>();
        return services;
    }
}

public sealed class PermissionCheckingVendorBankService(IVendorBankService inner, AccessControl access) : IVendorBankService
{
    private const string What = "see or change vendor bank details";

    public async Task<VendorBankDetailsDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetAsync(vendorId, cancellationToken);
    }

    public async Task<IReadOnlyList<VendorBankChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListPendingAsync(cancellationToken);
    }

    public async Task<VendorBankChangeDto> RequestChangeAsync(Guid vendorId, RequestVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.RequestChangeAsync(vendorId, request, cancellationToken);
    }

    public async Task<VendorBankChangeDto> ApproveChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ApproveChangeAsync(changeId, request, cancellationToken);
    }

    public async Task<VendorBankChangeDto> RejectChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.RejectChangeAsync(changeId, request, cancellationToken);
    }

    public async Task<VendorBankChangeDto> CancelChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.CancelChangeAsync(changeId, cancellationToken);
    }

    public async Task<VendorBankRevealDto> RevealAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.RevealAsync(vendorId, cancellationToken);
    }
}

public sealed class PermissionCheckingPaymentFileService(IPaymentFileService inner, AccessControl access, Companies.CompanyGuard companies) : IPaymentFileService
{
    private const string What = "make bank payment files";

    public async Task<IReadOnlyList<BankFileSettingsDto>> ListBankAccountsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListBankAccountsAsync(cancellationToken);
    }

    public async Task<BankFileSettingsDto> GetSettingsAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetSettingsAsync(bankAccountId, cancellationToken);
    }

    public async Task<BankFileSettingsDto> SaveSettingsAsync(Guid bankAccountId, SaveBankFileSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveSettingsAsync(bankAccountId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<PositivePayLayoutDto>> ListLayoutsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListLayoutsAsync(cancellationToken);
    }

    public async Task<PositivePayLayoutDto> SaveLayoutAsync(Guid? layoutId, SavePositivePayLayoutRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveLayoutAsync(layoutId, request, cancellationToken);
    }

    public async Task<PaymentRunFilesDto> GetRunFilesAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        await companies.EnsureAsync<Domain.Purchasing.ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.GetRunFilesAsync(runId, cancellationToken);
    }

    public async Task<PaymentFileDto> GenerateAsync(Guid runId, GeneratePaymentFileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        await companies.EnsureAsync<Domain.Purchasing.ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.GenerateAsync(runId, request, cancellationToken);
    }

    public async Task<RemittanceResultDto> SendRemittanceAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        await companies.EnsureAsync<Domain.Purchasing.ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.SendRemittanceAsync(runId, cancellationToken);
    }
}
