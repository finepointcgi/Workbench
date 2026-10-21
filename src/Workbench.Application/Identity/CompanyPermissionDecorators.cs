using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Companies;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using BillingPaymentDto = Workbench.Contracts.Billing.PaymentDto;
using PaymentDto = Workbench.Contracts.Purchasing.PaymentDto;
using Payment = Workbench.Domain.Billing.Payment;

namespace Workbench.Application.Identity;

// Multicompany permissions. Companies, intercompany setup and consolidation are Finance/Admin; who may use which company is
// Admin. Every user's company list (empty = all) and the company they're working in limit the company-owned records:
// journal entries, contracts, projects, invoices, AR receipts, AP bills and payment runs, fixed assets and bank accounts.
// A record in a company the user can't see is not found; lists drop it. The ledger reports filter by the same companies
// inside AccountingReportService (sums can't be filtered afterwards). Employees, vendors, customers, labor categories and
// the chart of accounts are shared by every company, and anyone may charge time to another company's project
// (intercompany labor), so charge codes and people aren't filtered.

public static class CompanyPermissionRegistration
{
    /// <summary>Called last in <see cref="IdentityRegistration.AddWorkbenchIdentity"/>: the company filters wrap the role checks.</summary>
    public static IServiceCollection AddWorkbenchCompanyPermissions(this IServiceCollection services)
    {
        services.AddScoped<CompanyGuard>();
        services.Decorate<ICompanyService, PermissionCheckingCompanyService>();
        services.Decorate<IIntercompanyService, PermissionCheckingIntercompanyService>();
        services.Decorate<IConsolidationService, PermissionCheckingConsolidationService>();
        services.Decorate<IJournalService, CompanyScopedJournalService>();
        services.Decorate<IProjectService, CompanyScopedProjectService>();
        services.Decorate<IContractService, CompanyScopedContractService>();
        services.Decorate<IPeopleService, CompanyScopedPeopleService>();
        services.Decorate<IInvoiceService, CompanyScopedInvoiceService>();
        services.Decorate<IPaymentService, CompanyScopedPaymentService>();
        services.Decorate<IApBillService, CompanyScopedApBillService>();
        services.Decorate<IApPaymentService, CompanyScopedApPaymentService>();
        services.Decorate<IAssetService, CompanyScopedAssetService>();
        services.Decorate<IBankAccountService, CompanyScopedBankAccountService>();
        return services;
    }
}

public sealed class PermissionCheckingCompanyService(ICompanyService inner, AccessControl access) : ICompanyService
{
    private const string What = "manage companies";

    public Task<CompanyContextDto> GetContextAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetContextAsync(cancellationToken);
    }

    public Task<CompanyContextDto> SelectAsync(SelectCompanyRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.SelectAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.ListAsync(includeInactive, cancellationToken);
    }

    public async Task<CompanyDto> CreateAsync(SaveCompanyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<CompanyDto> UpdateAsync(Guid companyId, SaveCompanyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.UpdateAsync(companyId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<UserCompanyAccessDto>> ListUserAccessAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "change which companies people can see", cancellationToken);
        return await inner.ListUserAccessAsync(cancellationToken);
    }

    public async Task<UserCompanyAccessDto> SetUserAccessAsync(Guid userId, SetUserCompanyAccessRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "change which companies people can see", cancellationToken);
        return await inner.SetUserAccessAsync(userId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyRecordDto>> ListRecordsAsync(CompanyRecordKind kind, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.ListRecordsAsync(kind, cancellationToken);
    }

    public async Task<CompanyRecordDto> AssignAsync(AssignCompanyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.AssignAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyPeriodDto>> ListPeriodsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.ListPeriodsAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<CompanyPeriodDto> SetPeriodClosedAsync(Guid companyId, Guid fiscalPeriodId, SetCompanyPeriodRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlClose, "close periods", cancellationToken);
        return await inner.SetPeriodClosedAsync(companyId, fiscalPeriodId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingIntercompanyService(IIntercompanyService inner, AccessControl access) : IIntercompanyService
{
    private const string What = "set up intercompany accounts";

    public async Task<IReadOnlyList<IntercompanyAccountDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<IntercompanyAccountDto> SaveAsync(SaveIntercompanyAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupCompanies, What, cancellationToken);
        return await inner.SaveAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<IntercompanyBalanceDto>> ListBalancesAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlReportsView, "see intercompany balances", cancellationToken);
        return await inner.ListBalancesAsync(fiscalPeriodId, cancellationToken);
    }
}

/// <summary>Finance/Admin; the service itself keeps the consolidation to companies the user may use.</summary>
public sealed class PermissionCheckingConsolidationService(IConsolidationService inner, AccessControl access) : IConsolidationService
{
    public async Task<ConsolidationDto> GetAsync(ConsolidatedReport report, ConsolidationRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlReportsView, "see consolidated statements", cancellationToken);
        return await inner.GetAsync(report, request, cancellationToken);
    }
}

public sealed class CompanyScopedJournalService(IJournalService inner, CompanyGuard guard) : IJournalService
{
    public async Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<JournalEntry, JournalEntrySummaryDto>(await inner.ListAsync(query, cancellationToken), e => e.Id, cancellationToken);
    }

    public async Task<JournalEntryDto> GetAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<JournalEntry>(entryId, "Journal entry", cancellationToken);
        return await inner.GetAsync(entryId, cancellationToken);
    }

    public async Task<JournalEntryDto> CreateDraftAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        request = await WithCompanyAsync(request, cancellationToken);
        return await inner.CreateDraftAsync(request, cancellationToken);
    }

    public async Task<JournalEntryDto> UpdateDraftAsync(Guid entryId, SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<JournalEntry>(entryId, "Journal entry", cancellationToken);
        request = await WithCompanyAsync(request, cancellationToken);
        return await inner.UpdateDraftAsync(entryId, request, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<JournalEntry>(entryId, "Journal entry", cancellationToken);
        await inner.DeleteDraftAsync(entryId, cancellationToken);
    }

    public async Task<JournalEntryDto> PostAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<JournalEntry>(entryId, "Journal entry", cancellationToken);
        return await inner.PostAsync(entryId, cancellationToken);
    }

    public async Task<JournalEntryDto> CreateAndPostAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        request = await WithCompanyAsync(request, cancellationToken);
        return await inner.CreateAndPostAsync(request, cancellationToken);
    }

    public async Task<JournalEntryDto> ReverseAsync(Guid entryId, ReverseJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<JournalEntry>(entryId, "Journal entry", cancellationToken);
        return await inner.ReverseAsync(entryId, request, cancellationToken);
    }

    /// <summary>A manual entry is entered in the company the user is working in, unless the request names one they can use.</summary>
    private async Task<SaveJournalEntryRequest> WithCompanyAsync(SaveJournalEntryRequest request, CancellationToken ct)
    {
        var view = await guard.ViewAsync(ct);
        if (request.CompanyId is { } company)
        {
            return view.Visible is { } visible && !visible.Contains(company) ? throw WorkbenchNotFoundException.For("Company", company) : request;
        }

        return view.IsMulticompany ? request with { CompanyId = view.ForNewRecords } : request;
    }
}

public sealed class CompanyScopedProjectService(IProjectService inner, CompanyGuard guard, IWorkbenchDbContextFactory dbFactory) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<Project, ProjectDto>(await inner.ListProjectsAsync(cancellationToken), p => p.Id, cancellationToken);
    }

    public async Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await guard.IsVisibleAsync<Project>(projectId, cancellationToken) ? await inner.GetProjectAsync(projectId, cancellationToken) : null;

    public async Task<ProjectDto?> GetProjectForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        var result = await inner.GetProjectForSheetAsync(sheetId, cancellationToken);
        return result is not null && await guard.IsVisibleAsync<Project>(result.Id, cancellationToken) ? result : null;
    }

    public async Task<ProjectDto> CreateProjectAsync(SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ContractId is { } contractId)
        {
            await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        }

        var result = await inner.CreateProjectAsync(request, cancellationToken);
        if (request.ContractId is null)
        {
            await guard.StampNewAsync<Project>(result.Id, cancellationToken);
        }

        return result;
    }

    public async Task<ProjectDto> UpdateProjectAsync(Guid projectId, SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Project>(projectId, "Project", cancellationToken);
        if (request.ContractId is { } contractId)
        {
            await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        }

        return await inner.UpdateProjectAsync(projectId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<WbsElementDto>> ListWbsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Project>(projectId, "Project", cancellationToken);
        return await inner.ListWbsAsync(projectId, cancellationToken);
    }

    public async Task<WbsElementDto> CreateWbsElementAsync(Guid projectId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Project>(projectId, "Project", cancellationToken);
        return await inner.CreateWbsElementAsync(projectId, request, cancellationToken);
    }

    public async Task<WbsElementDto> UpdateWbsElementAsync(Guid wbsElementId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureWbsAsync(wbsElementId, cancellationToken);
        return await inner.UpdateWbsElementAsync(wbsElementId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<ChargeAssignmentDto>> ListAssignmentsAsync(Guid wbsElementId, CancellationToken cancellationToken = default)
    {
        await EnsureWbsAsync(wbsElementId, cancellationToken);
        return await inner.ListAssignmentsAsync(wbsElementId, cancellationToken);
    }

    public async Task<ChargeAssignmentDto> AddAssignmentAsync(Guid wbsElementId, AddChargeAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureWbsAsync(wbsElementId, cancellationToken);
        return await inner.AddAssignmentAsync(wbsElementId, request, cancellationToken);
    }

    public Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) => inner.RemoveAssignmentAsync(assignmentId, cancellationToken);

    public Task<IReadOnlyList<ChargeCodeDto>> ListChargeCodesAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default) => inner.ListChargeCodesAsync(employeeId, date, cancellationToken);

    private async Task EnsureWbsAsync(Guid wbsElementId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var projectId = await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == wbsElementId).Select(w => (Guid?)w.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is { } id)
        {
            await guard.EnsureAsync<Project>(id, "WBS element", ct);
        }
    }
}

public sealed class CompanyScopedContractService(IContractService inner, CompanyGuard guard) : IContractService
{
    public Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default) => inner.ListCustomersAsync(cancellationToken);

    public Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default) => inner.CreateCustomerAsync(request, cancellationToken);

    public async Task<IReadOnlyList<ContractDto>> ListContractsAsync(CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<Contract, ContractDto>(await inner.ListContractsAsync(cancellationToken), c => c.Id, cancellationToken);
    }

    public async Task<ContractDto?> GetContractAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await guard.IsVisibleAsync<Contract>(contractId, cancellationToken) ? await inner.GetContractAsync(contractId, cancellationToken) : null;

    public async Task<ContractDto> CreateContractAsync(SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateContractAsync(request, cancellationToken);
        await guard.StampNewAsync<Contract>(result.Id, cancellationToken);
        return result;
    }

    public async Task<ContractDto> UpdateContractAsync(Guid contractId, SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        return await inner.UpdateContractAsync(contractId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<BillRateDto>> ListBillRatesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        return await inner.ListBillRatesAsync(contractId, cancellationToken);
    }

    public async Task<BillRateDto> AddBillRateAsync(Guid contractId, AddBillRateRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        return await inner.AddBillRateAsync(contractId, request, cancellationToken);
    }
}

public sealed class CompanyScopedPeopleService(IPeopleService inner, CompanyGuard guard) : IPeopleService
{
    public Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) => inner.ListEmployeesAsync(includeInactive, cancellationToken);

    public Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) => inner.GetEmployeeAsync(employeeId, cancellationToken);

    public Task<EmployeeDto?> GetCurrentEmployeeAsync(CancellationToken cancellationToken = default) => inner.GetCurrentEmployeeAsync(cancellationToken);

    public async Task<EmployeeDto> CreateEmployeeAsync(SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateEmployeeAsync(request, cancellationToken);
        await guard.StampNewAsync<Employee>(result.Id, cancellationToken);
        return result;
    }

    public Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken = default) => inner.UpdateEmployeeAsync(employeeId, request, cancellationToken);

    public Task<IReadOnlyList<OrgUnitDto>> ListOrgUnitsAsync(CancellationToken cancellationToken = default) => inner.ListOrgUnitsAsync(cancellationToken);

    public Task<OrgUnitDto> CreateOrgUnitAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken = default) => inner.CreateOrgUnitAsync(request, cancellationToken);

    public Task<IReadOnlyList<LaborCategoryDto>> ListLaborCategoriesAsync(CancellationToken cancellationToken = default) => inner.ListLaborCategoriesAsync(cancellationToken);

    public Task<LaborCategoryDto> CreateLaborCategoryAsync(SaveLaborCategoryRequest request, CancellationToken cancellationToken = default) => inner.CreateLaborCategoryAsync(request, cancellationToken);

    public Task<LaborCategoryDto> UpdateLaborCategoryAsync(Guid laborCategoryId, SaveLaborCategoryRequest request, CancellationToken cancellationToken = default) => inner.UpdateLaborCategoryAsync(laborCategoryId, request, cancellationToken);

    public Task<IReadOnlyList<CostRateDto>> ListCostRatesAsync(Guid employeeId, CancellationToken cancellationToken = default) => inner.ListCostRatesAsync(employeeId, cancellationToken);

    public Task<CostRateDto> AddCostRateAsync(Guid employeeId, AddCostRateRequest request, CancellationToken cancellationToken = default) => inner.AddCostRateAsync(employeeId, request, cancellationToken);
}

public sealed class CompanyScopedInvoiceService(IInvoiceService inner, CompanyGuard guard) : IInvoiceService
{
    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(InvoiceQuery query, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<Invoice, InvoiceSummaryDto>(await inner.ListAsync(query, cancellationToken), i => i.Id, cancellationToken);
    }

    public async Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.GetAsync(invoiceId, cancellationToken);
    }

    public async Task<IReadOnlyList<BillingContractDto>> ListBillingContractsAsync(CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<Contract, BillingContractDto>(await inner.ListBillingContractsAsync(cancellationToken), c => c.Id, cancellationToken);
    }

    public async Task<InvoicePreviewDto> PreviewAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(request.ContractId, "Contract", cancellationToken);
        return await inner.PreviewAsync(request, cancellationToken);
    }

    public async Task<InvoiceDto> GenerateDraftAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(request.ContractId, "Contract", cancellationToken);
        return await inner.GenerateDraftAsync(request, cancellationToken);
    }

    public async Task<InvoiceDto> RegenerateDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.RegenerateDraftAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, UpdateInvoiceDraftRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.UpdateDraftAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> AddLineAsync(Guid invoiceId, AddInvoiceLineRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.AddLineAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.RemoveLineAsync(invoiceId, lineId, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        await inner.DeleteDraftAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceDto> ApproveAsync(Guid invoiceId, ApproveInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.ApproveAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> MarkSentAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.MarkSentAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.VoidAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoiceDto> CreateCreditMemoAsync(Guid invoiceId, CreditMemoRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.CreateCreditMemoAsync(invoiceId, request, cancellationToken);
    }

    public async Task<InvoicePrintDto> GetPrintAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.GetPrintAsync(invoiceId, cancellationToken);
    }

    public async Task<BillingExportFile> ExportWawfCsvAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.ExportWawfCsvAsync(invoiceId, cancellationToken);
    }

    public async Task<ContractBillingSummaryDto> GetContractSummaryAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        return await inner.GetContractSummaryAsync(contractId, cancellationToken);
    }

    public async Task<ContractBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        return await inner.GetSettingsAsync(contractId, cancellationToken);
    }

    public async Task<ContractBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveContractBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Contract>(contractId, "Contract", cancellationToken);
        return await inner.SaveSettingsAsync(contractId, request, cancellationToken);
    }

    public Task<UnbilledAccrualDto> GetUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.GetUnbilledAccrualAsync(fiscalPeriodId, cancellationToken);

    public Task<UnbilledAccrualDto> PostUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.PostUnbilledAccrualAsync(fiscalPeriodId, cancellationToken);
}

public sealed class CompanyScopedPaymentService(IPaymentService inner, CompanyGuard guard) : IPaymentService
{
    public async Task<IReadOnlyList<BillingPaymentDto>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<Payment, BillingPaymentDto>(await inner.ListAsync(query, cancellationToken), p => p.Id, cancellationToken);
    }

    public async Task<BillingPaymentDto> GetAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Payment>(paymentId, "Payment", cancellationToken);
        return await inner.GetAsync(paymentId, cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListOpenInvoicesAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<Invoice, InvoiceSummaryDto>(await inner.ListOpenInvoicesAsync(customerId, cancellationToken), i => i.Id, cancellationToken);
    }

    public async Task<BillingPaymentDto> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAllAsync<Invoice>((request.Applications ?? []).Select(a => a.InvoiceId), "Invoice", cancellationToken);
        return await inner.RecordAsync(request, cancellationToken);
    }

    public async Task<BillingPaymentDto> VoidAsync(Guid paymentId, VoidInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<Payment>(paymentId, "Payment", cancellationToken);
        return await inner.VoidAsync(paymentId, request, cancellationToken);
    }
}

public sealed class CompanyScopedApBillService(IApBillService inner, CompanyGuard guard) : IApBillService
{
    public async Task<IReadOnlyList<BillSummaryDto>> ListAsync(BillQuery query, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<ApBill, BillSummaryDto>(await inner.ListAsync(query, cancellationToken), b => b.Id, cancellationToken);
    }

    public async Task<BillDto> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.GetAsync(billId, cancellationToken);
    }

    public Task<IReadOnlyList<BillDuplicateDto>> CheckDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default) => inner.CheckDuplicatesAsync(request, cancellationToken);

    public async Task<BillDto> CreateAsync(SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateAsync(request, cancellationToken);
        await guard.StampNewAsync<ApBill>(result.Bill.Id, cancellationToken);
        return result;
    }

    public async Task<BillDto> UpdateAsync(Guid billId, SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.UpdateAsync(billId, request, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        await inner.DeleteDraftAsync(billId, cancellationToken);
    }

    public async Task<BillDto> SubmitAsync(Guid billId, SubmitBillRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.SubmitAsync(billId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<BillApprovalItemDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<ApBill, BillApprovalItemDto>(await inner.ListApprovalQueueAsync(cancellationToken), b => b.Bill.Id, cancellationToken);
    }

    public async Task<BillDto> ApproveAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.ApproveAsync(billId, cancellationToken);
    }

    public async Task<BillDto> RejectAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.RejectAsync(billId, request, cancellationToken);
    }

    public async Task<BillDto> PostAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.PostAsync(billId, cancellationToken);
    }

    public async Task<BillDto> VoidAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApBill>(billId, "AP bill", cancellationToken);
        return await inner.VoidAsync(billId, request, cancellationToken);
    }

    public Task<IReadOnlyList<LinkableOdcDto>> ListLinkableOdcsAsync(Guid vendorId, CancellationToken cancellationToken = default) => inner.ListLinkableOdcsAsync(vendorId, cancellationToken);

    public Task<IReadOnlyList<OdcBillLinkDto>> GetOdcLinksAsync(IReadOnlyCollection<Guid> odcEntryIds, CancellationToken cancellationToken = default) => inner.GetOdcLinksAsync(odcEntryIds, cancellationToken);
}

public sealed class CompanyScopedApPaymentService(IApPaymentService inner, CompanyGuard guard, IWorkbenchDbContextFactory dbFactory) : IApPaymentService
{
    public async Task<IReadOnlyList<DueBillDto>> ListDueBillsAsync(DueBillQuery query, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<ApBill, DueBillDto>(await inner.ListDueBillsAsync(query, cancellationToken), b => b.Bill.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentRunDto>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<ApPaymentRun, PaymentRunDto>(await inner.ListRunsAsync(cancellationToken), r => r.Id, cancellationToken);
    }

    public async Task<PaymentRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.GetRunAsync(runId, cancellationToken);
    }

    public async Task<PaymentRunDto> CreateRunAsync(CreatePaymentRunRequest request, CancellationToken cancellationToken = default)
    {
        var bills = (request.Items ?? []).Select(i => i.BillId).ToList();
        await guard.EnsureAllAsync<ApBill>(bills, "AP bill", cancellationToken);
        var company = await guard.SingleCompanyAsync<ApBill>(bills, "A payment run pays one company's bills; make one run per company.", cancellationToken);
        var result = await inner.CreateRunAsync(request, cancellationToken);
        if (company is { } payer)
        {
            await guard.SetCompanyAsync<ApPaymentRun>(result.Id, payer, cancellationToken);
        }

        return result;
    }

    public async Task<PaymentRunDto> ApproveRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.ApproveRunAsync(runId, cancellationToken);
    }

    public async Task<PaymentRunDto> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.CancelRunAsync(runId, cancellationToken);
    }

    public async Task<PaymentRunDto> RecordPaymentsAsync(Guid runId, RecordPaymentsRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.RecordPaymentsAsync(runId, request, cancellationToken);
    }

    public async Task<PaymentDto> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePaymentAsync(paymentId, cancellationToken);
        return await inner.VoidPaymentAsync(paymentId, request, cancellationToken);
    }

    public async Task<PurchasingExportFile> ExportAchAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.ExportAchAsync(runId, cancellationToken);
    }

    public async Task<PurchasingExportFile> ExportPositivePayAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<ApPaymentRun>(runId, "Payment run", cancellationToken);
        return await inner.ExportPositivePayAsync(runId, cancellationToken);
    }

    private async Task EnsurePaymentAsync(Guid paymentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var runId = await db.Set<ApPayment>().AsNoTracking().Where(p => p.Id == paymentId).Select(p => (Guid?)p.RunId).FirstOrDefaultAsync(ct);
        if (runId is { } id)
        {
            await guard.EnsureAsync<ApPaymentRun>(id, "Payment", ct);
        }
    }
}

public sealed class CompanyScopedAssetService(IAssetService inner, CompanyGuard guard, IWorkbenchDbContextFactory dbFactory) : IAssetService
{
    public async Task<IReadOnlyList<AssetSummaryDto>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<FixedAsset, AssetSummaryDto>(await inner.ListAsync(query, cancellationToken), a => a.Id, cancellationToken);
    }

    public async Task<AssetDto> GetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        return await inner.GetAsync(assetId, cancellationToken);
    }

    public Task<AssetSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => inner.GetSettingsAsync(cancellationToken);

    public async Task<AssetDto> CreateAsync(SaveAssetRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateAsync(request, cancellationToken);
        await guard.StampNewAsync<FixedAsset>(result.Asset.Id, cancellationToken);
        return result;
    }

    public async Task<AssetDto> UpdateAsync(Guid assetId, SaveAssetRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        return await inner.UpdateAsync(assetId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CapitalizableBillLineDto>> ListCapitalizableBillLinesAsync(CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<ApBill, CapitalizableBillLineDto>(await inner.ListCapitalizableBillLinesAsync(cancellationToken), l => l.BillId, cancellationToken);
    }

    public async Task<AssetDto> CapitalizeAsync(CapitalizeBillLineRequest request, CancellationToken cancellationToken = default)
    {
        var billId = await BillOfLineAsync(request.BillLineId, cancellationToken);
        if (billId is { } id)
        {
            await guard.EnsureAsync<ApBill>(id, "AP bill", cancellationToken);
        }

        var result = await inner.CapitalizeAsync(request, cancellationToken);
        if (billId is { } bill && await BillCompanyAsync(bill, cancellationToken) is { } company)
        {
            await guard.SetCompanyAsync<FixedAsset>(result.Asset.Id, company, cancellationToken);
        }

        return result;
    }

    public async Task<AssetDto> TransferAsync(Guid assetId, TransferAssetRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        if (request.CompanyId is { } target && !(await guard.ViewAsync(cancellationToken)).CanUse(target))
        {
            throw WorkbenchNotFoundException.For("Company", target);
        }

        return await inner.TransferAsync(assetId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<AssetTransferRowDto>> ListTransfersAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<FixedAsset, AssetTransferRowDto>(await inner.ListTransfersAsync(from, to, cancellationToken), t => t.AssetId, cancellationToken);
    }

    public async Task<AssetDto> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        return await inner.DisposeAsync(assetId, request, cancellationToken);
    }

    public Task<IReadOnlyList<AssetCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default) => inner.ListCategoriesAsync(cancellationToken);

    public Task<AssetCategoryDto> SaveCategoryAsync(Guid? categoryId, SaveAssetCategoryRequest request, CancellationToken cancellationToken = default) => inner.SaveCategoryAsync(categoryId, request, cancellationToken);

    public Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) => inner.DeleteCategoryAsync(categoryId, cancellationToken);

    private async Task<Guid?> BillOfLineAsync(Guid billLineId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<ApBillLine>().AsNoTracking().Where(l => l.Id == billLineId).Select(l => (Guid?)l.BillId).FirstOrDefaultAsync(ct);
    }

    /// <summary>A capitalized bill line becomes an asset of the bill's company.</summary>
    private async Task<Guid?> BillCompanyAsync(Guid billId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<ApBill>().AsNoTracking().Where(b => b.Id == billId).Select(b => (Guid?)b.CompanyId).FirstOrDefaultAsync(ct);
    }
}

public sealed class CompanyScopedBankAccountService(IBankAccountService inner, CompanyGuard guard) : IBankAccountService
{
    public async Task<IReadOnlyList<BankAccountDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        return await guard.FilterAsync<BankAccount, BankAccountDto>(await inner.ListAsync(includeInactive, cancellationToken), b => b.Id, cancellationToken);
    }

    public async Task<BankAccountDto> GetAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<BankAccount>(bankAccountId, "Bank account", cancellationToken);
        return await inner.GetAsync(bankAccountId, cancellationToken);
    }

    public async Task<BankAccountDto> CreateAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateAsync(request, cancellationToken);
        await guard.StampNewAsync<BankAccount>(result.Id, cancellationToken);
        return result;
    }

    public async Task<BankAccountDto> UpdateAsync(Guid bankAccountId, SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<BankAccount>(bankAccountId, "Bank account", cancellationToken);
        return await inner.UpdateAsync(bankAccountId, request, cancellationToken);
    }

    public async Task<BankAccountNumberDto> RevealAccountNumberAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<BankAccount>(bankAccountId, "Bank account", cancellationToken);
        return await inner.RevealAccountNumberAsync(bankAccountId, cancellationToken);
    }

    public Task<IReadOnlyList<CashGlAccountDto>> ListCashGlAccountsAsync(CancellationToken cancellationToken = default) => inner.ListCashGlAccountsAsync(cancellationToken);
}

