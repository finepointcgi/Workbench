using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Application.Security;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;

namespace Workbench.Application.Identity;

/// <summary>
/// Sensitive actions need a recent sign-in (ISO 27001 A.8.5, NIST 800-171 3.5.x): revealing pay rates, bank account
/// numbers and vendor tax ids, changing bank accounts, approval workflows and sign-in settings. "Recent" is the
/// organization's re-authentication window (15 minutes by default). Calls without an interactive session (background
/// jobs, in-process tools) aren't affected. Refusals are <c>access.reauth_required</c> security events.
/// </summary>
public sealed class ReauthenticationGuard(ISignInContext signIn, SignInSettingsStore settings, SecurityEventLog securityLog, TimeProvider clock)
    : IReauthenticationService
{
    public event Action<ReauthenticationRequiredException>? Required;

    public async Task RequireRecentAsync(string action, CancellationToken cancellationToken = default)
    {
        if (signIn.Current is not { } facts)
        {
            return;
        }

        var window = await WindowAsync(cancellationToken);
        if (IsRecent(facts, window))
        {
            return;
        }

        await securityLog.RecordAsync(SecurityEventTypes.ReauthenticationRequired, action, SecurityOutcomes.Denied,
            facts.AuthenticatedAtUtc is { } at ? $"Signed in {(int)(clock.GetUtcNow().UtcDateTime - at).TotalMinutes} min ago; window {window} min" : "No sign-in time on the session",
            cancellationToken: cancellationToken);
        var exception = new ReauthenticationRequiredException(action, window);
        Required?.Invoke(exception);
        throw exception;
    }

    public async Task<ReauthenticationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var window = await WindowAsync(cancellationToken);
        var facts = signIn.Current;
        return new ReauthenticationStatusDto(facts?.AuthenticatedAtUtc, window, facts is null || IsRecent(facts, window), facts?.MfaPerformed ?? false, facts?.Method);
    }

    private bool IsRecent(SignInFacts facts, int windowMinutes) =>
        facts.AuthenticatedAtUtc is { } at && clock.GetUtcNow().UtcDateTime - at <= TimeSpan.FromMinutes(windowMinutes);

    private async Task<int> WindowAsync(CancellationToken cancellationToken) =>
        Math.Clamp((await settings.GetAsync(cancellationToken)).Settings.ReauthenticationMinutes, 1, 24 * 60);
}

public static class ReauthenticationRegistration
{
    /// <summary>
    /// The re-authentication guard and its decorators on the sensitive services. Register above
    /// <c>AddWorkbenchIdentity</c>, so the permission checks run first and only permitted callers are asked to sign in again.
    /// </summary>
    public static IServiceCollection AddWorkbenchReauthentication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.TryAddScoped<ISignInContext, InProcessSignInContext>();
        services.TryAddSingleton<SsoSecretProtector>();
        services.TryAddSingleton<SignInSettingsStore>();
        services.TryAddSingleton(new SignInServerInfo());
        services.AddScoped<ReauthenticationGuard>();
        services.AddScoped<IReauthenticationService>(sp => sp.GetRequiredService<ReauthenticationGuard>());
        services.AddScoped<ISignInSettingsService, SignInSettingsService>();
        services.AddScoped<IComplianceChecklistService, ComplianceChecklistService>();

        services.Decorate<IPayrollProfileService, ReauthPayrollProfileService>();
        services.Decorate<IBankAccountService, ReauthBankAccountService>();
        services.Decorate<IVendorService, ReauthVendorService>();
        services.Decorate<IApprovalWorkflowService, ReauthApprovalWorkflowService>();
        return services;
    }
}

internal sealed class ReauthPayrollProfileService(IPayrollProfileService inner, ReauthenticationGuard guard) : IPayrollProfileService
{
    public Task<IReadOnlyList<PayProfileDto>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

    public Task<PayProfileDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default) => inner.GetAsync(employeeId, cancellationToken);

    public Task<PayProfileDto> SaveAsync(Guid employeeId, SavePayProfileRequest request, CancellationToken cancellationToken = default) =>
        inner.SaveAsync(employeeId, request, cancellationToken);

    public async Task<PayProfileDto> RevealAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.PayRateReveal, cancellationToken);
        return await inner.RevealAsync(employeeId, cancellationToken);
    }

    public Task<IReadOnlyList<LaborAdjustmentDto>> ListLaborAdjustmentsAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        inner.ListLaborAdjustmentsAsync(employeeId, cancellationToken);
}

internal sealed class ReauthBankAccountService(IBankAccountService inner, ReauthenticationGuard guard) : IBankAccountService
{
    public Task<IReadOnlyList<BankAccountDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        inner.ListAsync(includeInactive, cancellationToken);

    public Task<BankAccountDto> GetAsync(Guid bankAccountId, CancellationToken cancellationToken = default) => inner.GetAsync(bankAccountId, cancellationToken);

    public async Task<BankAccountDto> CreateAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.BankDetails, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<BankAccountDto> UpdateAsync(Guid bankAccountId, SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.BankDetails, cancellationToken);
        return await inner.UpdateAsync(bankAccountId, request, cancellationToken);
    }

    public async Task<BankAccountNumberDto> RevealAccountNumberAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.BankDetails, cancellationToken);
        return await inner.RevealAccountNumberAsync(bankAccountId, cancellationToken);
    }

    public Task<IReadOnlyList<CashGlAccountDto>> ListCashGlAccountsAsync(CancellationToken cancellationToken = default) => inner.ListCashGlAccountsAsync(cancellationToken);
}

internal sealed class ReauthVendorService(IVendorService inner, ReauthenticationGuard guard) : IVendorService
{
    public Task<IReadOnlyList<VendorDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default) => inner.ListAsync(includeInactive, cancellationToken);

    public Task<VendorDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default) => inner.GetAsync(vendorId, cancellationToken);

    public Task<IReadOnlyList<VendorDuplicateDto>> FindDuplicatesAsync(string name, string? uei, Guid? excludeVendorId = null, CancellationToken cancellationToken = default) =>
        inner.FindDuplicatesAsync(name, uei, excludeVendorId, cancellationToken);

    public Task<VendorDto> CreateAsync(SaveVendorRequest request, CancellationToken cancellationToken = default) => inner.CreateAsync(request, cancellationToken);

    public Task<VendorDto> UpdateAsync(Guid vendorId, SaveVendorRequest request, CancellationToken cancellationToken = default) => inner.UpdateAsync(vendorId, request, cancellationToken);

    public Task<VendorDto> SetTaxIdAsync(Guid vendorId, SetVendorTaxIdRequest request, CancellationToken cancellationToken = default) => inner.SetTaxIdAsync(vendorId, request, cancellationToken);

    public async Task<VendorTaxIdDto> RevealTaxIdAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.VendorTaxId, cancellationToken);
        return await inner.RevealTaxIdAsync(vendorId, cancellationToken);
    }

    public Task<VendorDto> AttachW9Async(Guid vendorId, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        inner.AttachW9Async(vendorId, fileName, content, cancellationToken);

    public Task<ReceiptContent> OpenW9Async(Guid vendorId, CancellationToken cancellationToken = default) => inner.OpenW9Async(vendorId, cancellationToken);

    public string GetW9Url(Guid vendorId) => inner.GetW9Url(vendorId);
}

internal sealed class ReauthApprovalWorkflowService(IApprovalWorkflowService inner, ReauthenticationGuard guard) : IApprovalWorkflowService
{
    public Task<IReadOnlyList<ApprovalWorkflowSummaryDto>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

    public Task<ApprovalWorkflowDto> GetAsync(string documentType, CancellationToken cancellationToken = default) => inner.GetAsync(documentType, cancellationToken);

    public Task<ApprovalWorkflowDefinition> GetVersionAsync(string documentType, int version, CancellationToken cancellationToken = default) =>
        inner.GetVersionAsync(documentType, version, cancellationToken);

    public async Task<ApprovalWorkflowDto> SaveAsync(string documentType, SaveApprovalWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.ApprovalWorkflowChange, cancellationToken);
        return await inner.SaveAsync(documentType, request, cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> ResetToDefaultAsync(string documentType, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.ApprovalWorkflowChange, cancellationToken);
        return await inner.ResetToDefaultAsync(documentType, cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> CreateRecordWorkflowAsync(CreateRecordApprovalWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.ApprovalWorkflowChange, cancellationToken);
        return await inner.CreateRecordWorkflowAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<OrgUnitApproverDto>> ListOrgUnitApproversAsync(CancellationToken cancellationToken = default) => inner.ListOrgUnitApproversAsync(cancellationToken);

    public async Task<OrgUnitApproverDto> SetOrgUnitApproverAsync(Guid orgUnitId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        await guard.RequireRecentAsync(SensitiveActions.ApprovalWorkflowChange, cancellationToken);
        return await inner.SetOrgUnitApproverAsync(orgUnitId, employeeId, cancellationToken);
    }
}
