using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

// Commercial billing uses the billing permissions that already exist: agreements, milestones and settings are "Bill customers"
// (a project manager may read the plan and milestones of a contract their projects are on); credits and write-offs are "Record
// customer payments"; the aging and reminders are "See receivables".

public static class CommercialBillingPermissionRegistration
{
    public static IServiceCollection AddWorkbenchCommercialBillingPermissions(this IServiceCollection services)
    {
        services.Decorate<IAgreementBillingService, PermissionCheckingAgreementBillingService>();
        services.Decorate<IReceivablesService, PermissionCheckingReceivablesService>();
        services.Decorate<IBillingSettingsService, PermissionCheckingBillingSettingsService>();
        return services;
    }
}

public sealed class PermissionCheckingAgreementBillingService(IAgreementBillingService inner, BillingAccess access) : IAgreementBillingService
{
    public async Task<AgreementBillingPlanDto?> GetPlanAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.GetPlanAsync(contractId, cancellationToken);
    }

    public async Task<IReadOnlyList<AgreementBillingPlanDto>> ListPlansAsync(CancellationToken cancellationToken = default)
    {
        var readable = await access.ReadableContractsAsync(cancellationToken);
        var all = await inner.ListPlansAsync(cancellationToken);
        return readable is null ? all : all.Where(p => readable.Contains(p.ContractId)).ToList();
    }

    public async Task<AgreementBillingPlanDto> SavePlanAsync(Guid contractId, SaveAgreementBillingPlanRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.SavePlanAsync(contractId, request, cancellationToken);
    }

    public async Task RemovePlanAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        await inner.RemovePlanAsync(contractId, cancellationToken);
    }

    public async Task<IReadOnlyList<AgreementMilestoneDto>> ListMilestonesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.ListMilestonesAsync(contractId, cancellationToken);
    }

    public async Task<AgreementMilestoneDto> CreateMilestoneAsync(Guid contractId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.CreateMilestoneAsync(contractId, request, cancellationToken);
    }

    public async Task<AgreementMilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.UpdateMilestoneAsync(milestoneId, request, cancellationToken);
    }

    public async Task<AgreementMilestoneCompletionDto> CompleteMilestoneAsync(Guid milestoneId, CompleteAgreementMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.CompleteMilestoneAsync(milestoneId, request, cancellationToken);
    }

    public async Task<AgreementMilestoneDto> ReopenMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.ReopenMilestoneAsync(milestoneId, cancellationToken);
    }

    public async Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        await inner.DeleteMilestoneAsync(milestoneId, cancellationToken);
    }

    public async Task<IReadOnlyList<DueAgreementPeriodDto>> ListDuePeriodsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.ListDuePeriodsAsync(asOf, cancellationToken);
    }

    public async Task<GenerateDueInvoicesResultDto> GenerateDueDraftsAsync(DateOnly? asOf = null, Guid? contractId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.GenerateDueDraftsAsync(asOf, contractId, cancellationToken);
    }
}

public sealed class PermissionCheckingReceivablesService(IReceivablesService inner, BillingAccess access) : IReceivablesService
{
    public async Task<ReceivablesAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.GetAgingAsync(asOf, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerCreditDto>> ListCreditsAsync(Guid? customerId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.ListCreditsAsync(customerId, cancellationToken);
    }

    public async Task<PaymentDto> ApplyCreditAsync(Guid paymentId, ApplyCreditRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.ApplyCreditAsync(paymentId, request, cancellationToken);
    }

    public async Task<WriteOffDto> WriteOffAsync(Guid invoiceId, WriteOffInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.WriteOffAsync(invoiceId, request, cancellationToken);
    }

    public async Task<WriteOffDto> ReverseWriteOffAsync(Guid writeOffId, ReverseWriteOffRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.ReverseWriteOffAsync(writeOffId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<WriteOffDto>> ListWriteOffsAsync(Guid? invoiceId = null, Guid? customerId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.ListWriteOffsAsync(invoiceId, customerId, cancellationToken);
    }

    public async Task<int> SendPaymentRemindersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.SendPaymentRemindersAsync(cancellationToken);
    }
}

public sealed class PermissionCheckingBillingSettingsService(IBillingSettingsService inner, BillingAccess access) : IBillingSettingsService
{
    public async Task<BillingSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.GetAsync(cancellationToken);
    }

    public async Task<BillingSettingsDto> SaveAsync(SaveBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.SaveAsync(request, cancellationToken);
    }
}
