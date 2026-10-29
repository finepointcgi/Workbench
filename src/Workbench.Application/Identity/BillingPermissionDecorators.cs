using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Domain.Billing;

namespace Workbench.Application.Identity;

// Billing and AR are for Finance and administrators. A project manager can read (and print) the invoices and milestones
// of contracts their projects are on, and the contract's billed-to-date summary; nothing else.

public static class BillingPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchBillingPermissions(this IServiceCollection services)
    {
        services.Decorate<IInvoiceService, PermissionCheckingInvoiceService>();
        services.Decorate<ICustomerShareLinkService, PermissionCheckingCustomerShareLinkService>();
        services.Decorate<IBillingMilestoneService, PermissionCheckingBillingMilestoneService>();
        services.Decorate<IPaymentService, PermissionCheckingPaymentService>();
        services.Decorate<IArReportService, PermissionCheckingArReportService>();
        services.AddWorkbenchGovernmentBillingPermissions();
        return services;
    }
}

/// <summary>Finance/Admin, or the manager of a project on the contract (read only).</summary>
public sealed class BillingAccess(ProjectAccess projects, IWorkbenchDbContextFactory dbFactory)
{
    public const string Finance = "work with billing and receivables";

    public Task RequireAsync(string permission, CancellationToken ct) => projects.Access.RequireAsync(permission, Finance, ct);

    // Billing people read every contract's invoices; others, the contracts of projects they manage (every contract with
    // "See every project's financials").
    private async Task<IReadOnlySet<Guid>?> ManagedAsync(CancellationToken ct) =>
        await projects.Access.HasAsync(Permission.BillingInvoicesManage, ct) ? null : await projects.ManagedContractIdsAsync(ct);

    public async Task RequireContractReadAsync(Guid contractId, CancellationToken ct)
    {
        projects.Access.RequireSignedIn();
        var managed = await ManagedAsync(ct);
        if (managed is not null && !managed.Contains(contractId))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the managers of the contract's projects can see its invoices.");
        }
    }

    public async Task RequireInvoiceReadAsync(Guid invoiceId, CancellationToken ct)
    {
        projects.Access.RequireSignedIn();
        var managed = await ManagedAsync(ct);
        if (managed is null)
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var contractId = await db.Set<Invoice>().AsNoTracking().Where(i => i.Id == invoiceId).Select(i => (Guid?)i.ContractId).FirstOrDefaultAsync(ct)
            ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        if (!managed.Contains(contractId))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the managers of the contract's projects can see this invoice.");
        }
    }

    /// <summary>Contracts whose invoices the user may read: null for Finance/Admin (all); refuses users who manage none.</summary>
    public async Task<IReadOnlySet<Guid>?> ReadableContractsAsync(CancellationToken ct)
    {
        projects.Access.RequireSignedIn();
        var managed = await ManagedAsync(ct);
        return managed is { Count: 0 }
            ? throw new WorkbenchForbiddenException("Billing is for Finance, administrators and project managers (for their contracts).")
            : managed;
    }
}

public sealed class PermissionCheckingInvoiceService(IInvoiceService inner, BillingAccess access) : IInvoiceService
{
    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(InvoiceQuery query, CancellationToken cancellationToken = default)
    {
        var readable = await access.ReadableContractsAsync(cancellationToken);
        if (readable is null)
        {
            return await inner.ListAsync(query, cancellationToken);
        }

        if (query.ContractId is { } c && !readable.Contains(c))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the managers of the contract's projects can see its invoices.");
        }

        return (await inner.ListAsync(query, cancellationToken)).Where(i => readable.Contains(i.ContractId)).ToList();
    }

    public async Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireInvoiceReadAsync(invoiceId, cancellationToken);
        return await inner.GetAsync(invoiceId, cancellationToken);
    }

    public async Task<IReadOnlyList<BillingContractDto>> ListBillingContractsAsync(CancellationToken cancellationToken = default)
    {
        var readable = await access.ReadableContractsAsync(cancellationToken);
        var all = await inner.ListBillingContractsAsync(cancellationToken);
        return readable is null ? all : all.Where(c => readable.Contains(c.Id)).ToList();
    }

    public async Task<InvoicePreviewDto> PreviewAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.PreviewAsync(request, cancellationToken);
    }

    public async Task<InvoiceDto> GenerateDraftAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.GenerateDraftAsync(request, cancellationToken);
    }

    public async Task<InvoiceDto> RegenerateDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.RegenerateDraftAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, UpdateInvoiceDraftRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.UpdateDraftAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> AddLineAsync(Guid invoiceId, AddInvoiceLineRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.AddLineAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.RemoveLineAsync(invoiceId, lineId, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        await inner.DeleteDraftAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceDto> ApproveAsync(Guid invoiceId, ApproveInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.ApproveAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> MarkSentAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.MarkSentAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.VoidAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> CreateCreditMemoAsync(Guid invoiceId, CreditMemoRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.CreateCreditMemoAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoicePrintDto> GetPrintAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireInvoiceReadAsync(invoiceId, cancellationToken);
        return await inner.GetPrintAsync(invoiceId, cancellationToken);
    }

    public async Task<BillingExportFile> ExportWawfCsvAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.ExportWawfCsvAsync(invoiceId, cancellationToken);
    }

    public async Task<ContractBillingSummaryDto> GetContractSummaryAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.GetContractSummaryAsync(contractId, cancellationToken);
    }

    public async Task<ContractBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.GetSettingsAsync(contractId, cancellationToken);
    }

    public async Task<ContractBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveContractBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.SaveSettingsAsync(contractId, request, cancellationToken);
    }

    public async Task<UnbilledAccrualDto> GetUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.GetUnbilledAccrualAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<UnbilledAccrualDto> PostUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.PostUnbilledAccrualAsync(fiscalPeriodId, cancellationToken);
    }
}

public sealed class PermissionCheckingBillingMilestoneService(IBillingMilestoneService inner, BillingAccess access) : IBillingMilestoneService
{
    public async Task<IReadOnlyList<BillingMilestoneDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireContractReadAsync(contractId, cancellationToken);
        return await inner.ListAsync(contractId, cancellationToken);
    }

    public async Task<BillingMilestoneDto> CreateAsync(Guid contractId, SaveMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.CreateAsync(contractId, request, cancellationToken);
    }

    public async Task<BillingMilestoneDto> UpdateAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.UpdateAsync(milestoneId, request, cancellationToken);
    }

    public async Task<BillingMilestoneDto> CompleteAsync(Guid milestoneId, CompleteMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.CompleteAsync(milestoneId, request, cancellationToken);
    }

    public async Task<BillingMilestoneDto> ReopenAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.ReopenAsync(milestoneId, cancellationToken);
    }

    public async Task DeleteAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        await inner.DeleteAsync(milestoneId, cancellationToken);
    }
}

public sealed class PermissionCheckingPaymentService(IPaymentService inner, BillingAccess access) : IPaymentService
{
    public async Task<IReadOnlyList<PaymentDto>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<PaymentDto> GetAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.GetAsync(paymentId, cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListOpenInvoicesAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.ListOpenInvoicesAsync(customerId, cancellationToken);
    }

    public async Task<PaymentDto> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.RecordAsync(request, cancellationToken);
    }

    public async Task<PaymentDto> VoidAsync(Guid paymentId, VoidInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingPaymentsRecord, cancellationToken);
        return await inner.VoidAsync(paymentId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingArReportService(IArReportService inner, BillingAccess access) : IArReportService
{
    public async Task<ArAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.GetAgingAsync(asOf, cancellationToken);
    }

    public async Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.GetStatementAsync(customerId, from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<BillingCustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.ListCustomersAsync(cancellationToken);
    }

    public async Task<int> SendOverdueRemindersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingReportsView, cancellationToken);
        return await inner.SendOverdueRemindersAsync(cancellationToken);
    }
}

/// <summary>Customer links are Finance's (billing managers): they decide what a customer may see and for how long.</summary>
public sealed class PermissionCheckingCustomerShareLinkService(ICustomerShareLinkService inner, BillingAccess access) : ICustomerShareLinkService
{
    public async Task<IReadOnlyList<CustomerShareLinkDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.ListAsync(contractId, cancellationToken);
    }

    public async Task<CreatedCustomerShareLinkDto> CreateAsync(Guid contractId, CreateCustomerShareLinkRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.CreateAsync(contractId, request, cancellationToken);
    }

    public async Task<CustomerShareLinkDto> RevokeAsync(Guid contractId, Guid linkId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, cancellationToken);
        return await inner.RevokeAsync(contractId, linkId, cancellationToken);
    }
}
