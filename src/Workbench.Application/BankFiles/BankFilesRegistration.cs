using Microsoft.AspNetCore.DataProtection;
using Workbench.Application.Collaboration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Approvals;
using Workbench.Application.Cash;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Contracts.BankFiles;
using Workbench.Contracts.Identity;

namespace Workbench.Application.BankFiles;

public static class BankFilesRegistration
{
    /// <summary>
    /// Vendor ACH bank details (encrypted, changed through the vendor bank change workflow), NACHA and positive pay files from
    /// payment runs, and emailed remittance advice. Call after AddWorkbenchPurchasing, AddWorkbenchCash and the approvals, and
    /// before AddWorkbenchIdentity, which adds the permission decorators (<c>AddWorkbenchBankFilePermissions</c>). Revealing or
    /// changing vendor bank details asks for a recent sign-in (<see cref="SensitiveActions.BankDetails"/>).
    /// </summary>
    public static IServiceCollection AddWorkbenchBankFiles(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.TryAddSingleton<VendorBankProtector>();
        services.TryAddSingleton<BankAccountNumberProtector>();
        services.TryAddSingleton<IEmailSender, NullEmailSender>();
        services.AddScoped<IVendorBankService, VendorBankService>();
        services.AddScoped<IPaymentFileService, PaymentFileService>();
        services.AddScoped<IApprovalDocumentHandler, VendorBankChangeApprovalHandler>();
        services.Decorate<IVendorBankService, ReauthVendorBankService>();
        return services;
    }
}

/// <summary>Revealing or changing a vendor's bank details needs a recent sign-in (approving a change too: it makes it live).</summary>
internal sealed class ReauthVendorBankService(IVendorBankService inner, ReauthenticationGuard guard) : IVendorBankService
{
    public Task<VendorBankDetailsDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default) => inner.GetAsync(vendorId, cancellationToken);

    public Task<IReadOnlyList<VendorBankChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default) => inner.ListPendingAsync(cancellationToken);

    public async Task<VendorBankChangeDto> RequestChangeAsync(Guid vendorId, RequestVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.BankDetails, cancellationToken);
        return await inner.RequestChangeAsync(vendorId, request, cancellationToken);
    }

    public async Task<VendorBankChangeDto> ApproveChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.BankDetails, cancellationToken);
        return await inner.ApproveChangeAsync(changeId, request, cancellationToken);
    }

    public Task<VendorBankChangeDto> RejectChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default) =>
        inner.RejectChangeAsync(changeId, request, cancellationToken);

    public Task<VendorBankChangeDto> CancelChangeAsync(Guid changeId, CancellationToken cancellationToken = default) => inner.CancelChangeAsync(changeId, cancellationToken);

    public async Task<VendorBankRevealDto> RevealAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.BankDetails, cancellationToken);
        return await inner.RevealAsync(vendorId, cancellationToken);
    }
}
