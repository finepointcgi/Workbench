namespace Workbench.Contracts.SalesTax;

public enum TaxTreatmentKind { Taxable, Exempt, Resale }

public enum TaxCodeTargetKind { InvoiceLineKind, OdcType, Account, Part }

public enum CustomerTaxState { Default, Taxable, Exempt }

// ---- Jurisdictions and rates ----

/// <summary>Rates are fractions (0.053 = 5.3%). <see cref="CurrentRate"/> is the combined rate in effect today, if any.</summary>
public sealed record TaxRateDto(Guid Id, DateOnly EffectiveDate, decimal StateRate, decimal CountyRate, decimal CityRate, decimal SpecialRate, decimal CombinedRate, string Source, string CreatedBy);

public sealed record TaxJurisdictionDto(
    Guid Id, string Code, string Name, string State, string? County, string? City, bool IsActive, decimal? CurrentRate, IReadOnlyList<TaxRateDto> Rates);

public sealed record SaveTaxJurisdictionRequest(string Code, string Name, string State, string? County = null, string? City = null, bool IsActive = true);

public sealed record AddTaxRateRequest(DateOnly EffectiveDate, decimal StateRate, decimal CountyRate = 0, decimal CityRate = 0, decimal SpecialRate = 0);

/// <summary>
/// CSV with a header row: Code, Name, State, County, City, EffectiveDate (yyyy-MM-dd), StateRate, CountyRate, CityRate,
/// SpecialRate. Rates as fractions (0.053) or percents (5.3%). Missing jurisdictions are created; a rate already on file for
/// the same date is skipped.
/// </summary>
public sealed record ImportTaxRatesRequest(string Csv);

public sealed record TaxRateImportResultDto(int JurisdictionsCreated, int RatesAdded, int RatesSkipped, IReadOnlyList<string> Errors);

// ---- Tax codes ----

public sealed record TaxCodeDto(Guid Id, string Code, string Name, TaxTreatmentKind Treatment, bool IsActive);

public sealed record SaveTaxCodeRequest(string Code, string Name, TaxTreatmentKind Treatment, bool IsActive = true);

public sealed record TaxCodeAssignmentDto(Guid Id, TaxCodeTargetKind Target, string TargetKey, string TargetLabel, Guid TaxCodeId, string TaxCode);

/// <summary>Assigns a tax code to a target; a null <see cref="TaxCodeId"/> removes the assignment.</summary>
public sealed record SaveTaxCodeAssignmentRequest(TaxCodeTargetKind Target, string TargetKey, Guid? TaxCodeId);

// ---- Customers and exemption certificates ----

public sealed record TaxExemptionCertificateDto(
    Guid Id, Guid CustomerId, string Number, string? State, string Reason, DateOnly EffectiveDate, DateOnly? ExpiresOn, bool IsRevoked, bool IsExpired, bool ExpiresSoon);

public sealed record SaveTaxExemptionCertificateRequest(string Number, string? State, string Reason, DateOnly EffectiveDate, DateOnly? ExpiresOn, bool IsRevoked = false);

public sealed record CustomerTaxDto(
    Guid CustomerId,
    string CustomerCode,
    string CustomerName,
    string? Category,
    bool IsGovernment,
    CustomerTaxState Status,
    bool IsExemptByDefault,
    Guid? ShipToJurisdictionId,
    string? ShipToJurisdiction,
    IReadOnlyList<TaxExemptionCertificateDto> Certificates);

public sealed record SaveCustomerTaxRequest(CustomerTaxState Status, Guid? ShipToJurisdictionId);

// ---- Settings ----

public sealed record SalesTaxSettingsDto(
    Guid? SalesTaxPayableAccountId,
    string? SalesTaxPayableAccount,
    Guid? UseTaxPayableAccountId,
    string? UseTaxPayableAccount,
    Guid? UseTaxExpenseAccountId,
    string? UseTaxExpenseAccount,
    Guid? HomeJurisdictionId,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId,
    IReadOnlyList<string> GovernmentCategories,
    string Engine,
    IReadOnlyList<string> AvailableEngines);

public sealed record SaveSalesTaxSettingsRequest(
    Guid? SalesTaxPayableAccountId,
    Guid? UseTaxPayableAccountId,
    Guid? UseTaxExpenseAccountId,
    Guid? HomeJurisdictionId,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId,
    IReadOnlyList<string>? GovernmentCategories = null,
    string? Engine = null);

/// <summary>An account a tax posting can use (liabilities for payables, expenses for use tax expense).</summary>
public sealed record TaxAccountOptionDto(Guid Id, string Number, string Name, string Type);

// ---- Documents ----

public sealed record InvoiceTaxDto(
    Guid InvoiceId,
    string InvoiceNumber,
    bool IsDraft,
    bool IsCalculated,
    Guid? JurisdictionId,
    string? Jurisdiction,
    Guid? JurisdictionOverrideId,
    decimal TaxableAmount,
    decimal ExemptAmount,
    decimal Rate,
    decimal TaxAmount,
    string? ExemptionReason,
    string? CertificateNumber,
    IReadOnlyList<string> Notes,
    string? Engine,
    DateTime? CalculatedAtUtc);

/// <summary>Overrides the ship-to jurisdiction on a draft invoice (null: the customer's).</summary>
public sealed record SetInvoiceTaxRequest(Guid? JurisdictionOverrideId);

public sealed record BillTaxDto(
    Guid BillId,
    string BillNumber,
    bool IsPosted,
    Guid? JurisdictionId,
    string? Jurisdiction,
    decimal VendorChargedTax,
    Guid? TaxCodeOverrideId,
    decimal TaxableAmount,
    decimal Rate,
    decimal ExpectedTax,
    decimal UseTaxAmount,
    Guid? UseTaxJournalEntryId,
    IReadOnlyList<string> Notes);

/// <summary>
/// Records the tax a vendor charged (already in the bill's lines) and where the purchase is used. When the bill is (or
/// becomes) posted and the vendor charged nothing on a taxable purchase, use tax accrues.
/// </summary>
public sealed record SetBillTaxRequest(Guid? JurisdictionId, decimal VendorChargedTax, Guid? TaxCodeOverrideId = null);

// ---- Return ----

public sealed record SalesTaxReturnRowDto(
    Guid JurisdictionId,
    string Code,
    string Name,
    string State,
    decimal GrossSales,
    decimal ExemptSales,
    decimal TaxableSales,
    decimal SalesTax,
    decimal StateTax,
    decimal CountyTax,
    decimal CityTax,
    decimal SpecialTax,
    decimal TaxablePurchases,
    decimal UseTax,
    decimal VendorChargedTax,
    int Invoices,
    int Bills)
{
    public decimal TotalDue => SalesTax + UseTax;
}

public sealed record SalesTaxReturnDto(DateOnly From, DateOnly To, string? State, IReadOnlyList<SalesTaxReturnRowDto> Rows)
{
    public decimal TotalSalesTax => Rows.Sum(r => r.SalesTax);
    public decimal TotalUseTax => Rows.Sum(r => r.UseTax);
    public decimal TotalDue => Rows.Sum(r => r.TotalDue);
}

public sealed record SalesTaxExportFile(string FileName, string ContentType, string Content);

/// <summary>A tax quote, for checking a rate (no document).</summary>
public sealed record TaxQuoteRequest(Guid JurisdictionId, DateOnly Date, decimal TaxableAmount);

public sealed record TaxQuoteDto(Guid JurisdictionId, string Jurisdiction, DateOnly Date, decimal TaxableAmount, decimal Rate, decimal Tax, decimal StateTax, decimal CountyTax, decimal CityTax, decimal SpecialTax, string Engine);

/// <summary>
/// Sales and use tax: jurisdictions with effective-dated rates (CSV import), tax codes, customer exemptions, invoice sales tax
/// by ship-to, AP vendor-charged and use tax, and the return by jurisdiction. Reads and changes are Finance or Admin.
/// </summary>
public interface ISalesTaxService
{
    Task<IReadOnlyList<TaxJurisdictionDto>> ListJurisdictionsAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<TaxJurisdictionDto> SaveJurisdictionAsync(Guid? jurisdictionId, SaveTaxJurisdictionRequest request, CancellationToken cancellationToken = default);

    Task<TaxJurisdictionDto> AddRateAsync(Guid jurisdictionId, AddTaxRateRequest request, CancellationToken cancellationToken = default);

    Task<TaxRateImportResultDto> ImportRatesAsync(ImportTaxRatesRequest request, CancellationToken cancellationToken = default);

    Task<TaxQuoteDto> QuoteAsync(TaxQuoteRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxCodeDto>> ListTaxCodesAsync(CancellationToken cancellationToken = default);

    Task<TaxCodeDto> SaveTaxCodeAsync(Guid? taxCodeId, SaveTaxCodeRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxCodeAssignmentDto>> ListAssignmentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxCodeAssignmentDto>> SaveAssignmentAsync(SaveTaxCodeAssignmentRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerTaxDto>> ListCustomersAsync(CancellationToken cancellationToken = default);

    Task<CustomerTaxDto> SaveCustomerAsync(Guid customerId, SaveCustomerTaxRequest request, CancellationToken cancellationToken = default);

    Task<CustomerTaxDto> SaveCertificateAsync(Guid customerId, Guid? certificateId, SaveTaxExemptionCertificateRequest request, CancellationToken cancellationToken = default);

    Task<SalesTaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<SalesTaxSettingsDto> SaveSettingsAsync(SaveSalesTaxSettingsRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxAccountOptionDto>> ListAccountsAsync(CancellationToken cancellationToken = default);

    Task<InvoiceTaxDto> GetInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Works out a draft invoice's tax now and puts it on the invoice as a sales tax line (approval does this again).</summary>
    Task<InvoiceTaxDto> CalculateInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<InvoiceTaxDto> SetInvoiceTaxAsync(Guid invoiceId, SetInvoiceTaxRequest request, CancellationToken cancellationToken = default);

    Task<BillTaxDto> GetBillTaxAsync(Guid billId, CancellationToken cancellationToken = default);

    Task<BillTaxDto> SetBillTaxAsync(Guid billId, SetBillTaxRequest request, CancellationToken cancellationToken = default);

    Task<SalesTaxReturnDto> GetReturnAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default);

    Task<SalesTaxExportFile> ExportReturnCsvAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default);
}
