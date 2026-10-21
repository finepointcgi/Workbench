using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.SalesTax;

namespace Workbench.Application.Identity;

// Sales and use tax: setup, document tax and the return are Finance or Admin only.

public static class SalesTaxPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchSalesTaxPermissions(this IServiceCollection services)
    {
        services.Decorate<ISalesTaxService, PermissionCheckingSalesTaxService>();
        return services;
    }
}

public sealed class PermissionCheckingSalesTaxService(ISalesTaxService inner, AccessControl access, Companies.CompanyGuard companies) : ISalesTaxService
{
    private const string What = "manage sales and use tax";

    public async Task<IReadOnlyList<TaxJurisdictionDto>> ListJurisdictionsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ListJurisdictionsAsync(includeInactive, cancellationToken);
    }

    public async Task<TaxJurisdictionDto> SaveJurisdictionAsync(Guid? jurisdictionId, SaveTaxJurisdictionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.SaveJurisdictionAsync(jurisdictionId, request, cancellationToken);
    }

    public async Task<TaxJurisdictionDto> AddRateAsync(Guid jurisdictionId, AddTaxRateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.AddRateAsync(jurisdictionId, request, cancellationToken);
    }

    public async Task<TaxRateImportResultDto> ImportRatesAsync(ImportTaxRatesRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ImportRatesAsync(request, cancellationToken);
    }

    public async Task<TaxQuoteDto> QuoteAsync(TaxQuoteRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.QuoteAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<TaxCodeDto>> ListTaxCodesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ListTaxCodesAsync(cancellationToken);
    }

    public async Task<TaxCodeDto> SaveTaxCodeAsync(Guid? taxCodeId, SaveTaxCodeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.SaveTaxCodeAsync(taxCodeId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<TaxCodeAssignmentDto>> ListAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ListAssignmentsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaxCodeAssignmentDto>> SaveAssignmentAsync(SaveTaxCodeAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.SaveAssignmentAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerTaxDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ListCustomersAsync(cancellationToken);
    }

    public async Task<CustomerTaxDto> SaveCustomerAsync(Guid customerId, SaveCustomerTaxRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.SaveCustomerAsync(customerId, request, cancellationToken);
    }

    public async Task<CustomerTaxDto> SaveCertificateAsync(Guid customerId, Guid? certificateId, SaveTaxExemptionCertificateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.SaveCertificateAsync(customerId, certificateId, request, cancellationToken);
    }

    public async Task<SalesTaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<SalesTaxSettingsDto> SaveSettingsAsync(SaveSalesTaxSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<TaxAccountOptionDto>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ListAccountsAsync(cancellationToken);
    }

    public async Task<InvoiceTaxDto> GetInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        await companies.EnsureAsync<Domain.Billing.Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.GetInvoiceTaxAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceTaxDto> CalculateInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        await companies.EnsureAsync<Domain.Billing.Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.CalculateInvoiceTaxAsync(invoiceId, cancellationToken);
    }

    public async Task<InvoiceTaxDto> SetInvoiceTaxAsync(Guid invoiceId, SetInvoiceTaxRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        await companies.EnsureAsync<Domain.Billing.Invoice>(invoiceId, "Invoice", cancellationToken);
        return await inner.SetInvoiceTaxAsync(invoiceId, request, cancellationToken);
    }

    public async Task<BillTaxDto> GetBillTaxAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        await companies.EnsureAsync<Domain.Purchasing.ApBill>(billId, "Bill", cancellationToken);
        return await inner.GetBillTaxAsync(billId, cancellationToken);
    }

    public async Task<BillTaxDto> SetBillTaxAsync(Guid billId, SetBillTaxRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        await companies.EnsureAsync<Domain.Purchasing.ApBill>(billId, "Bill", cancellationToken);
        return await inner.SetBillTaxAsync(billId, request, cancellationToken);
    }

    public async Task<SalesTaxReturnDto> GetReturnAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.GetReturnAsync(from, to, state, cancellationToken);
    }

    public async Task<SalesTaxExportFile> ExportReturnCsvAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SalesTaxManage, What, cancellationToken);
        return await inner.ExportReturnCsvAsync(from, to, state, cancellationToken);
    }
}
