using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Identity;

// Purchasing is for Finance and administrators. The one exception: a project manager sees and decides the AP bill lines
// routed to them (the bill service itself checks the routing and refuses self-approval), and sees which vendor and bill
// their projects' ODCs came from.

public static class PurchasingPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchPurchasingPermissions(this IServiceCollection services)
    {
        services.Decorate<IVendorService, PermissionCheckingVendorService>();
        services.Decorate<ISubcontractService, PermissionCheckingSubcontractService>();
        services.Decorate<IApBillService, PermissionCheckingApBillService>();
        services.Decorate<IApPaymentService, PermissionCheckingApPaymentService>();
        services.Decorate<IApReportService, PermissionCheckingApReportService>();
        services.Decorate<IApCreditService, PermissionCheckingApCreditService>();
        return services;
    }
}

public sealed class PermissionCheckingVendorService(IVendorService inner, AccessControl access) : IVendorService
{
    private const string What = "manage vendors";

    public async Task<IReadOnlyList<VendorDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorsManage, What, cancellationToken);
        return await inner.ListAsync(includeInactive, cancellationToken);
    }

    public async Task<VendorDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorsManage, What, cancellationToken);
        return await inner.GetAsync(vendorId, cancellationToken);
    }

    public async Task<IReadOnlyList<VendorDuplicateDto>> FindDuplicatesAsync(string name, string? uei, Guid? excludeVendorId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorsManage, What, cancellationToken);
        return await inner.FindDuplicatesAsync(name, uei, excludeVendorId, cancellationToken);
    }

    public async Task<VendorDto> CreateAsync(SaveVendorRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorsManage, What, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<VendorDto> UpdateAsync(Guid vendorId, SaveVendorRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorsManage, What, cancellationToken);
        return await inner.UpdateAsync(vendorId, request, cancellationToken);
    }

    public async Task<VendorDto> SetTaxIdAsync(Guid vendorId, SetVendorTaxIdRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorTaxIds, "set a vendor's TIN", cancellationToken);
        return await inner.SetTaxIdAsync(vendorId, request, cancellationToken);
    }

    public async Task<VendorTaxIdDto> RevealTaxIdAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        // A refusal is recorded as access.denied by AccessControl; a reveal as vendor.tax_id_revealed by the service.
        await access.RequireAsync(Permission.VendorTaxIds, "see a vendor's full TIN", cancellationToken);
        return await inner.RevealTaxIdAsync(vendorId, cancellationToken);
    }

    public async Task<VendorDto> AttachW9Async(Guid vendorId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorsManage, What, cancellationToken);
        return await inner.AttachW9Async(vendorId, fileName, content, cancellationToken);
    }

    public async Task<ReceiptContent> OpenW9Async(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.VendorTaxIds, "see vendor W-9s", cancellationToken);
        return await inner.OpenW9Async(vendorId, cancellationToken);
    }

    public string GetW9Url(Guid vendorId) => inner.GetW9Url(vendorId);
}

public sealed class PermissionCheckingSubcontractService(ISubcontractService inner, AccessControl access) : ISubcontractService
{
    private const string What = "manage subcontracts";

    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.VendorsManage, What, ct);

    public async Task<IReadOnlyList<SubcontractDto>> ListAsync(Guid? vendorId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListAsync(vendorId, cancellationToken);
    }

    public async Task<SubcontractDetailDto> GetAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(subcontractId, cancellationToken);
    }

    public async Task<SubcontractDetailDto> CreateAsync(SaveSubcontractRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<SubcontractDetailDto> UpdateAsync(Guid subcontractId, SaveSubcontractRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UpdateAsync(subcontractId, request, cancellationToken);
    }

    public async Task<SubcontractDetailDto> ActivateAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ActivateAsync(subcontractId, cancellationToken);
    }

    public async Task<SubcontractDetailDto> CloseAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CloseAsync(subcontractId, cancellationToken);
    }

    public async Task<SubcontractDetailDto> AddFundingAsync(Guid subcontractId, AddSubcontractFundingRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.AddFundingAsync(subcontractId, request, cancellationToken);
    }

    public async Task<SubcontractDetailDto> SaveFlowDownAsync(Guid subcontractId, SaveFlowDownRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveFlowDownAsync(subcontractId, request, cancellationToken);
    }

    public async Task<SubcontractDetailDto> RemoveFlowDownAsync(Guid subcontractId, Guid flowDownId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RemoveFlowDownAsync(subcontractId, flowDownId, cancellationToken);
    }

    public async Task<SubcontractDetailDto> AddStandardFlowDownsAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.AddStandardFlowDownsAsync(subcontractId, cancellationToken);
    }
}

public sealed class PermissionCheckingApBillService(IApBillService inner, AccessControl access, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory) : IApBillService
{
    private const string What = "work with AP bills";

    private Task RequireFinanceAsync(CancellationToken ct) => access.RequireAsync(Permission.ApBillsManage, What, ct);

    public async Task<IReadOnlyList<BillSummaryDto>> ListAsync(BillQuery query, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    /// <summary>Finance and admins see every bill; anyone else only bills with a line routed to them (others are not found).</summary>
    public async Task<BillDto> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!await access.HasAsync(Permission.ApBillsManage, cancellationToken))
        {
            var me = access.Email;
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            if (!await db.Set<ApBillLine>().AnyAsync(l => l.BillId == billId && l.ApproverEmail == me, cancellationToken))
            {
                throw WorkbenchNotFoundException.For("Bill", billId);
            }
        }

        return await inner.GetAsync(billId, cancellationToken);
    }

    public async Task<IReadOnlyList<BillDuplicateDto>> CheckDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.CheckDuplicatesAsync(request, cancellationToken);
    }

    public async Task<BillDto> CreateAsync(SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<BillDto> UpdateAsync(Guid billId, SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.UpdateAsync(billId, request, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        await inner.DeleteDraftAsync(billId, cancellationToken);
    }

    public async Task<BillDto> SubmitAsync(Guid billId, SubmitBillRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.SubmitAsync(billId, request, cancellationToken);
    }

    public Task<IReadOnlyList<BillApprovalItemDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListApprovalQueueAsync(cancellationToken);
    }

    public Task<BillDto> ApproveAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ApproveAsync(billId, cancellationToken);
    }

    public Task<BillDto> RejectAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RejectAsync(billId, request, cancellationToken);
    }

    public async Task<BillDto> PostAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.PostAsync(billId, cancellationToken);
    }

    public async Task<BillDto> VoidAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.VoidAsync(billId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<LinkableOdcDto>> ListLinkableOdcsAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListLinkableOdcsAsync(vendorId, cancellationToken);
    }

    public async Task<IReadOnlyList<OdcBillLinkDto>> GetOdcLinksAsync(IReadOnlyCollection<Guid> odcEntryIds, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (await access.HasAsync(Permission.ApBillsManage, cancellationToken))
        {
            return await inner.GetOdcLinksAsync(odcEntryIds, cancellationToken);
        }

        // Others get links only for ODCs on projects whose financials they may read (their own projects as PM).
        var ids = odcEntryIds.Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var byProject = await db.Set<OdcEntry>().AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => new { o.Id, o.ProjectId }).ToListAsync(cancellationToken);
        var allowed = new List<Guid>();
        foreach (var group in byProject.GroupBy(o => o.ProjectId))
        {
            if (await projects.CanReadProjectFinancialsAsync(group.Key, cancellationToken))
            {
                allowed.AddRange(group.Select(o => o.Id));
            }
        }

        return allowed.Count == 0 ? [] : await inner.GetOdcLinksAsync(allowed, cancellationToken);
    }
}

/// <summary>
/// Credits are bill work (Enter and approve AP bills); refunds are cash (Pay vendors). Every document is also checked
/// against the companies the user may see.
/// </summary>
public sealed class PermissionCheckingApCreditService(IApCreditService inner, AccessControl access, Companies.CompanyGuard companies, IWorkbenchDbContextFactory dbFactory) : IApCreditService
{
    private const string What = "work with AP credits";

    private async Task RequireBillsAsync(Guid? billId, CancellationToken ct)
    {
        await access.RequireAsync(Permission.ApBillsManage, What, ct);
        if (billId is { } id)
        {
            await companies.EnsureAsync<ApBill>(id, "AP bill", ct);
        }
    }

    public async Task<BillDto> CreditBillAsync(Guid billId, CreditBillRequest request, CancellationToken cancellationToken = default)
    {
        await RequireBillsAsync(billId, cancellationToken);
        return await inner.CreditBillAsync(billId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenCreditDto>> ListOpenCreditsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(p => p.Has(Permission.ApBillsManage) || p.Has(Permission.ApPaymentsManage), "Only Finance and administrators can see vendor credits.", cancellationToken);
        return await companies.FilterAsync<ApBill, OpenCreditDto>(await inner.ListOpenCreditsAsync(vendorId, cancellationToken), c => c.Credit.Id, cancellationToken);
    }

    public async Task<BillDto> ApplyAsync(Guid creditId, ApplyCreditRequest request, CancellationToken cancellationToken = default)
    {
        await RequireBillsAsync(creditId, cancellationToken);
        await companies.EnsureAllAsync<ApBill>((request.Items ?? []).Select(i => i.BillId), "AP bill", cancellationToken);
        return await inner.ApplyAsync(creditId, request, cancellationToken);
    }

    public async Task<BillDto> UnapplyAsync(Guid applicationId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var creditId = await db.Set<ApCreditApplication>().AsNoTracking().Where(a => a.Id == applicationId).Select(a => (Guid?)a.CreditId).FirstOrDefaultAsync(cancellationToken);
            await RequireBillsAsync(creditId, cancellationToken);
        }

        return await inner.UnapplyAsync(applicationId, request, cancellationToken);
    }

    public async Task<BillDto> RecordRefundAsync(Guid creditId, RecordRefundRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.ApPaymentsManage, "record vendor refunds", cancellationToken);
        await companies.EnsureAsync<ApBill>(creditId, "AP bill", cancellationToken);
        return await inner.RecordRefundAsync(creditId, request, cancellationToken);
    }

    public async Task<BillDto> VoidRefundAsync(Guid refundId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.ApPaymentsManage, "void vendor refunds", cancellationToken);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<ApRefund>().AsNoTracking().Where(r => r.Id == refundId).Select(r => (Guid?)r.CreditId).FirstOrDefaultAsync(cancellationToken) is { } creditId)
            {
                await companies.EnsureAsync<ApBill>(creditId, "AP bill", cancellationToken);
            }
        }

        return await inner.VoidRefundAsync(refundId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<RetainageBillDto>> ListRetainageAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await RequireBillsAsync(null, cancellationToken);
        return await companies.FilterAsync<ApBill, RetainageBillDto>(await inner.ListRetainageAsync(subcontractId, cancellationToken), r => r.BillId, cancellationToken);
    }

    public async Task<BillDto> ReleaseRetainageAsync(ReleaseRetainageRequest request, CancellationToken cancellationToken = default)
    {
        await RequireBillsAsync(null, cancellationToken);
        await companies.EnsureAllAsync<ApBill>((request.Items ?? []).Select(i => i.BillId), "AP bill", cancellationToken);
        return await inner.ReleaseRetainageAsync(request, cancellationToken);
    }

    public async Task<BillDto> CreateFromQualityReturnAsync(Guid qualityDebitMemoId, CancellationToken cancellationToken = default)
    {
        await RequireBillsAsync(null, cancellationToken);
        return await inner.CreateFromQualityReturnAsync(qualityDebitMemoId, cancellationToken);
    }

    public async Task<BillTrailDto> GetTrailAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await RequireBillsAsync(billId, cancellationToken);
        return await inner.GetTrailAsync(billId, cancellationToken);
    }
}

public sealed class PermissionCheckingApPaymentService(IApPaymentService inner, AccessControl access) : IApPaymentService
{
    private const string What = "work with AP payments";

    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.ApPaymentsManage, What, ct);

    public async Task<IReadOnlyList<DueBillDto>> ListDueBillsAsync(DueBillQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListDueBillsAsync(query, cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentRunDto>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListRunsAsync(cancellationToken);
    }

    public async Task<PaymentRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetRunAsync(runId, cancellationToken);
    }

    public async Task<PaymentRunDto> CreateRunAsync(CreatePaymentRunRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateRunAsync(request, cancellationToken);
    }

    public async Task<PaymentRunDto> ApproveRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ApproveRunAsync(runId, cancellationToken);
    }

    public async Task<PaymentRunDto> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CancelRunAsync(runId, cancellationToken);
    }

    public async Task<PaymentRunDto> RecordPaymentsAsync(Guid runId, RecordPaymentsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RecordPaymentsAsync(runId, request, cancellationToken);
    }

    public async Task<PaymentDto> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.VoidPaymentAsync(paymentId, request, cancellationToken);
    }

    public async Task<PurchasingExportFile> ExportAchAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportAchAsync(runId, cancellationToken);
    }

    public async Task<PurchasingExportFile> ExportPositivePayAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportPositivePayAsync(runId, cancellationToken);
    }
}

public sealed class PermissionCheckingApReportService(IApReportService inner, AccessControl access) : IApReportService
{
    private const string What = "see AP reports";

    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.ApReportsView, What, ct);

    public async Task<AgingReportDto> GetAgingAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAgingAsync(asOf, cancellationToken);
    }

    public async Task<VendorLedgerDto> GetVendorLedgerAsync(Guid vendorId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetVendorLedgerAsync(vendorId, from, to, cancellationToken);
    }

    public async Task<Form1099ReportDto> Get1099Async(int year, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.Get1099Async(year, cancellationToken);
    }

    public async Task<IReadOnlyList<SubcontractDto>> GetSubcontractStatusAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetSubcontractStatusAsync(cancellationToken);
    }

    public async Task<SmallBusinessReportDto> GetSmallBusinessAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetSmallBusinessAsync(from, to, cancellationToken);
    }

    public async Task<PurchasingExportFile> ExportAgingCsvAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportAgingCsvAsync(asOf, cancellationToken);
    }

    public async Task<PurchasingExportFile> Export1099CsvAsync(int year, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.Export1099CsvAsync(year, cancellationToken);
    }
}
